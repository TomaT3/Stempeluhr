"""Maintenance access of the NFC agent: PIN unlock and the WLAN setup helper.

The kiosk opens the WLAN setup behind a hidden gesture and the maintenance PIN.
The server stores only a PBKDF2 hash of that PIN; wifi_setup.py sync-service
puts it next to config.json (maintenance.json), so unlocking also works while
the terminal is offline - which is exactly when the WLAN needs changing.

A correct PIN yields a short-lived session token (memory only). After
repeated wrong PINs the unlock is locked with a growing wait. WLAN commands
go to the root helper wifi_setup.py over a Unix socket; the agent itself
stays unprivileged.
"""
from __future__ import annotations

import hashlib
import hmac
import json
import re
import secrets
import socket
import threading
import time
from pathlib import Path
from typing import Callable, Optional

SOCKET_PATH = Path("/run/stempeluhr-wifi-setup.sock")
SESSION_SECONDS = 10 * 60
FREE_ATTEMPTS = 5
LOCK_BASE_SECONDS = 60
LOCK_MAX_SECONDS = 15 * 60
MAX_ITERATIONS = 2_000_000
MAX_ANSWER_BYTES = 65536
# Longer than the helper's worst case (wifi_setup.CONNECT_WORST_CASE_SECONDS,
# 310 s) and shorter than its RuntimeMaxSec (6 min): the agent never gives up
# on a connect that is still running or rolling back.
CONNECT_TIMEOUT_SECONDS = 330
# A status query of the helper may take up to three nmcli calls of 15 s; the
# agent keeps its socket open that long (no orphaned helper), the kiosk is
# answered after STATUS_WAIT_SECONDS with the last known status meanwhile.
STATUS_QUERY_TIMEOUT_SECONDS = 60
SCAN_QUERY_TIMEOUT_SECONDS = 60
# Below the kiosk's 15 s request timeout (terminal-wifi.ts, AGENT_TIMEOUT_MS).
STATUS_WAIT_SECONDS = 8
SCAN_WAIT_SECONDS = 40

# Encryption of a hidden network (not in the scan): WPA2 (also WPA2/WPA3
# mixed), WPA3 only, '' open. Shared by agent and helper; the kiosk offers
# the same values (terminal-wifi.ts, HiddenSecurity).
HIDDEN_SECURITY = ("WPA2", "WPA3", "")
PIN_HASH = re.compile(r"pbkdf2-sha256\$([1-9][0-9]{0,6})\$([0-9a-f]{16,128})\$([0-9a-f]{32,128})")


def verify_pin(pin: str, stored: Optional[str]) -> bool:
    """Same format as TerminalMaintenance.HashPin on the server."""
    match = PIN_HASH.fullmatch(stored or "")
    if match is None or int(match.group(1)) > MAX_ITERATIONS:
        return False
    expected = bytes.fromhex(match.group(3))
    actual = hashlib.pbkdf2_hmac("sha256", pin.encode("utf-8"), bytes.fromhex(match.group(2)),
                                 int(match.group(1)), dklen=len(expected))
    return hmac.compare_digest(actual, expected)


class MaintenanceAccess:
    """PIN check with lockout and one session at a time; thread-safe."""

    def __init__(self, hash_file: Path, clock: Callable[[], float] = time.monotonic):
        self.hash_file, self.clock = hash_file, clock
        self._lock = threading.Lock()
        # One PIN check at a time: parallel requests must not all pass the
        # lockout check before the first failure is counted.
        self._attempt = threading.Lock()
        self._failures = 0
        self._locked_until = 0.0
        self._session: Optional[str] = None
        self._session_until = 0.0

    def stored_hash(self) -> Optional[str]:
        try:
            value = json.loads(self.hash_file.read_text(encoding="utf-8")).get("maintenancePinHash")
        except (OSError, ValueError, AttributeError):
            return None
        return value if isinstance(value, str) and value else None

    def enabled(self) -> bool:
        return self.stored_hash() is not None

    def unlock(self, pin: object) -> tuple[str, object]:
        """("ok", token) | ("locked", seconds) | ("wrong", None) | ("disabled", None)."""
        stored = self.stored_hash()
        if stored is None:
            return "disabled", None
        with self._attempt:
            return self._unlock(pin, stored)

    def _unlock(self, pin: object, stored: str) -> tuple[str, object]:
        with self._lock:
            remaining = self._locked_until - self.clock()
            if remaining > 0:
                return "locked", int(remaining) + 1
        valid = isinstance(pin, str) and pin.isdigit() and len(pin) <= 12 and verify_pin(pin, stored)
        with self._lock:
            if not valid:
                self._failures += 1
                if self._failures >= FREE_ATTEMPTS:
                    wait = min(LOCK_BASE_SECONDS * 2 ** (self._failures - FREE_ATTEMPTS), LOCK_MAX_SECONDS)
                    self._locked_until = self.clock() + wait
                    return "locked", wait
                return "wrong", None
            self._failures = 0
            self._session = secrets.token_urlsafe(32)
            self._session_until = self.clock() + SESSION_SECONDS
            return "ok", self._session

    def check(self, token: object) -> bool:
        """Valid session; each use extends it."""
        with self._lock:
            if (not isinstance(token, str) or self._session is None or self.clock() >= self._session_until
                    or not hmac.compare_digest(token.encode(), self._session.encode())):
                return False
            self._session_until = self.clock() + SESSION_SECONDS
            return True

    def end(self, token: object) -> None:
        if self.check(token):
            with self._lock:
                self._session = None


class WifiSetupUnavailable(Exception):
    """The root helper is not installed or does not answer."""

    reason = "missing"


class WifiSetupError(WifiSetupUnavailable):
    """The helper is installed but broke off or answered garbage."""

    reason = "error"


class WifiSetupTimeout(WifiSetupError):
    """The helper is there but did not answer in time (it may still be working)."""

    reason = "timeout"


class WifiSetupClient:
    def __init__(self, socket_path: Path = SOCKET_PATH):
        self.socket_path = socket_path

    def request(self, payload: dict, timeout: float = 30) -> dict:
        """`timeout` bounds the whole request, not each socket operation."""
        deadline = time.monotonic() + timeout

        def remaining() -> float:
            left = deadline - time.monotonic()
            if left <= 0:
                raise socket.timeout("deadline")
            return left

        try:
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
                connection.settimeout(remaining())
                connection.connect(str(self.socket_path))
                connection.settimeout(remaining())
                connection.sendall(json.dumps(payload).encode("utf-8") + b"\n")
                connection.shutdown(socket.SHUT_WR)
                chunks, size = [], 0
                while size <= MAX_ANSWER_BYTES:
                    connection.settimeout(remaining())
                    chunk = connection.recv(8192)
                    if not chunk:
                        break
                    chunks.append(chunk)
                    size += len(chunk)
            answer = json.loads(b"".join(chunks))
        # socket.timeout: an alias of TimeoutError only since Python 3.10.
        except (socket.timeout, TimeoutError) as error:
            raise WifiSetupTimeout(type(error).__name__) from None
        except (FileNotFoundError, ConnectionRefusedError, PermissionError) as error:
            # No socket unit (older bundle), not enabled, or the agent user is
            # not in the group stempeluhr: the setup is incomplete.
            raise WifiSetupUnavailable(type(error).__name__) from None
        except (OSError, ValueError) as error:
            raise WifiSetupError(type(error).__name__) from None
        if not isinstance(answer, dict):
            raise WifiSetupError("unexpected answer")
        return answer


class SingleFlight:
    """At most one helper query of a kind at a time.

    The helper sockets allow only a few connections (MaxConnections): a caller
    that gives up must not leave a root helper behind while the kiosk already
    asks again. So a caller waits up to `wait` seconds; if the query is still
    running it gets the last finished result instead (or WifiSetupTimeout) and
    the query keeps running, its result serves the next caller.
    """

    def __init__(self, query: Callable[[], dict]):
        self.query = query
        self._lock = threading.Lock()
        self._running: Optional[dict] = None
        self._last: Optional[dict] = None

    def get(self, wait: float) -> dict:
        with self._lock:
            run = self._running
            if run is None:
                run = self._running = {"done": threading.Event()}
                threading.Thread(target=self._run, args=(run,), name="wifi-query", daemon=True).start()
        if not run["done"].wait(wait):
            with self._lock:
                run = self._last
            if run is None:
                raise WifiSetupTimeout("pending")
        if "error" in run:
            raise run["error"]
        return run["answer"]

    def _run(self, run: dict) -> None:
        try:
            run["answer"] = self.query()
        except WifiSetupUnavailable as error:
            run["error"] = error
        with self._lock:
            self._running, self._last = None, run
        run["done"].set()


class WifiJob:
    """The one running connect attempt; the kiosk polls its outcome."""

    def __init__(self, client: WifiSetupClient):
        self.client = client
        self._lock = threading.Lock()
        self._state: dict = {"state": "idle"}
        # Status and scan: one helper each at a time (see SingleFlight).
        self.status_query = SingleFlight(
            lambda: client.request({"command": "status"}, timeout=STATUS_QUERY_TIMEOUT_SECONDS))
        self.scan_query = SingleFlight(
            lambda: client.request({"command": "scan"}, timeout=SCAN_QUERY_TIMEOUT_SECONDS))

    def snapshot(self) -> dict:
        with self._lock:
            return dict(self._state)

    def start(self, ssid: str, psk: str, hidden: bool, security: str | None = None) -> bool:
        with self._lock:
            if self._state["state"] == "running":
                return False
            self._state = {"state": "running", "ssid": ssid}
        threading.Thread(target=self._run, args=(ssid, psk, hidden, security), name="wifi-connect",
                         daemon=True).start()
        return True

    def _run(self, ssid: str, psk: str, hidden: bool, security: str | None) -> None:
        payload = {"command": "connect", "ssid": ssid, "psk": psk, "hidden": hidden}
        if hidden and security is not None:
            payload["security"] = security
        try:
            answer = self.client.request(payload, timeout=CONNECT_TIMEOUT_SECONDS)
        except WifiSetupError:
            # Timeout or broken off mid-way: the WLAN may have changed (the
            # helper may still finish; a second connect is refused until then).
            answer = {"ok": False, "error": "Ergebnis unklar. Bitte den WLAN-Status prüfen."}
        except WifiSetupUnavailable:
            answer = {"ok": False, "error": "WLAN-Einstellung auf diesem Terminal nicht eingerichtet."}
        with self._lock:
            if answer.get("ok"):
                self._state = {"state": "connected", "ssid": ssid}
            else:
                error = answer.get("error")
                self._state = {"state": "failed", "ssid": ssid,
                               "message": error[:300] if isinstance(error, str) else "Verbindung fehlgeschlagen."}
