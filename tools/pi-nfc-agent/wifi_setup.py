#!/usr/bin/env python3
"""WLAN setup for the kiosk terminal: root helper of the NFC agent.

Two jobs, both as root:

serve        One request of the NFC agent per connection on
             /run/stempeluhr-wifi-setup.sock (stempeluhr-wifi-setup.socket,
             Accept=yes): a JSON line on stdin, a JSON line on stdout.
             Commands: status, scan, connect. The agent itself stays
             unprivileged (NoNewPrivileges); only members of the group
             stempeluhr may connect to the socket.
sync-service Called by update.sh: fetches the maintenance settings of this
             terminal from the server (/api/kiosk/maintenance, terminal
             token) and keeps the maintenance WLAN profile and the hash of the
             maintenance PIN in line with them.

Profiles are written as NetworkManager keyfiles (root, 0600) and loaded with
"nmcli connection load": a password never appears in a command line, a log
line or an answer. "connect" adds the new customer profile next to the old
one and removes the old one only once the new one is connected; otherwise the
new one is deleted and the previous connection is activated again. Only the
profiles stempeluhr-wlan (customer WLAN, priority 10) and stempeluhr-service
(maintenance WLAN, priority -10) are ever changed.
"""
from __future__ import annotations

import argparse
import fcntl
import grp
import json
import os
from pathlib import Path
import re
import sys
import time
from typing import Callable, List, Optional
import urllib.error
import urllib.request
import uuid as uuidlib

from maintenance_access import HIDDEN_SECURITY, PIN_HASH, STARTED_LINE
from wifi_recovery import STATE_DIR, CommandResult, Runner, device_category, run_nmcli, short_error, split_terse

CONFIG = Path(os.environ.get("STEMPELUHR_AGENT_CONFIG", "/etc/stempeluhr-nfc-agent/config.json"))
CONNECTIONS_DIR = Path(os.environ.get("STEMPELUHR_NM_CONNECTIONS", "/etc/NetworkManager/system-connections"))
CONNECT_LOCK = Path(os.environ.get("STEMPELUHR_WIFI_SETUP_LOCK", "/run/stempeluhr-wifi-setup.lock"))
SERVICE_GROUP = "stempeluhr"
MAINTENANCE_FILE_NAME = "maintenance.json"

CUSTOMER_ID = "stempeluhr-wlan"
CUSTOMER_PRIORITY = 10
SERVICE_ID = "stempeluhr-service"
SERVICE_PRIORITY = -10

WIFI_TYPE = "802-11-wireless"
READ_TIMEOUT_SECONDS = 15
SCAN_TIMEOUT_SECONDS = 25
CONNECT_WAIT_SECONDS = 40
CONNECT_TIMEOUT_SECONDS = 55
ROLLBACK_WAIT_SECONDS = 30
ROLLBACK_TIMEOUT_SECONDS = 45
LOCK_WAIT_SECONDS = 50
# Upper bound of one "connect" request, every nmcli call at its timeout: lock,
# scan with fallback, activation, rollback and at most nine short calls (device,
# profiles, load, one delete for all old profiles, status). The agent waits longer
# (maintenance_access.CONNECT_TIMEOUT_SECONDS), systemd longer still
# (RuntimeMaxSec), so neither cuts off a rollback; test_wifi_setup checks it.
CONNECT_WORST_CASE_SECONDS = (LOCK_WAIT_SECONDS + SCAN_TIMEOUT_SECONDS + CONNECT_TIMEOUT_SECONDS
                              + ROLLBACK_TIMEOUT_SECONDS + 9 * READ_TIMEOUT_SECONDS)
MAX_REQUEST_BYTES = 4096
MAX_NETWORKS = 30
SERVER_TIMEOUT_SECONDS = 15


class SetupError(Exception):
    """A message for the person at the terminal (German, no secrets)."""


# --- validation and keyfiles -------------------------------------------------

def validate_ssid(ssid: object) -> str:
    if not isinstance(ssid, str) or not ssid or len(ssid.encode("utf-8")) > 32 \
            or any(ord(char) < 32 or ord(char) == 127 for char in ssid):
        raise SetupError("Ungültiger WLAN-Name (1 bis 32 Byte).")
    return ssid


def validate_psk(psk: object) -> str:
    """'' for an open network, else a WPA passphrase or a 64-digit hex key."""
    if psk == "":
        return ""
    if isinstance(psk, str) and (8 <= len(psk) <= 63 and all(" " <= char <= "~" for char in psk)
                                 or len(psk) == 64 and all(char in "0123456789abcdefABCDEF" for char in psk)):
        return psk
    raise SetupError("Das WLAN-Passwort braucht 8 bis 63 Zeichen.")


def key_management(security: str, psk: str) -> Optional[str]:
    """NetworkManager key-mgmt for a network; None for an open one.

    Checks the password against the encryption for every network, scanned or
    hidden: a wrong combination would give a profile that never connects.
    """
    words = set(security.upper().split())
    if "802.1X" in words:
        raise SetupError("WLANs mit Benutzername (802.1X) werden nicht unterstützt.")
    if "WEP" in words:
        raise SetupError("WEP-verschlüsselte WLANs werden nicht unterstützt.")
    if words == {"OWE"}:
        # Enhanced Open: encrypted, but without a password.
        if psk:
            raise SetupError("Dieses WLAN hat kein Passwort.")
        return "owe"
    if not words:
        if psk:
            raise SetupError("Ein offenes WLAN hat kein Passwort.")
        return None
    if not psk:
        raise SetupError("Für dieses WLAN fehlt das Passwort.")
    if "WPA3" in words and not {"WPA1", "WPA2"} & words:
        return "sae"
    return "wpa-psk"


def escape_value(value: str) -> str:
    """GKeyFile escaping; every space as \\s, so none is trimmed."""
    return (value.replace("\\", "\\\\").replace(" ", "\\s").replace("\t", "\\t")
            .replace("\n", "\\n").replace("\r", "\\r"))


def keyfile(connection_id: str, connection_uuid: str, ssid: str, psk: str, key_mgmt: Optional[str],
            priority: int, hidden: bool = False) -> str:
    # The SSID as decimal bytes: unambiguous for any name (see nm-settings-keyfile).
    ssid_bytes = "".join(f"{byte};" for byte in ssid.encode("utf-8"))
    lines = [
        "[connection]", f"id={connection_id}", f"uuid={connection_uuid}", "type=wifi",
        "autoconnect=true", f"autoconnect-priority={priority}", "",
        "[wifi]", "mode=infrastructure", f"ssid={ssid_bytes}",
    ]
    if hidden:
        lines.append("hidden=true")
    lines.append("")
    if key_mgmt:
        lines += ["[wifi-security]", f"key-mgmt={key_mgmt}"]
        if psk:
            lines.append(f"psk={escape_value(psk)}")
        lines.append("")
    lines += ["[ipv4]", "method=auto", "", "[ipv6]", "addr-gen-mode=default", "method=auto", ""]
    return "\n".join(lines)


def write_private(path: Path, content: str, mode: int = 0o600, group: Optional[int] = None) -> None:
    """Atomic replace; the mode is set before any content is written."""
    temporary = path.with_name(f".{path.name}.tmp")
    descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, mode)
    try:
        os.fchmod(descriptor, mode)
        if group is not None:
            os.fchown(descriptor, 0, group)
        with os.fdopen(descriptor, "w", encoding="utf-8") as handle:
            descriptor = -1
            handle.write(content)
            handle.flush()
            os.fsync(handle.fileno())
    finally:
        if descriptor >= 0:
            os.close(descriptor)
    os.replace(temporary, path)


def read_text(path: Path) -> Optional[str]:
    try:
        return path.read_text(encoding="utf-8")
    except OSError:
        return None


def uuid_of(content: Optional[str]) -> Optional[str]:
    match = re.search(r"^uuid=([0-9a-f-]{36})$", content or "", re.MULTILINE)
    return match.group(1) if match else None


# --- NetworkManager ------------------------------------------------------------

class NetworkManager:
    def __init__(self, nmcli: Runner = run_nmcli, connections_dir: Path = CONNECTIONS_DIR,
                 log: Callable[[str], None] = lambda line: print(line, file=sys.stderr)):
        self.nmcli, self.connections_dir, self.log = nmcli, connections_dir, log

    def run(self, args: List[str], timeout: float = READ_TIMEOUT_SECONDS) -> CommandResult:
        try:
            return self.nmcli(args, timeout)
        except FileNotFoundError:
            raise SetupError("NetworkManager (nmcli) ist nicht installiert.") from None

    def wifi_device(self) -> Optional[dict]:
        result = self.run(["-t", "-f", "DEVICE,TYPE,STATE,CONNECTION", "device", "status"])
        if result.returncode != 0:
            raise SetupError("NetworkManager antwortet nicht.")
        devices = [split_terse(line) for line in result.stdout.splitlines()]
        wifi = [fields for fields in devices if len(fields) >= 4 and fields[1] == "wifi"]
        ethernet = any(len(fields) >= 3 and fields[1] == "ethernet" and device_category(fields[2]) == "connected"
                       for fields in devices)
        if not wifi:
            return None
        name, _, state, connection = wifi[0][:4]
        return {"device": name, "state": device_category(state), "connection": connection or None,
                "ethernet": ethernet}

    def profiles(self, active: bool = False) -> List[dict]:
        args = ["-t", "-f", "NAME,UUID,TYPE,DEVICE", "connection", "show"] + (["--active"] if active else [])
        result = self.run(args)
        profiles = []
        for line in result.stdout.splitlines() if result.returncode == 0 else []:
            fields = split_terse(line)
            if len(fields) >= 4 and fields[2] == WIFI_TYPE:
                profiles.append({"name": fields[0], "uuid": fields[1], "device": fields[3]})
        return profiles

    def delete(self, *connection_uuids: str) -> None:
        """Deletes the profiles in one nmcli call (one timeout, however many)."""
        args = ["connection", "delete"]
        for connection_uuid in connection_uuids:
            args += ["uuid", connection_uuid]
        result = self.run(args)
        if result.returncode != 0:
            self.log(f"WARNUNG: WLAN-Profil {', '.join(connection_uuids)} ließ sich nicht löschen: {short_error(result)}")

    def load(self, path: Path) -> None:
        result = self.run(["connection", "load", str(path)])
        if result.returncode != 0:
            raise SetupError(f"NetworkManager übernimmt das WLAN-Profil nicht: {short_error(result)}")

    def write_profile(self, file_name: str, content: str) -> Path:
        if not self.connections_dir.is_dir():
            raise SetupError("NetworkManager-Profilordner fehlt.")
        path = self.connections_dir / file_name
        write_private(path, content)
        return path

    # -- commands of the agent --

    def status(self) -> dict:
        device = self.wifi_device()
        if device is None:
            return {"device": None, "state": "unavailable", "ssid": None, "ip": None,
                    "connection": None, "serviceWifi": False, "ethernet": False}
        ssid = ip = None
        if device["state"] == "connected":
            details = self.run(["-g", "IP4.ADDRESS", "device", "show", device["device"]])
            if details.returncode == 0 and details.stdout.strip():
                ip = details.stdout.strip().split(" | ")[0].split("/")[0] or None
            active = self.run(["-t", "-f", "ACTIVE,SSID", "device", "wifi", "list", "--rescan", "no"])
            for line in active.stdout.splitlines() if active.returncode == 0 else []:
                fields = split_terse(line)
                if len(fields) >= 2 and fields[0] == "yes":
                    ssid = fields[1] or None
                    break
        return {"device": device["device"], "state": device["state"], "ssid": ssid, "ip": ip,
                "connection": device["connection"], "serviceWifi": device["connection"] == SERVICE_ID,
                "ethernet": device["ethernet"]}

    def scan(self) -> List[dict]:
        fields = ["-t", "-f", "IN-USE,SSID,SIGNAL,SECURITY", "device", "wifi", "list", "--rescan"]
        result = self.run(fields + ["yes"], SCAN_TIMEOUT_SECONDS)
        if result.returncode != 0:
            # e.g. "Scanning not allowed while already scanning": the last result.
            result = self.run(fields + ["no"])
        if result.returncode != 0:
            raise SetupError(f"WLAN-Suche fehlgeschlagen: {short_error(result)}")
        networks: dict = {}
        for line in result.stdout.splitlines():
            parts = split_terse(line)
            if len(parts) < 4 or not parts[1]:
                continue  # hidden networks have no name in the list
            in_use, ssid, signal, security = parts[0] == "*", parts[1], parts[2], parts[3].strip()
            entry = {"ssid": ssid, "signal": int(signal) if signal.isdigit() else 0,
                     "security": "" if security in ("", "--") else security, "inUse": in_use}
            known = networks.get(ssid)
            if known is None or entry["signal"] > known["signal"]:
                entry["inUse"] = in_use or bool(known and known["inUse"])
                networks[ssid] = entry
            elif in_use:
                known["inUse"] = True
        ordered = sorted(networks.values(), key=lambda entry: (not entry["inUse"], -entry["signal"], entry["ssid"]))
        return ordered[:MAX_NETWORKS]

    def connect(self, ssid: str, psk: str, hidden: bool = False, security: Optional[str] = None,
                on_connected: Callable[[str], None] = lambda _uuid: None) -> Optional[dict]:
        """security: only for hidden networks ('WPA2', 'WPA3' or '' for open), else from the scan.

        Returns the status after connecting; None if it cannot be read - the
        connect itself succeeded then. on_connected gets the new profile's uuid.
        """
        ssid, psk = validate_ssid(ssid), validate_psk(psk)
        device = self.wifi_device()
        if device is None:
            raise SetupError("Kein WLAN-Gerät gefunden.")
        if hidden:
            security = ("WPA2" if psk else "") if security is None else security
        else:
            match = next((network for network in self.scan() if network["ssid"] == ssid), None)
            if match is None:
                raise SetupError("WLAN nicht gefunden. Ist es in Reichweite?")
            security = match["security"]
        key_mgmt = key_management(security, psk)

        previous = next((profile for profile in self.profiles(active=True)
                         if profile["device"] == device["device"]), None)
        old_customer = [profile["uuid"] for profile in self.profiles() if profile["name"] == CUSTOMER_ID]
        new_uuid = str(uuidlib.uuid4())
        path = self.write_profile(f"{CUSTOMER_ID}-{new_uuid[:8]}.nmconnection",
                                  keyfile(CUSTOMER_ID, new_uuid, ssid, psk, key_mgmt, CUSTOMER_PRIORITY, hidden))
        try:
            self.load(path)
        except SetupError:
            path.unlink(missing_ok=True)
            raise
        self.log(f"WLAN-Einstellung: verbinde mit '{ssid}' auf {device['device']}.")
        result = self.run(["--wait", str(CONNECT_WAIT_SECONDS), "connection", "up", "uuid", new_uuid,
                           "ifname", device["device"]], CONNECT_TIMEOUT_SECONDS)
        if result.returncode == 0:
            # One call for all old profiles: the time bound must not depend on
            # how many earlier deletions failed.
            if old_customer:
                self.delete(*old_customer)
            self.log(f"WLAN-Einstellung: mit '{ssid}' verbunden.")
            try:
                on_connected(new_uuid)
            except Exception as error:  # noqa: BLE001 - the connect itself succeeded
                self.log(f"WARNUNG: Nacharbeit nach dem WLAN-Wechsel fehlgeschlagen: {type(error).__name__}")
            try:
                return self.status()
            except SetupError:
                # Connected and the old profiles are gone: reporting a failure
                # now would be wrong. The kiosk reads the status again later.
                return None

        message = connect_error(result)
        self.log(f"WLAN-Einstellung: Verbindung mit '{ssid}' fehlgeschlagen ({short_error(result)}).")
        self.delete(new_uuid)
        path.unlink(missing_ok=True)
        if previous is not None:
            back = self.run(["--wait", str(ROLLBACK_WAIT_SECONDS), "connection", "up", "uuid", previous["uuid"],
                             "ifname", device["device"]], ROLLBACK_TIMEOUT_SECONDS)
            if back.returncode != 0:
                self.log(f"WARNUNG: vorheriges WLAN {previous['name']} nicht wieder aktiviert: {short_error(back)}")
        raise SetupError(message)

    # -- maintenance WLAN from the server --

    def sync_service_wifi(self, desired: Optional[dict]) -> str:
        """Brings stempeluhr-service in line with the server; returns what changed."""
        path = self.connections_dir / f"{SERVICE_ID}.nmconnection"
        existing = [profile["uuid"] for profile in self.profiles() if profile["name"] == SERVICE_ID]
        if desired is None:
            if not existing and not path.exists():
                return "unverändert"
            for old in existing:
                self.delete(old)
            path.unlink(missing_ok=True)
            return "entfernt"
        ssid, psk = validate_ssid(desired.get("ssid")), validate_psk(desired.get("psk"))
        if not psk:
            raise SetupError("Wartungs-WLAN ohne Passwort wird nicht eingerichtet.")
        current = read_text(path)
        connection_uuid = uuid_of(current) or str(uuidlib.uuid4())
        # "WPA3": a hotspot with WPA3-Personal only needs SAE; everything else
        # (also missing, from an older server) is WPA2 incl. WPA2/WPA3 mixed.
        key_mgmt = "sae" if desired.get("security") == "WPA3" else "wpa-psk"
        content = keyfile(SERVICE_ID, connection_uuid, ssid, psk, key_mgmt, SERVICE_PRIORITY)
        if current == content and existing == [connection_uuid]:
            return "unverändert"
        # Also a profile of the same name created by hand (other uuid/file).
        for old in existing:
            if old != connection_uuid:
                self.delete(old)
        self.write_profile(path.name, content)
        self.load(path)
        return "eingerichtet"


def connect_error(result: CommandResult) -> str:
    if result.returncode is None:
        return "Zeitüberschreitung beim Verbinden. Bitte erneut versuchen."
    text = f"{result.stderr}\n{result.stdout}".lower()
    if "secrets were required" in text or "no-secrets" in text or "802.1x supplicant" in text:
        return "Verbindung abgelehnt. Bitte das Passwort prüfen."
    if "no network with ssid" in text or "not found" in text:
        return "WLAN nicht gefunden. Ist es in Reichweite?"
    if "timeout" in text or "timed out" in text:
        return "Zeitüberschreitung beim Verbinden. Bitte erneut versuchen."
    return "Verbindung fehlgeschlagen. Bitte erneut versuchen."


# --- serve: one request of the agent ---------------------------------------------

class RecoveryPause:
    """Holds the lock of wifi_recovery.py, so it does not interfere meanwhile."""

    def __init__(self, directory: Path = STATE_DIR, wait_seconds: float = LOCK_WAIT_SECONDS,
                 sleep: Callable[[float], None] = time.sleep):
        self.directory, self.wait_seconds, self.sleep = directory, wait_seconds, sleep
        self.handle = None

    def __enter__(self) -> "RecoveryPause":
        try:
            self.directory.mkdir(parents=True, exist_ok=True, mode=0o700)
            self.handle = (self.directory / "lock").open("a")
        except OSError:
            return self
        deadline = time.monotonic() + self.wait_seconds
        while True:
            try:
                fcntl.flock(self.handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
                return self
            except BlockingIOError:
                if time.monotonic() >= deadline:
                    return self  # a hanging recovery run must not block the setup
                self.sleep(0.5)

    def __exit__(self, *_exc) -> None:
        if self.handle is not None:
            self.handle.close()


class ConnectLock:
    """Only one connect at a time, also after the agent gave up waiting for one.

    Not waited for: a second connect fails right away instead of running its
    own rollback in parallel to the first one.
    """

    def __init__(self, path: Optional[Path] = None):
        self.path = path or CONNECT_LOCK
        self.handle = None

    def __enter__(self) -> "ConnectLock":
        try:
            self.handle = self.path.open("a")
        except OSError:
            raise SetupError("WLAN-Einstellung gerade nicht möglich (Sperrdatei).") from None
        try:
            fcntl.flock(self.handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            self.handle.close()
            self.handle = None
            raise SetupError("Eine WLAN-Umstellung oder ein Agent-Update läuft noch. "
                             "Bitte in ein paar Minuten erneut versuchen.") from None
        return self

    def __exit__(self, *_exc) -> None:
        if self.handle is not None:
            self.handle.close()


def retarget_recovery(config_path: Path, connection_uuid: str, log: Callable[[str], None]) -> None:
    """Points a configured wifi_recovery.connection_uuid at the new profile.

    Otherwise the recovery would keep activating the old WLAN (or skip itself
    when that profile was deleted). Without the key nothing changes: the
    recovery then picks the profile with the highest priority, which is the
    new stempeluhr-wlan anyway. Mode and owner of config.json are kept.
    """
    try:
        config = json.loads(config_path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return
    recovery = config.get("wifi_recovery") if isinstance(config, dict) else None
    if not isinstance(recovery, dict) or not recovery.get("connection_uuid") \
            or recovery["connection_uuid"] == connection_uuid:
        return
    recovery["connection_uuid"] = connection_uuid
    info = config_path.stat()
    write_private(config_path, json.dumps(config, indent=2) + "\n", info.st_mode & 0o777,
                  info.st_gid if os.geteuid() == 0 else None)
    log("WLAN-Recovery folgt jetzt dem neuen WLAN-Profil.")


def handle_request(raw: bytes, manager: NetworkManager,
                   pause: Callable[[], RecoveryPause] = RecoveryPause,
                   connect_lock: Callable[[], ConnectLock] = ConnectLock,
                   on_start: Callable[[], None] = lambda: None,
                   config_path: Optional[Path] = None) -> dict:
    """on_start: called for a connect once it holds the lock, before anything
    changes (serve() tells the agent with STARTED_LINE)."""
    try:
        request = json.loads(raw.decode("utf-8"))
        if not isinstance(request, dict):
            raise ValueError()
    except (UnicodeDecodeError, ValueError):
        return {"ok": False, "error": "Ungültige Anfrage."}
    command = request.get("command")
    try:
        if command == "status":
            return {"ok": True, "status": manager.status()}
        if command == "scan":
            return {"ok": True, "networks": manager.scan()}
        if command == "connect":
            hidden = request.get("hidden") is True
            security = request.get("security")
            if security is not None and security not in HIDDEN_SECURITY:
                return {"ok": False, "error": "Unbekannte Verschlüsselung."}
            config = config_path or CONFIG
            with connect_lock():
                on_start()
                with pause():
                    return {"ok": True, "status": manager.connect(
                        request.get("ssid"), request.get("psk", ""), hidden, security if hidden else None,
                        on_connected=lambda uuid: retarget_recovery(config, uuid, manager.log))}
    except SetupError as error:
        return {"ok": False, "error": str(error)}
    return {"ok": False, "error": "Unbekannter Befehl."}


def serve(stdin=sys.stdin.buffer, stdout=sys.stdout, manager: Optional[NetworkManager] = None) -> int:
    raw = stdin.readline(MAX_REQUEST_BYTES + 1)

    def started() -> None:
        stdout.write(STARTED_LINE.decode())
        stdout.flush()

    answer = ({"ok": False, "error": "Anfrage zu groß."} if len(raw) > MAX_REQUEST_BYTES
              else handle_request(raw, manager or NetworkManager(), on_start=started))
    stdout.write(json.dumps(answer) + "\n")
    stdout.flush()
    return 0


# --- sync-service: maintenance settings from the server --------------------------------

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def fetch_maintenance(config: dict) -> Optional[dict]:
    """None without a terminal token; raises OSError/ValueError when unreachable."""
    base_url = str(config.get("api_base_url") or "").rstrip("/")
    token = config.get("terminal_token")
    if not base_url or not isinstance(token, str) or not token:
        return None
    request = urllib.request.Request(base_url + "/api/kiosk/maintenance", headers={
        "Authorization": "Bearer " + token,
        "X-Terminal-Id": str(config.get("terminal_id") or "default"),
        "User-Agent": "Stempeluhr-WLAN-Setup",
    })
    with urllib.request.build_opener(NoRedirect()).open(request, timeout=SERVER_TIMEOUT_SECONDS) as response:
        answer = json.loads(response.read(65536))
    if not isinstance(answer, dict):
        raise ValueError("unexpected answer")
    return answer


def sync_pin_hash(pin_hash: object, path: Path) -> str:
    if pin_hash is None:
        if not path.exists():
            return "unverändert"
        path.unlink()
        return "entfernt"
    if not isinstance(pin_hash, str) or not PIN_HASH.fullmatch(pin_hash):
        raise SetupError("ungültiger PIN-Hash vom Server")
    content = json.dumps({"maintenancePinHash": pin_hash}) + "\n"
    if read_text(path) == content:
        return "unverändert"
    try:
        group = grp.getgrnam(SERVICE_GROUP).gr_gid
    except KeyError:
        group = None
    # Readable for the agent (group stempeluhr), the hash only - never the PIN.
    write_private(path, content, 0o640, group if os.geteuid() == 0 else None)
    return "eingerichtet"


def sync_service(config_path: Path, manager: Optional[NetworkManager] = None,
                 fetch: Callable[[dict], Optional[dict]] = fetch_maintenance,
                 log: Callable[[str], None] = print) -> int:
    try:
        config = json.loads(config_path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return 0
    if not isinstance(config, dict):
        return 0
    try:
        answer = fetch(config)
    except urllib.error.HTTPError as error:
        error.close()
        log(f"Wartungseinstellungen nicht abgerufen (HTTP {error.code}); nichts geändert.")
        return 0
    except (OSError, ValueError) as error:
        log(f"Wartungseinstellungen nicht abgerufen ({type(error).__name__}); nichts geändert.")
        return 0
    if answer is None:
        return 0  # no terminal token: maintenance settings are not available
    manager = manager or NetworkManager(log=log)
    for label, action in (
        ("Wartungs-WLAN", lambda: manager.sync_service_wifi(
            answer.get("serviceWifi") if isinstance(answer.get("serviceWifi"), dict) else None)),
        ("Wartungs-PIN", lambda: sync_pin_hash(answer.get("maintenancePinHash"),
                                               config_path.parent / MAINTENANCE_FILE_NAME)),
    ):
        try:
            outcome = action()
        except (SetupError, OSError) as error:
            log(f"WARNUNG: {label} nicht übernommen: {error}")
            continue
        if outcome != "unverändert":
            log(f"{label} {outcome}.")
    return 0


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    parser.add_argument("command", choices=["serve", "sync-service"])
    parser.add_argument("--config", type=Path, default=CONFIG)
    args = parser.parse_args(argv)
    if args.command == "serve":
        return serve()
    return sync_service(args.config)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:  # noqa: BLE001 - one journal line instead of a traceback
        print(f"WLAN-Einstellung fehlgeschlagen: {type(error).__name__}", file=sys.stderr)
        sys.exit(1)
