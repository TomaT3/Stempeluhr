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
# The helper's scan takes up to wifi_setup.SCAN_TIMEOUT + READ_TIMEOUT (40 s)
# plus its start; the kiosk waits longer still (SCAN_TIMEOUT_MS, 50 s).
# test_wifi_setup checks these relations.
SCAN_WAIT_SECONDS = 44

# Encryption of a hidden network (not in the scan): WPA2 (also WPA2/WPA3
# mixed), WPA3 only, '' open. Shared by agent and helper; the kiosk offers
# the same values (terminal-wifi.ts, HiddenSecurity).
HIDDEN_SECURITY = ("WPA2", "WPA3", "")
# Whole bytes only (even hex lengths): bytes.fromhex would raise otherwise.
# Same format as TerminalMaintenance.VerifyPin on the server; wifi_setup
# uses it before storing a hash.
PIN_HASH = re.compile(r"pbkdf2-sha256\$([1-9][0-9]{0,6})\$((?:[0-9a-f]{2}){8,64})\$((?:[0-9a-f]{2}){16,64})")
# First line of the helper's answer to a connect, before it changes anything.
STARTED_LINE = b'{"started": true}\n'
# A cached status or scan result older than this is not handed out.
MAX_RESULT_AGE_SECONDS = 30


def verify_pin(pin: str, stored: Optional[str]) -> bool:
    """Same format as TerminalMaintenance.HashPin on the server."""
    match = PIN_HASH.fullmatch(stored or "")
    if match is None or int(match.group(1)) > MAX_ITERATIONS:
        return False
    expected = bytes.fromhex(match.group(3))
    actual = hashlib.pbkdf2_hmac("sha256", pin.encode("utf-8"), bytes.fromhex(match.group(2)),
                                 int(match.group(1)), dklen=len(expected))
    return hmac.compare_digest(actual, expected)


# lock_seconds reaches LOCK_MAX_SECONDS long before; the count stays bounded.
MAX_FAILURES = 999


def lock_seconds(failures: int) -> float:
    return min(LOCK_BASE_SECONDS * 2 ** (failures - FREE_ATTEMPTS), LOCK_MAX_SECONDS)


class MaintenanceAccess:
    """PIN check with lockout and one session at a time; thread-safe.

    The failure count survives restarts (`state_file`): rebooting the kiosk or
    a crash of the agent must not reset the lockout. Its end is not stored -
    the monotonic clock restarts with the boot and the wall clock of a Pi
    without network may be off by hours - so a restart imposes the current
    wait again from the start.
    """

    def __init__(self, hash_file: Path, clock: Callable[[], float] = time.monotonic,
                 state_file: Optional[Path] = None):
        self.hash_file, self.clock, self.state_file = hash_file, clock, state_file
        self._lock = threading.Lock()
        # One PIN check at a time: parallel requests must not all pass the
        # lockout check before the first failure is counted.
        self._attempt = threading.Lock()
        self._failures = self._load_failures()
        self._locked_until = self.clock() + lock_seconds(self._failures) if self._failures >= FREE_ATTEMPTS else 0.0
        self._session: Optional[str] = None
        self._session_hash: Optional[str] = None
        self._session_until = 0.0

    def _load_failures(self) -> int:
        try:
            failures = json.loads(self.state_file.read_text(encoding="utf-8")).get("failures")
        except (AttributeError, OSError, ValueError):
            return 0
        if not isinstance(failures, int) or isinstance(failures, bool) or failures < 0:
            return 0
        # Capped, not discarded: a huge count must not reset the lockout.
        return min(failures, MAX_FAILURES)

    def _save_failures(self) -> None:
        if self.state_file is None:
            return
        try:
            temporary = self.state_file.with_name(self.state_file.name + ".tmp")
            temporary.write_text(json.dumps({"failures": self._failures}) + "\n", encoding="utf-8")
            temporary.replace(self.state_file)
        except OSError:
            pass  # the lockout still holds until the next restart

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
                self._failures = min(self._failures + 1, MAX_FAILURES)
                self._save_failures()
                if self._failures >= FREE_ATTEMPTS:
                    wait = lock_seconds(self._failures)
                    self._locked_until = self.clock() + wait
                    return "locked", wait
                return "wrong", None
            if self._failures:
                self._failures = 0
                self._save_failures()
            self._session = secrets.token_urlsafe(32)
            self._session_hash = stored
            self._session_until = self.clock() + SESSION_SECONDS
            return "ok", self._session

    def check(self, token: object) -> bool:
        """Valid session; each use extends it.

        Only while the PIN it was opened with still applies: removing or
        changing the maintenance PIN on the server ends open sessions.
        """
        current = self.stored_hash()
        with self._lock:
            if (not isinstance(token, str) or self._session is None or self.clock() >= self._session_until
                    or not hmac.compare_digest(token.encode(), self._session.encode())):
                return False
            if current != self._session_hash:
                self._session = None
                return False
            self._session_until = self.clock() + SESSION_SECONDS
            return True

    def end(self, token: object) -> None:
        if self.check(token):
            with self._lock:
                self._session = None


class WifiSetupUnavailable(Exception):
    """The root helper is not installed or does not answer.

    `started`: the helper confirmed it began a connect (it may have changed
    the WLAN). Without it nothing was changed and a retry is safe.
    """

    reason = "missing"

    def __init__(self, message: str = "", started: bool = False):
        super().__init__(message)
        self.started = started

    def fresh(self) -> "WifiSetupUnavailable":
        """A new instance to raise again: one stored error is handed to many
        callers, and every raise would append to its traceback."""
        return type(self)(str(self), self.started)


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

        # A connect is confirmed by the helper with a first line {"started": true}
        # before it changes anything; only after that the outcome is unclear.
        started = False
        try:
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
                connection.settimeout(remaining())
                connection.connect(str(self.socket_path))
                connection.settimeout(remaining())
                connection.sendall(json.dumps(payload).encode("utf-8") + b"\n")
                connection.shutdown(socket.SHUT_WR)
                buffer, size = b"", 0
                while size <= MAX_ANSWER_BYTES:
                    connection.settimeout(remaining())
                    chunk = connection.recv(8192)
                    if not chunk:
                        break
                    buffer += chunk
                    size += len(chunk)
                    started = started or buffer.startswith(STARTED_LINE)
            lines = [line for line in buffer.split(b"\n") if line.strip()]
            if lines and lines[0] == STARTED_LINE.strip():
                started, lines = True, lines[1:]
            if len(lines) != 1:
                # Empty: refused by systemd (MaxConnections), skipped unit or a
                # crashed helper - started tells them apart for a connect.
                raise ValueError("no answer")
            answer = json.loads(lines[0])
        # socket.timeout: an alias of TimeoutError only since Python 3.10.
        except (socket.timeout, TimeoutError) as error:
            raise WifiSetupTimeout(type(error).__name__, started) from None
        except (FileNotFoundError, ConnectionRefusedError, PermissionError) as error:
            # No socket unit (older bundle), not enabled, or the agent user is
            # not in the group stempeluhr: the setup is incomplete.
            raise WifiSetupUnavailable(type(error).__name__) from None
        except (OSError, ValueError, RecursionError) as error:
            raise WifiSetupError(type(error).__name__, started) from None
        if not isinstance(answer, dict):
            raise WifiSetupError("unexpected answer", started)
        return answer


class SingleFlight:
    """At most one helper query of a kind at a time.

    The helper sockets allow only a few connections (MaxConnections): a caller
    that gives up must not leave a root helper behind while the kiosk already
    asks again. So a caller waits up to `wait` seconds; if the query is still
    running it gets the last finished result instead (or WifiSetupTimeout) and
    the query keeps running, its result serves the next caller.
    """

    def __init__(self, query: Callable[[], dict], clock: Callable[[], float] = time.monotonic):
        self.query, self.clock = query, clock
        self._lock = threading.Lock()
        self._running: Optional[dict] = None
        self._last: Optional[dict] = None

    def get(self, wait: float, not_before: float = float("-inf")) -> dict:
        """A result of a query started at or after `not_before` (e.g. the last
        change of the connect job): an older one may describe the WLAN before it."""
        with self._lock:
            run = self._running
            if run is None:
                run = {"done": threading.Event(), "started": self.clock()}
                try:
                    threading.Thread(target=self._run, args=(run,), name="wifi-query", daemon=True).start()
                except RuntimeError:
                    raise WifiSetupError("no thread") from None
                self._running = run
        if not run["done"].wait(wait):
            with self._lock:
                run = self._last
            if run is None or self.clock() - run["started"] > MAX_RESULT_AGE_SECONDS:
                raise WifiSetupTimeout("pending")
        if run["started"] < not_before:
            raise WifiSetupTimeout("stale")
        if "error" in run:
            raise run["error"].fresh()
        return dict(run["answer"])

    def _run(self, run: dict) -> None:
        try:
            run["answer"] = self.query()
        except WifiSetupUnavailable as error:
            run["error"] = error
        except Exception as error:  # noqa: BLE001 - must never leave the query "running" forever
            run["error"] = WifiSetupError(type(error).__name__)
        finally:
            if "answer" not in run and "error" not in run:
                run["error"] = WifiSetupError("no answer")
            with self._lock:
                self._running, self._last = None, run
            run["done"].set()


class WifiJob:
    """The one running connect attempt; the kiosk polls its outcome."""

    def __init__(self, client: WifiSetupClient, clock: Callable[[], float] = time.monotonic):
        self.client, self.clock = client, clock
        self._lock = threading.Lock()
        self._state: dict = {"state": "idle"}
        # The helper's status right after a successful connect.
        self._connect_status: Optional[dict] = None
        # Last change of the job: status results of queries started before
        # it may still show the WLAN before the change.
        self.changed_at = float("-inf")
        # Status and scan: one helper each at a time (see SingleFlight).
        # Together with the connect at most three of the socket's
        # MaxConnections (test_wifi_setup checks the relation).
        self.status_query = SingleFlight(
            lambda: self.client.request({"command": "status"}, timeout=STATUS_QUERY_TIMEOUT_SECONDS), clock)
        self.scan_query = SingleFlight(
            lambda: self.client.request({"command": "scan"}, timeout=SCAN_QUERY_TIMEOUT_SECONDS), clock)

    def snapshot(self) -> dict:
        with self._lock:
            return dict(self._state)

    def connect_status(self) -> Optional[dict]:
        """The status the helper reported with the last successful connect,
        while it is recent: it stands in for a slow query right after the
        connect, not days later when the WLAN may be gone."""
        with self._lock:
            if not self._connect_status or self.clock() - self.changed_at > MAX_RESULT_AGE_SECONDS:
                return None
            return dict(self._connect_status)

    def _set(self, state: dict, connect_status: Optional[dict] = None) -> None:
        self._state, self._connect_status = state, connect_status
        self.changed_at = self.clock()

    def start(self, ssid: str, psk: str, hidden: bool, security: str | None = None) -> bool:
        with self._lock:
            if self._state["state"] == "running":
                return False
            self._set({"state": "running", "ssid": ssid})
        try:
            threading.Thread(target=self._run, args=(ssid, psk, hidden, security), name="wifi-connect",
                             daemon=True).start()
        except RuntimeError:
            with self._lock:
                self._set({"state": "failed", "ssid": ssid, "message": NOT_STARTED})
        return True

    def _run(self, ssid: str, psk: str, hidden: bool, security: str | None) -> None:
        payload = {"command": "connect", "ssid": ssid, "psk": psk, "hidden": hidden}
        if hidden and security is not None:
            payload["security"] = security
        answer: dict = {"ok": False, "error": UNCLEAR}
        try:
            answer = self.client.request(payload, timeout=CONNECT_TIMEOUT_SECONDS)
        except WifiSetupError as error:
            # Broken off or timed out: only after the helper confirmed the
            # start may the WLAN have changed (it may still finish; a second
            # connect is refused until then).
            answer = {"ok": False, "error": UNCLEAR if error.started else NOT_STARTED}
        except WifiSetupUnavailable:
            answer = {"ok": False, "error": "WLAN-Einstellung auf diesem Terminal nicht eingerichtet."}
        except Exception:  # noqa: BLE001 - the job must never stay "running"
            answer = {"ok": False, "error": UNCLEAR}
        finally:
            with self._lock:
                if answer.get("ok"):
                    status = answer.get("status")
                    self._set({"state": "connected", "ssid": ssid}, status if isinstance(status, dict) else None)
                else:
                    error = answer.get("error")
                    self._set({"state": "failed", "ssid": ssid,
                               "message": error[:300] if isinstance(error, str) else "Verbindung fehlgeschlagen."})


UNCLEAR = "Ergebnis unklar. Bitte den WLAN-Status prüfen."
NOT_STARTED = "Die WLAN-Umstellung wurde nicht gestartet. Bitte erneut versuchen."
