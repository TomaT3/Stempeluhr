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


class WifiSetupClient:
    def __init__(self, socket_path: Path = SOCKET_PATH):
        self.socket_path = socket_path

    def request(self, payload: dict, timeout: float = 30) -> dict:
        try:
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
                connection.settimeout(timeout)
                connection.connect(str(self.socket_path))
                connection.sendall(json.dumps(payload).encode("utf-8") + b"\n")
                connection.shutdown(socket.SHUT_WR)
                chunks, size = [], 0
                while size <= MAX_ANSWER_BYTES:
                    chunk = connection.recv(8192)
                    if not chunk:
                        break
                    chunks.append(chunk)
                    size += len(chunk)
            answer = json.loads(b"".join(chunks))
        except (OSError, ValueError) as error:
            raise WifiSetupUnavailable(type(error).__name__) from None
        if not isinstance(answer, dict):
            raise WifiSetupUnavailable("unexpected answer")
        return answer


class WifiJob:
    """The one running connect attempt; the kiosk polls its outcome."""

    def __init__(self, client: WifiSetupClient):
        self.client = client
        self._lock = threading.Lock()
        self._state: dict = {"state": "idle"}

    def snapshot(self) -> dict:
        with self._lock:
            return dict(self._state)

    def start(self, ssid: str, psk: str, hidden: bool) -> bool:
        with self._lock:
            if self._state["state"] == "running":
                return False
            self._state = {"state": "running", "ssid": ssid}
        threading.Thread(target=self._run, args=(ssid, psk, hidden), name="wifi-connect", daemon=True).start()
        return True

    def _run(self, ssid: str, psk: str, hidden: bool) -> None:
        try:
            answer = self.client.request({"command": "connect", "ssid": ssid, "psk": psk, "hidden": hidden},
                                         timeout=150)
        except WifiSetupUnavailable:
            answer = {"ok": False, "error": "WLAN-Einstellung auf diesem Terminal nicht verfügbar."}
        with self._lock:
            if answer.get("ok"):
                self._state = {"state": "connected", "ssid": ssid}
            else:
                error = answer.get("error")
                self._state = {"state": "failed", "ssid": ssid,
                               "message": error[:300] if isinstance(error, str) else "Verbindung fehlgeschlagen."}
