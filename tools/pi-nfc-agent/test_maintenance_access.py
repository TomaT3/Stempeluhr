"""Tests of the maintenance PIN, its lockout and the agent's WLAN routes."""
import hashlib
import json
from pathlib import Path
import tempfile
import threading
import time
import unittest
from unittest.mock import patch
import urllib.error
import urllib.request

# Installs the smartcard stub before the agent module is imported.
from test_local_scan_server import FOREIGN, KIOSK  # noqa: F401
import socket

from maintenance_access import (FREE_ATTEMPTS, LOCK_BASE_SECONDS, MAX_RESULT_AGE_SECONDS, SESSION_SECONDS,
                                MaintenanceAccess, SingleFlight, WifiJob,
                                WifiSetupClient, WifiSetupError, WifiSetupTimeout, WifiSetupUnavailable, verify_pin)
from stempeluhr_nfc_agent import LocalScanServer

PIN = "482915"
SALT = "ab" * 16


def pin_hash(pin: str, iterations: int = 1000) -> str:
    digest = hashlib.pbkdf2_hmac("sha256", pin.encode(), bytes.fromhex(SALT), iterations).hex()
    return f"pbkdf2-sha256${iterations}${SALT}${digest}"


class Clock:
    def __init__(self):
        self.now = 1000.0

    def __call__(self):
        return self.now


class FakeHelper:
    """The root helper behind the socket."""

    def __init__(self):
        self.requests = []
        self.available = True
        self.connect_answer = {"ok": True, "status": {"state": "connected"}}
        self.release = threading.Event()
        self.release.set()
        # Cleared: the status query hangs, like nmcli with a busy NetworkManager.
        self.status_gate = threading.Event()
        self.status_gate.set()
        self.scan_gate = threading.Event()
        self.scan_gate.set()
        self.scan_error = None
        self.connect_status = {"state": "connected", "ssid": "Neu", "ip": "192.168.5.7"}

    def request(self, payload, timeout=30):
        self.requests.append(payload)
        if not self.available:
            raise WifiSetupUnavailable("FileNotFoundError")
        if payload["command"] == "status":
            self.status_gate.wait(5)
            return {"ok": True, "status": {"state": "connected", "ssid": "Kunde", "ip": "192.168.1.23"}}
        if payload["command"] == "scan":
            self.scan_gate.wait(5)
            if self.scan_error:
                raise self.scan_error
            return {"ok": True, "networks": [{"ssid": "Kunde", "signal": 80, "security": "WPA2", "inUse": True}]}
        self.release.wait(5)
        return self.connect_answer


class ClientTest(unittest.TestCase):
    """Why the helper did not answer, over a real Unix socket."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.path = Path(self.tmp.name) / "setup.sock"

    def tearDown(self):
        self.tmp.cleanup()

    def test_missing_socket(self):
        with self.assertRaises(WifiSetupUnavailable) as caught:
            WifiSetupClient(self.path).request({"command": "status"}, timeout=1)
        self.assertEqual(caught.exception.reason, "missing")

    def test_silent_helper_is_a_timeout(self):
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as listener:
            listener.bind(str(self.path))
            listener.listen(1)
            with self.assertRaises(WifiSetupTimeout) as caught:
                WifiSetupClient(self.path).request({"command": "status"}, timeout=0.2)
        self.assertEqual(caught.exception.reason, "timeout")

    def test_deadline_covers_the_whole_request(self):
        """A helper that trickles bytes must not stretch the timeout per recv()."""
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as listener:
            listener.bind(str(self.path))
            listener.listen(1)

            def trickle():
                connection, _ = listener.accept()
                with connection:
                    for _ in range(40):
                        try:
                            connection.sendall(b" ")
                        except OSError:
                            return
                        time.sleep(0.05)

            threading.Thread(target=trickle, daemon=True).start()
            started = time.monotonic()
            with self.assertRaises(WifiSetupTimeout):
                WifiSetupClient(self.path).request({"command": "status"}, timeout=0.3)
            self.assertLess(time.monotonic() - started, 0.8)

    def test_missing_group_rights_count_as_missing(self):
        class Refused:
            def __init__(self, *_args):
                pass

            def __enter__(self):
                return self

            def __exit__(self, *_exc):
                return False

            def settimeout(self, _timeout):
                pass

            def connect(self, _path):
                raise PermissionError(13, "Permission denied")

        with patch("maintenance_access.socket.socket", Refused):
            with self.assertRaises(WifiSetupUnavailable) as caught:
                WifiSetupClient(self.path).request({"command": "status"}, timeout=1)
        self.assertEqual(caught.exception.reason, "missing")

    def job_message(self, error):
        class Failing:
            def request(self, payload, timeout=30):
                raise error

        job = WifiJob(Failing())
        job.start("Kunde", "passwort1", False)
        for _ in range(50):
            if job.snapshot()["state"] != "running":
                break
            time.sleep(0.02)
        return job.snapshot()["message"]

    def test_job_reports_unclear_outcomes_as_such(self):
        # Only after the helper confirmed the start may the WLAN have changed.
        for error in (WifiSetupTimeout("timeout", started=True), WifiSetupError("ConnectionResetError", started=True)):
            self.assertEqual(self.job_message(error), "Ergebnis unklar. Bitte den WLAN-Status prüfen.")
        for error in (WifiSetupTimeout("timeout"), WifiSetupError("ValueError")):
            self.assertIn("nicht gestartet", self.job_message(error))
        self.assertIn("nicht eingerichtet", self.job_message(WifiSetupUnavailable("FileNotFoundError")))
        # An unexpected error must not leave the job "running" (every connect would get 409).
        self.assertIn("unklar", self.job_message(RecursionError()))

    def serve_once(self, answer: bytes):
        """A helper that sends `answer` and closes."""
        listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        listener.bind(str(self.path))
        listener.listen(1)

        def reply():
            connection, _ = listener.accept()
            with connection, listener:
                connection.recv(4096)
                connection.sendall(answer)

        threading.Thread(target=reply, daemon=True).start()

    def test_started_line_tells_whether_a_connect_began(self):
        self.serve_once(b'{"started": true}\n{"ok": true, "status": null}\n')
        self.assertEqual(WifiSetupClient(self.path).request({"command": "connect"}, timeout=2),
                         {"ok": True, "status": None})
        self.path.unlink()
        self.serve_once(b'{"started": true}\n')  # helper died after the start
        with self.assertRaises(WifiSetupError) as caught:
            WifiSetupClient(self.path).request({"command": "connect"}, timeout=2)
        self.assertTrue(caught.exception.started)
        self.path.unlink()
        self.serve_once(b"")  # refused by systemd (MaxConnections) or skipped unit
        with self.assertRaises(WifiSetupError) as caught:
            WifiSetupClient(self.path).request({"command": "connect"}, timeout=2)
        self.assertFalse(caught.exception.started)


class SingleFlightTest(unittest.TestCase):
    def setUp(self):
        self.clock = Clock()
        self.calls = 0
        self.gate = threading.Event()
        self.gate.set()
        self.result: object = {"ok": True, "n": 0}

    def query(self):
        self.calls += 1
        self.gate.wait(5)
        if isinstance(self.result, BaseException):
            raise self.result
        return {**self.result, "n": self.calls}

    def test_falls_back_to_a_recent_result_only(self):
        flight = SingleFlight(self.query, self.clock)
        self.assertEqual(flight.get(1)["n"], 1)
        self.gate.clear()
        self.clock.now += 10
        self.assertEqual(flight.get(0.05)["n"], 1)  # slow: the last result, 10 s old
        self.clock.now += MAX_RESULT_AGE_SECONDS
        with self.assertRaises(WifiSetupTimeout):
            flight.get(0.05)  # too old to stand in
        self.assertEqual(self.calls, 2)  # still the one running query
        self.gate.set()

    def test_ignores_results_from_before_a_change(self):
        flight = SingleFlight(self.query, self.clock)
        flight.get(1)  # n=1, started at 1000
        self.gate.clear()
        self.assertEqual(flight.get(0.01)["n"], 1)  # starts n=2 at 1000, falls back to n=1
        # The connect job changes now; n=2 is still running and began before.
        changed_at = self.clock.now + 1
        self.clock.now += 2
        threading.Timer(0.05, self.gate.set).start()
        with self.assertRaises(WifiSetupTimeout):
            flight.get(1, not_before=changed_at)  # n=2 finishes meanwhile: stale
        # The next query starts after the change and counts.
        self.assertEqual(flight.get(1, not_before=changed_at)["n"], 3)

    def test_unexpected_errors_do_not_block_later_queries(self):
        flight = SingleFlight(self.query, self.clock)
        self.result = MemoryError()
        with self.assertRaises(WifiSetupError):
            flight.get(1)
        self.result = {"ok": True}
        self.assertEqual(flight.get(1)["n"], 2)

    def test_stored_error_is_raised_as_a_fresh_copy(self):
        flight = SingleFlight(self.query, self.clock)
        self.result = WifiSetupError("broken")
        raised = []
        with self.assertRaises(WifiSetupError) as first:
            flight.get(1)  # the failed run becomes the stored result
        raised.append(first.exception)
        self.gate.clear()  # from now on every caller gets the stored error
        for _ in range(4):
            with self.assertRaises(WifiSetupError) as again:
                flight.get(0.01)
            raised.append(again.exception)
        self.gate.set()
        self.assertEqual(len({id(error) for error in raised}), len(raised))
        depths = []
        for error in raised:
            depth, tb = 0, error.__traceback__
            while tb:
                depth, tb = depth + 1, tb.tb_next
            depths.append(depth)
        self.assertEqual(len(set(depths)), 1, depths)  # does not grow with every raise

    def test_scan_and_status_run_one_helper_each(self):
        helper = FakeHelper()
        helper.status_gate.clear()
        helper.scan_gate.clear()
        job = WifiJob(helper)
        threads = [threading.Thread(target=lambda: self.swallow(job.status_query.get, 0.1)) for _ in range(3)]
        threads += [threading.Thread(target=lambda: self.swallow(job.scan_query.get, 0.1)) for _ in range(3)]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join(5)
        commands = sorted(r["command"] for r in helper.requests)
        helper.status_gate.set()
        helper.scan_gate.set()
        self.assertEqual(commands, ["scan", "status"])

    def test_connect_status_stands_in_only_while_recent(self):
        helper = FakeHelper()
        helper.connect_answer = {"ok": True, "status": helper.connect_status}
        job = WifiJob(helper, self.clock)
        job.start("Neu", "passwort1", False)
        for _ in range(50):
            if job.snapshot()["state"] == "connected":
                break
            time.sleep(0.02)
        self.assertEqual(job.connect_status()["ip"], "192.168.5.7")
        self.clock.now += MAX_RESULT_AGE_SECONDS + 1  # days later the WLAN may be gone
        self.assertIsNone(job.connect_status())

    def test_queries_follow_a_replaced_client(self):
        job = WifiJob(FakeHelper())
        replacement = FakeHelper()
        job.client = replacement
        job.status_query.get(1)
        self.assertEqual([r["command"] for r in replacement.requests], ["status"])

    @staticmethod
    def swallow(call, *args):
        try:
            call(*args)
        except WifiSetupUnavailable:
            pass


class VerifyPinTest(unittest.TestCase):
    def test_matches_server_format(self):
        stored = pin_hash(PIN)
        self.assertTrue(verify_pin(PIN, stored))
        self.assertFalse(verify_pin("482916", stored))
        for broken in (None, "", "plain", stored.replace("pbkdf2-sha256", "md5"),
                       f"pbkdf2-sha256$9999999${SALT}${'cd' * 32}"):
            self.assertFalse(verify_pin(PIN, broken))

    def test_odd_hex_lengths_are_rejected_not_raised(self):
        # bytes.fromhex would raise ValueError in the HTTP handler.
        for broken in (f"pbkdf2-sha256$1000${SALT}a${'cd' * 32}", f"pbkdf2-sha256$1000${SALT}${'cd' * 32}c"):
            self.assertFalse(verify_pin(PIN, broken))


class AccessTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.file = Path(self.tmp.name) / "maintenance.json"
        self.file.write_text(json.dumps({"maintenancePinHash": pin_hash(PIN)}))
        self.clock = Clock()
        self.access = MaintenanceAccess(self.file, self.clock)

    def tearDown(self):
        self.tmp.cleanup()

    def test_lockout_survives_a_restart(self):
        state = Path(self.tmp.name) / "lockout.json"
        access = MaintenanceAccess(self.file, self.clock, state)
        for _ in range(FREE_ATTEMPTS):
            access.unlock("000000")
        # Reboot: new process, new monotonic clock - the lockout applies again.
        restarted = MaintenanceAccess(self.file, Clock(), state)
        self.assertEqual(restarted.unlock(PIN)[0], "locked")
        # A correct PIN after the wait clears the stored count.
        restarted.clock.now += LOCK_BASE_SECONDS
        self.assertEqual(restarted.unlock(PIN)[0], "ok")
        self.assertEqual(MaintenanceAccess(self.file, Clock(), state).unlock(PIN)[0], "ok")
        # Unwritable or broken state never blocks.
        state.write_text("kaputt")
        self.assertEqual(MaintenanceAccess(self.file, Clock(), state).unlock(PIN)[0], "ok")

    def test_huge_stored_failure_count_still_locks(self):
        state = Path(self.tmp.name) / "lockout.json"
        state.write_text(json.dumps({"failures": 5000}))
        clock = Clock()
        access = MaintenanceAccess(self.file, clock, state)
        self.assertEqual(access.unlock(PIN)[0], "locked")
        clock.now += 24 * 3600
        access.unlock("000000")  # counting on stays bounded
        self.assertLessEqual(json.loads(state.read_text())["failures"], 999)

    def test_session_ends_when_the_pin_is_removed_or_changed(self):
        _, token = self.access.unlock(PIN)
        self.assertTrue(self.access.check(token))
        self.file.write_text(json.dumps({"maintenancePinHash": pin_hash("739104")}))
        self.assertFalse(self.access.check(token))
        _, token = self.access.unlock("739104")
        self.file.unlink()
        self.assertFalse(self.access.check(token))

    def test_disabled_without_hash_file(self):
        self.file.unlink()
        self.assertEqual(self.access.unlock(PIN), ("disabled", None))

    def test_session_expires_and_is_extended_by_use(self):
        outcome, token = self.access.unlock(PIN)
        self.assertEqual(outcome, "ok")
        self.clock.now += SESSION_SECONDS - 1
        self.assertTrue(self.access.check(token))
        self.clock.now += SESSION_SECONDS - 1
        self.assertTrue(self.access.check(token))
        self.assertFalse(self.access.check("x" + token))
        self.clock.now += SESSION_SECONDS
        self.assertFalse(self.access.check(token))

    def test_new_unlock_replaces_session_and_end_logs_out(self):
        _, first = self.access.unlock(PIN)
        _, second = self.access.unlock(PIN)
        self.assertFalse(self.access.check(first))
        self.access.end(second)
        self.assertFalse(self.access.check(second))

    def test_lockout_grows_and_blocks_even_the_right_pin(self):
        for _ in range(FREE_ATTEMPTS - 1):
            self.assertEqual(self.access.unlock("000000"), ("wrong", None))
        self.assertEqual(self.access.unlock("000000"), ("locked", LOCK_BASE_SECONDS))
        self.assertEqual(self.access.unlock(PIN)[0], "locked")
        self.clock.now += LOCK_BASE_SECONDS
        self.assertEqual(self.access.unlock("000000"), ("locked", 2 * LOCK_BASE_SECONDS))
        self.clock.now += 2 * LOCK_BASE_SECONDS
        self.assertEqual(self.access.unlock(PIN)[0], "ok")
        self.assertEqual(self.access.unlock("000000"), ("wrong", None))

    def test_parallel_wrong_pins_cannot_bypass_the_lockout(self):
        outcomes, checks = [], []
        threads = [threading.Thread(target=lambda: outcomes.append(self.access.unlock("000000")[0]))
                   for _ in range(30)]

        def slow_check(pin, stored):
            # A slow check like PBKDF2 on the Pi widens the race window.
            checks.append(pin)
            time.sleep(0.02)
            return False

        with patch("maintenance_access.verify_pin", slow_check):
            for thread in threads:
                thread.start()
            for thread in threads:
                thread.join(10)
        # Only the attempts before the lockout are checked at all.
        self.assertEqual(len(checks), FREE_ATTEMPTS)
        self.assertEqual(outcomes.count("wrong"), FREE_ATTEMPTS - 1)
        self.assertEqual(outcomes.count("locked"), 30 - (FREE_ATTEMPTS - 1))

    def test_non_digit_pins_count_as_wrong(self):
        for pin in (None, 482915, "48291a", "1" * 13):
            self.assertEqual(self.access.unlock(pin), ("wrong", None))


class RoutesTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.file = Path(self.tmp.name) / "maintenance.json"
        self.file.write_text(json.dumps({"maintenancePinHash": pin_hash(PIN)}))
        self.helper = FakeHelper()
        self.server = LocalScanServer(port=0, allowed_origin=KIOSK,
                                      maintenance=MaintenanceAccess(self.file), wifi_setup=self.helper)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def tearDown(self):
        self.helper.release.set()
        self.server.shutdown()
        self.server.server_close()
        self.tmp.cleanup()

    def post(self, path, body, origin=KIOSK):
        request = urllib.request.Request(f"{self.server.url}{path}", data=json.dumps(body).encode(), method="POST",
                                         headers={"Content-Type": "application/json"})
        if origin:
            request.add_header("Origin", origin)
        try:
            with urllib.request.urlopen(request, timeout=5) as response:
                return response.status, json.loads(response.read())
        except urllib.error.HTTPError as error:
            return error.code, json.loads(error.read())

    def unlock(self):
        status, answer = self.post("/maintenance/unlock", {"pin": PIN})
        self.assertEqual(status, 200)
        return answer["session"]

    def test_only_the_kiosk_origin(self):
        for origin in (FOREIGN, None):
            status, _ = self.post("/maintenance/unlock", {"pin": PIN}, origin)
            self.assertEqual(status, 403)

    def test_wifi_routes_need_a_session(self):
        for path in ("/wifi/status", "/wifi/networks", "/wifi/connect"):
            status, _ = self.post(path, {"session": "geraten", "ssid": "Kunde", "psk": "passwort1"})
            self.assertEqual(status, 401, path)
        self.assertEqual(self.helper.requests, [])

    def test_wrong_pin_and_disabled(self):
        self.assertEqual(self.post("/maintenance/unlock", {"pin": "000000"})[0], 401)
        self.file.unlink()
        self.assertEqual(self.post("/maintenance/unlock", {"pin": PIN}), (403, {"error": "maintenance disabled"}))

    def test_status_networks_and_connect(self):
        session = self.unlock()
        status, answer = self.post("/wifi/status", {"session": session})
        self.assertEqual((status, answer["status"]["ssid"], answer["job"]), (200, "Kunde", {"state": "idle"}))
        status, answer = self.post("/wifi/networks", {"session": session})
        self.assertEqual((status, answer["networks"][0]["ssid"]), (200, "Kunde"))

        self.helper.release.clear()
        self.assertEqual(self.post("/wifi/connect", {"session": session, "ssid": "Kunde", "psk": "passwort1"})[0], 202)
        self.assertEqual(self.post("/wifi/connect", {"session": session, "ssid": "Kunde", "psk": "passwort1"})[0], 409)
        self.assertEqual(self.post("/wifi/status", {"session": session})[1]["job"]["state"], "running")
        self.helper.release.set()
        for _ in range(50):
            job = self.post("/wifi/status", {"session": session})[1]["job"]
            if job["state"] != "running":
                break
            time.sleep(0.05)
        self.assertEqual(job, {"state": "connected", "ssid": "Kunde"})
        self.assertIn({"command": "connect", "ssid": "Kunde", "psk": "passwort1", "hidden": False},
                      self.helper.requests)

    def test_failed_connect_reports_message(self):
        session = self.unlock()
        self.helper.connect_answer = {"ok": False, "error": "Verbindung abgelehnt. Bitte das Passwort prüfen."}
        self.post("/wifi/connect", {"session": session, "ssid": "Kunde", "psk": "falsch123"})
        for _ in range(50):
            job = self.post("/wifi/status", {"session": session})[1]["job"]
            if job["state"] != "running":
                break
            time.sleep(0.05)
        self.assertEqual(job["state"], "failed")
        self.assertIn("Passwort", job["message"])

    def test_invalid_network_and_missing_helper(self):
        session = self.unlock()
        for body in ({"ssid": ""}, {"ssid": "x" * 33}, {"ssid": "Kunde", "psk": 5}, {"ssid": "Kunde", "psk": "x" * 65},
                     {"ssid": "Kunde", "hidden": True, "security": "WEP"}):
            self.assertEqual(self.post("/wifi/connect", {"session": session, **body})[0], 400, body)
        self.helper.available = False
        self.assertEqual(self.post("/wifi/networks", {"session": session}), (503, {"error": "wifi setup unavailable"}))
        status, answer = self.post("/wifi/status", {"session": session})
        self.assertEqual((status, answer["status"], answer["job"], answer["helper"]),
                         (200, None, {"state": "idle"}, "missing"))

    def test_hidden_network_passes_its_encryption(self):
        session = self.unlock()
        self.post("/wifi/connect", {"session": session, "ssid": "Versteckt", "psk": "passwort1", "hidden": True,
                                    "security": "WPA3"})
        for _ in range(50):
            if any(r["command"] == "connect" for r in self.helper.requests):
                break
            time.sleep(0.02)
        self.assertIn({"command": "connect", "ssid": "Versteckt", "psk": "passwort1", "hidden": True,
                       "security": "WPA3"}, self.helper.requests)

    def test_slow_status_helper_runs_once_and_is_not_orphaned(self):
        import stempeluhr_nfc_agent
        session = self.unlock()
        self.helper.status_gate.clear()
        with patch.object(stempeluhr_nfc_agent, "STATUS_WAIT_SECONDS", 0.2):
            started = time.monotonic()
            first = self.post("/wifi/status", {"session": session})[1]
            second = self.post("/wifi/status", {"session": session})[1]
            elapsed = time.monotonic() - started
        # Both answered quickly with the job, the status still pending ...
        self.assertLess(elapsed, 2)
        self.assertEqual((first["status"], first["helper"], first["job"]), (None, "timeout", {"state": "idle"}))
        self.assertEqual(second["helper"], "timeout")
        # ... and only one helper query runs: the second request reused it.
        self.assertEqual([r["command"] for r in self.helper.requests], ["status"])
        self.helper.status_gate.set()
        for _ in range(50):
            answer = self.post("/wifi/status", {"session": session})[1]
            if answer["status"] is not None:
                break
            time.sleep(0.02)
        self.assertEqual(answer["status"]["ssid"], "Kunde")
        # The kiosk waits 15 s (terminal-wifi.ts) for any agent request.
        self.assertLess(stempeluhr_nfc_agent.STATUS_WAIT_SECONDS, 15)

    def test_scan_failures_are_not_reported_as_missing_setup(self):
        session = self.unlock()
        for error, message in ((WifiSetupTimeout("x"), "dauert zu lange"), (WifiSetupError("x"), "unterbrochen")):
            self.helper.scan_error = error
            status, answer = self.post("/wifi/networks", {"session": session})
            self.assertEqual(status, 502)
            self.assertIn(message, answer["message"])

    def test_running_attempt_names_its_ssid_on_409(self):
        session = self.unlock()
        self.helper.release.clear()
        self.post("/wifi/connect", {"session": session, "ssid": "A", "psk": "passwort1"})
        status, answer = self.post("/wifi/connect", {"session": session, "ssid": "B", "psk": "passwort1"})
        self.helper.release.set()
        self.assertEqual((status, answer["ssid"]), (409, "A"))

    def test_status_of_the_connect_stands_in_for_a_slow_query(self):
        import stempeluhr_nfc_agent
        session = self.unlock()
        self.helper.connect_answer = {"ok": True, "status": self.helper.connect_status}
        self.post("/wifi/status", {"session": session})  # a status of the old WLAN is cached
        self.post("/wifi/connect", {"session": session, "ssid": "Neu", "psk": "passwort1"})
        for _ in range(50):
            if self.server.wifi_job.snapshot()["state"] == "connected":
                break
            time.sleep(0.02)
        self.helper.status_gate.clear()  # NetworkManager busy: the fresh query hangs
        with patch.object(stempeluhr_nfc_agent, "STATUS_WAIT_SECONDS", 0.1):
            answer = self.post("/wifi/status", {"session": session})[1]
        self.helper.status_gate.set()
        # Not the cached status of the old WLAN, but the one of the connect.
        self.assertEqual((answer["job"]["state"], answer["status"]["ip"]), ("connected", "192.168.5.7"))

    def test_open_hidden_network_with_password_is_rejected(self):
        session = self.unlock()
        status, _ = self.post("/wifi/connect", {"session": session, "ssid": "Offen", "psk": "abcdefgh",
                                                "hidden": True, "security": ""})
        self.assertEqual(status, 400)

    def test_connect_outcome_survives_failing_status_queries(self):
        session = self.unlock()
        self.helper.release.clear()
        self.post("/wifi/connect", {"session": session, "ssid": "Kunde", "psk": "passwort1"})
        original = self.helper.request

        def busy(payload, timeout=30):
            if payload["command"] == "status":
                return {"ok": False, "error": "NetworkManager antwortet nicht."}
            return original(payload, timeout)

        self.helper.request = busy
        status, answer = self.post("/wifi/status", {"session": session})
        self.assertEqual((status, answer["job"]["state"], answer["message"]), (200, "running", "NetworkManager antwortet nicht."))
        self.helper.release.set()
        for _ in range(50):
            job = self.post("/wifi/status", {"session": session})[1]["job"]
            if job["state"] != "running":
                break
            time.sleep(0.05)
        self.assertEqual(job["state"], "connected")

    def test_lock_ends_the_session(self):
        session = self.unlock()
        self.assertEqual(self.post("/maintenance/lock", {"session": session})[0], 200)
        self.assertEqual(self.post("/wifi/status", {"session": session})[0], 401)

    def test_routes_absent_without_maintenance(self):
        server = LocalScanServer(port=0, allowed_origin=KIOSK)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            request = urllib.request.Request(f"{server.url}/maintenance/unlock", data=b'{"pin": "1"}',
                                             method="POST", headers={"Origin": KIOSK})
            with self.assertRaises(urllib.error.HTTPError) as caught:
                urllib.request.urlopen(request, timeout=5)
            self.assertEqual(caught.exception.code, 404)
            caught.exception.close()
        finally:
            server.shutdown()
            server.server_close()


if __name__ == "__main__":
    unittest.main()
