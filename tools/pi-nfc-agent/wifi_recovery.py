#!/usr/bin/env python3
"""WLAN recovery for the kiosk terminal, independent of the NFC agent.

After a failed WPA handshake NetworkManager asks for new secrets. Without a
desktop secret agent nobody answers, the activation ends with "no-secrets" and
NetworkManager stops retrying - the terminal stays offline until a reboot. An
explicit "nmcli connection up" uses the system-stored secrets again.

Runs as root from stempeluhr-wifi-recovery.timer, one check per run. Only a
WLAN device that has been disconnected for disconnect_grace_seconds without a
running connection attempt is touched: the saved autoconnect profile is
activated with backoff, after repeated failures the WLAN radio is switched off
and on once (cooldown). A connected WLAN is never touched - an unreachable
server, Kimai, DNS, internet or Tailscale is no reason for a reset. A disabled
radio, an unmanaged device or a connected Ethernet link are left alone.

Optional settings in config.json (all keys may be missing):
  "wifi_recovery": {"enabled": true, "interface": "", "connection_uuid": "",
                    "disconnect_grace_seconds": 120, "radio_reset": true}

State lives in /run (cleared on reboot) and uses the monotonic clock, which
is shared between runs of one boot and unaffected by NTP jumps after a
reconnect. Never reads or logs secrets (no --show-secrets, no --ask).
"""
from __future__ import annotations

import argparse
import fcntl
import json
import os
from dataclasses import dataclass
from pathlib import Path
import subprocess
import sys
import time
from typing import Callable, List, Optional, Tuple

CONFIG = Path(os.environ.get("STEMPELUHR_AGENT_CONFIG", "/etc/stempeluhr-nfc-agent/config.json"))
STATE_DIR = Path(os.environ.get("STEMPELUHR_WIFI_STATE", "/run/stempeluhr-wifi-recovery"))
WIFI_TYPE = "802-11-wireless"
# A running NetworkManager attempt (incl. need-auth, which ends after ~2 min)
# is only interrupted once it hangs longer than this.
CONNECTING_LIMIT_SECONDS = 180
ACTIVATION_WAIT_SECONDS = 30
ACTIVATION_TIMEOUT_SECONDS = 45
READ_TIMEOUT_SECONDS = 15
BACKOFF_BASE_SECONDS = 60
BACKOFF_MAX_SECONDS = 15 * 60
RADIO_RESET_AFTER_ATTEMPTS = 3
RADIO_RESET_COOLDOWN_SECONDS = 30 * 60
RADIO_OFF_SECONDS = 5
MIN_GRACE_SECONDS = 30

# Decisions of one run.
NONE = "none"            # WLAN connected
WAIT = "wait"            # disconnected, but not yet / not again
SKIP = "skip"            # nothing to recover (deliberately off, Ethernet, ...)
ACTIVATE = "activate"
RADIO_RESET = "radio-reset"
RESTORE_RADIO = "restore-radio"


@dataclass
class Settings:
    enabled: bool = True
    interface: str = ""
    connection_uuid: str = ""
    disconnect_grace_seconds: float = 120
    radio_reset: bool = True


@dataclass
class Observation:
    nmcli_available: bool = True
    nm_running: bool = True
    radio_enabled: bool = True
    device: Optional[str] = None
    # connected, connecting, disconnected, unavailable, unmanaged or unknown
    device_state: str = "disconnected"
    state_detail: str = ""
    ethernet_connected: bool = False
    profile: Optional[str] = None
    profile_problem: str = ""


@dataclass
class Decision:
    action: str
    reason: str = ""


@dataclass
class CommandResult:
    returncode: Optional[int]  # None: timed out
    stdout: str = ""
    stderr: str = ""


Runner = Callable[[List[str], float], CommandResult]


def run_nmcli(args: List[str], timeout: float) -> CommandResult:
    """nmcli without a terminal and in the C locale (states are parsed)."""
    try:
        completed = subprocess.run(["nmcli", *args], stdin=subprocess.DEVNULL, capture_output=True,
                                   text=True, timeout=timeout, env={**os.environ, "LC_ALL": "C"})
    except subprocess.TimeoutExpired:
        return CommandResult(None)
    return CommandResult(completed.returncode, completed.stdout, completed.stderr)


def load_settings(path: Path) -> Settings:
    """Defaults for a missing/broken config: recovery must not depend on it."""
    try:
        raw = json.loads(path.read_text(encoding="utf-8")).get("wifi_recovery") or {}
    except (OSError, ValueError, AttributeError):
        raw = {}
    if not isinstance(raw, dict):
        raw = {}
    settings = Settings()
    if isinstance(raw.get("enabled"), bool):
        settings.enabled = raw["enabled"]
    if isinstance(raw.get("radio_reset"), bool):
        settings.radio_reset = raw["radio_reset"]
    for key in ("interface", "connection_uuid"):
        if isinstance(raw.get(key), str):
            setattr(settings, key, raw[key].strip())
    grace = raw.get("disconnect_grace_seconds")
    if isinstance(grace, (int, float)) and not isinstance(grace, bool):
        settings.disconnect_grace_seconds = max(float(grace), MIN_GRACE_SECONDS)
    return settings


def split_terse(line: str) -> List[str]:
    """Fields of an `nmcli -t` line; values escape ':' and '\\' with '\\'."""
    fields, current, escaped = [], [], False
    for char in line:
        if escaped:
            current.append(char)
            escaped = False
        elif char == "\\":
            escaped = True
        elif char == ":":
            fields.append("".join(current))
            current = []
        else:
            current.append(char)
    fields.append("".join(current))
    return fields


def device_category(state: str) -> str:
    state = state.strip().lower()
    if state.startswith("connected"):
        return "connected"
    if state.startswith(("connecting", "deactivating")):
        return "connecting"
    if state in ("disconnected", "unavailable", "unmanaged"):
        return state
    return "unknown"


def select_profile(profiles: List[dict], interface: str, preferred_uuid: str) -> Tuple[Optional[str], str]:
    """(uuid, problem) of the WLAN profile to activate on interface.

    Without a configured UUID: autoconnect profiles bound to no or this
    interface, highest priority first, then the last successful one.
    """
    wifi = [p for p in profiles if p["type"] == WIFI_TYPE]
    if preferred_uuid:
        if any(p["uuid"] == preferred_uuid for p in wifi):
            return preferred_uuid, ""
        return None, f"konfiguriertes WLAN-Profil {preferred_uuid} fehlt"
    candidates = [p for p in wifi if p["autoconnect"] and p.get("interface_name", "") in ("", interface)]
    if not candidates:
        return None, "kein WLAN-Profil mit Autoconnect für dieses Gerät"
    best = max(candidates, key=lambda p: (p["priority"], p["timestamp"]))
    return best["uuid"], ""


def as_int(value: str) -> int:
    try:
        return int(value)
    except ValueError:
        return 0


def observe(nmcli: Runner, settings: Settings) -> Observation:
    observation = Observation()
    try:
        general = nmcli(["-t", "-f", "RUNNING", "general"], READ_TIMEOUT_SECONDS)
    except FileNotFoundError:
        observation.nmcli_available = False
        return observation
    if general.returncode != 0 or general.stdout.strip() != "running":
        observation.nm_running = False
        return observation
    radio = nmcli(["radio", "wifi"], READ_TIMEOUT_SECONDS)
    observation.radio_enabled = radio.returncode == 0 and radio.stdout.strip() == "enabled"

    status = nmcli(["-t", "-f", "DEVICE,TYPE,STATE", "device", "status"], READ_TIMEOUT_SECONDS)
    if status.returncode != 0:
        observation.nm_running = False
        return observation
    for line in status.stdout.splitlines():
        fields = split_terse(line)
        if len(fields) < 3:
            continue
        name, kind, state = fields[:3]
        if kind == "ethernet" and device_category(state) == "connected":
            observation.ethernet_connected = True
        if kind == "wifi" and observation.device is None and settings.interface in ("", name):
            observation.device = name
            observation.device_state = device_category(state)
            observation.state_detail = state
    if observation.device is None or observation.device_state == "connected":
        return observation

    details = nmcli(["-t", "-f", "GENERAL.STATE,GENERAL.REASON", "device", "show", observation.device],
                    READ_TIMEOUT_SECONDS)
    if details.returncode == 0:
        values = dict(line.split(":", 1) for line in details.stdout.splitlines() if ":" in line)
        observation.state_detail = "; ".join(
            f"{label} {values[key]}" for key, label in (("GENERAL.STATE", "Zustand"), ("GENERAL.REASON", "Grund"))
            if values.get(key)) or observation.state_detail

    connections = nmcli(["-t", "-f", "UUID,TYPE,AUTOCONNECT,AUTOCONNECT-PRIORITY,TIMESTAMP", "connection", "show"],
                        READ_TIMEOUT_SECONDS)
    profiles = []
    if connections.returncode == 0:
        for line in connections.stdout.splitlines():
            fields = split_terse(line)
            if len(fields) >= 5:
                profiles.append({"uuid": fields[0], "type": fields[1], "autoconnect": fields[2] == "yes",
                                 "priority": as_int(fields[3]), "timestamp": as_int(fields[4])})
    for profile in profiles:
        if profile["type"] == WIFI_TYPE and (profile["autoconnect"] or profile["uuid"] == settings.connection_uuid):
            bound = nmcli(["-g", "connection.interface-name", "connection", "show", "uuid", profile["uuid"]],
                          READ_TIMEOUT_SECONDS)
            profile["interface_name"] = bound.stdout.strip() if bound.returncode == 0 else ""
    observation.profile, observation.profile_problem = select_profile(
        profiles, observation.device, settings.connection_uuid)
    return observation


def backoff(attempts: int) -> float:
    return min(BACKOFF_BASE_SECONDS * 2 ** max(attempts - 1, 0), BACKOFF_MAX_SECONDS)


def decide(observation: Observation, state: dict, now: float, settings: Settings) -> Decision:
    """Pure decision for one run; state already holds this run's timestamps."""
    if not settings.enabled:
        return Decision(SKIP, "per config.json abgeschaltet")
    if not observation.nmcli_available:
        return Decision(SKIP, "nmcli nicht installiert")
    if not observation.nm_running:
        return Decision(SKIP, "NetworkManager läuft nicht")
    if not observation.radio_enabled:
        if state.get("radio_reset_pending"):
            return Decision(RESTORE_RADIO, "eigener Funk-Reset wurde unterbrochen")
        return Decision(SKIP, "WLAN-Funk ist ausgeschaltet")
    if observation.device is None:
        return Decision(SKIP, f"kein WLAN-Gerät {settings.interface}".rstrip())
    if observation.device_state == "connected":
        return Decision(NONE)
    if observation.device_state in ("unavailable", "unmanaged", "unknown"):
        return Decision(SKIP, f"WLAN-Gerät {observation.device} ist {observation.state_detail or observation.device_state}")
    if observation.ethernet_connected:
        return Decision(SKIP, "Ethernet verbunden")
    if observation.profile is None:
        return Decision(SKIP, observation.profile_problem)

    if now - state.get("disconnected_since", now) < settings.disconnect_grace_seconds:
        return Decision(WAIT, "kurze Trennung")
    connecting_since = state.get("connecting_since")
    if connecting_since is not None and now - connecting_since < CONNECTING_LIMIT_SECONDS:
        return Decision(WAIT, "NetworkManager verbindet gerade")
    if now < state.get("next_attempt_at", 0):
        return Decision(WAIT, "Backoff")
    last_reset = state.get("last_radio_reset_at")
    if (settings.radio_reset and state.get("attempts", 0) >= RADIO_RESET_AFTER_ATTEMPTS
            and (last_reset is None or now - last_reset >= RADIO_RESET_COOLDOWN_SECONDS)):
        return Decision(RADIO_RESET, f"{state['attempts']} Aktivierungen erfolglos")
    return Decision(ACTIVATE)


def short_error(result: CommandResult) -> str:
    if result.returncode is None:
        return "Zeitüberschreitung"
    lines = [line.strip() for line in (result.stderr or result.stdout).splitlines() if line.strip()]
    return (lines[-1] if lines else "keine Meldung")[:200]


class Recovery:
    def __init__(self, nmcli: Runner, clock: Callable[[], float] = time.monotonic,
                 sleep: Callable[[float], None] = time.sleep, log: Callable[[str], None] = print,
                 persist: Callable[[dict], None] = lambda state: None):
        self.nmcli, self.clock, self.sleep, self.log, self.persist = nmcli, clock, sleep, log, persist

    def tick(self, state: dict, settings: Settings) -> Decision:
        """One check; updates state in place and returns what was decided."""
        now = self.clock()
        observation = observe(self.nmcli, settings)
        if observation.nmcli_available and observation.nm_running and observation.radio_enabled:
            # Radio is on: a later deliberate switch-off must stay off.
            state["radio_reset_pending"] = False
        new_outage = False
        if observation.device and observation.device_state in ("disconnected", "connecting"):
            if state.get("disconnected_since") is None:
                state["disconnected_since"] = now
                new_outage = True
            if observation.device_state == "connecting":
                state["connecting_since"] = state.get("connecting_since") or now
            else:
                state["connecting_since"] = None

        decision = decide(observation, state, now, settings)
        if decision.action == NONE:
            if state.get("disconnected_since") is not None:
                self.log(f"WLAN {observation.device} wieder verbunden nach "
                         f"{now - state['disconnected_since']:.0f} s.")
            self.end_episode(state)
        elif decision.action == SKIP:
            # Deliberate states end an outage; recovery starts over afterwards.
            self.end_episode(state)
            if state.get("skip_reason") != decision.reason:
                self.log(f"Keine WLAN-Recovery: {decision.reason}.")
        elif new_outage:
            self.log(f"WLAN {observation.device} nicht verbunden ({observation.state_detail}).")
        state["skip_reason"] = decision.reason if decision.action == SKIP else None

        if decision.action == ACTIVATE:
            self.activate(state, observation)
        elif decision.action == RADIO_RESET:
            self.reset_radio(state, decision.reason)
        elif decision.action == RESTORE_RADIO:
            self.restore_radio(state)
        return decision

    @staticmethod
    def end_episode(state: dict) -> None:
        for key in ("disconnected_since", "connecting_since", "attempts", "next_attempt_at", "reloaded"):
            state.pop(key, None)

    def activate(self, state: dict, observation: Observation) -> None:
        if not state.get("reloaded"):
            # Re-reads the stored profiles (secrets included) without deleting anything.
            reload = self.nmcli(["connection", "reload"], READ_TIMEOUT_SECONDS)
            if reload.returncode != 0:
                self.log(f"WARNUNG: nmcli connection reload fehlgeschlagen: {short_error(reload)}")
            state["reloaded"] = True
        state["attempts"] = state.get("attempts", 0) + 1
        self.log(f"WLAN-Recovery Versuch {state['attempts']}: aktiviere Profil {observation.profile} "
                 f"auf {observation.device} ({observation.state_detail}).")
        result = self.nmcli(["--wait", str(ACTIVATION_WAIT_SECONDS), "connection", "up", "uuid",
                             observation.profile, "ifname", observation.device], ACTIVATION_TIMEOUT_SECONDS)
        delay = backoff(state["attempts"])
        state["next_attempt_at"] = self.clock() + delay
        if result.returncode == 0:
            self.log(f"WLAN-Recovery Versuch {state['attempts']}: Profil aktiviert.")
        else:
            code = "Timeout" if result.returncode is None else f"Exitcode {result.returncode}"
            self.log(f"WLAN-Recovery Versuch {state['attempts']} fehlgeschlagen ({code}): {short_error(result)}; "
                     f"nächster Versuch frühestens in {delay:.0f} s.")

    def reset_radio(self, state: dict, reason: str) -> None:
        self.log(f"WLAN-Recovery: {reason} - schalte WLAN-Funk aus und wieder ein.")
        # Persisted before switching off: a killed run must not leave the radio
        # off, which the next run would otherwise take as deliberate.
        state["radio_reset_pending"] = True
        state["last_radio_reset_at"] = self.clock()
        self.persist(state)
        try:
            off = self.nmcli(["radio", "wifi", "off"], READ_TIMEOUT_SECONDS)
            if off.returncode != 0:
                self.log(f"WARNUNG: WLAN-Funk ließ sich nicht ausschalten: {short_error(off)}")
            else:
                self.sleep(RADIO_OFF_SECONDS)
        finally:
            self.restore_radio(state)
        # Give NetworkManager's own autoconnect a chance before the next activation.
        state["next_attempt_at"] = self.clock() + BACKOFF_BASE_SECONDS

    def restore_radio(self, state: dict) -> None:
        on = self.nmcli(["radio", "wifi", "on"], READ_TIMEOUT_SECONDS)
        if on.returncode == 0:
            state["radio_reset_pending"] = False
            self.log("WLAN-Funk wieder eingeschaltet.")
        else:
            self.log(f"FEHLER: WLAN-Funk ließ sich nicht einschalten: {short_error(on)}; nächster Versuch beim nächsten Lauf.")


def load_state() -> dict:
    try:
        state = json.loads((STATE_DIR / "state.json").read_text())
        return state if isinstance(state, dict) else {}
    except (OSError, ValueError):
        return {}


def save_state(state: dict) -> None:
    temporary = STATE_DIR / "state.tmp"
    temporary.write_text(json.dumps(state) + "\n")
    temporary.replace(STATE_DIR / "state.json")


def main(argv: Optional[List[str]] = None, nmcli: Runner = run_nmcli) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    parser.add_argument("--config", type=Path, default=CONFIG)
    args = parser.parse_args(argv)
    STATE_DIR.mkdir(parents=True, exist_ok=True, mode=0o700)
    with (STATE_DIR / "lock").open("a") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            return 0  # another run is active
        state = load_state()
        try:
            Recovery(nmcli, persist=save_state).tick(state, load_settings(args.config))
        finally:
            save_state(state)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:  # noqa: BLE001 - one journal line instead of a traceback
        print(f"WLAN-Recovery fehlgeschlagen: {type(error).__name__}: {error}", file=sys.stderr)
        sys.exit(1)
