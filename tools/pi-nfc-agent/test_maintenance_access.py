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
from maintenance_access import (FREE_ATTEMPTS, LOCK_BASE_SECONDS, SESSION_SECONDS, MaintenanceAccess,
                                WifiSetupUnavailable, verify_pin)
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

    def request(self, payload, timeout=30):
        self.requests.append(payload)
        if not self.available:
            raise WifiSetupUnavailable("FileNotFoundError")
        if payload["command"] == "status":
            return {"ok": True, "status": {"state": "connected", "ssid": "Kunde", "ip": "192.168.1.23"}}
        if payload["command"] == "scan":
            return {"ok": True, "networks": [{"ssid": "Kunde", "signal": 80, "security": "WPA2", "inUse": True}]}
        self.release.wait(5)
        return self.connect_answer


class VerifyPinTest(unittest.TestCase):
    def test_matches_server_format(self):
        stored = pin_hash(PIN)
        self.assertTrue(verify_pin(PIN, stored))
        self.assertFalse(verify_pin("482916", stored))
        for broken in (None, "", "plain", stored.replace("pbkdf2-sha256", "md5"),
                       f"pbkdf2-sha256$9999999${SALT}${'cd' * 32}"):
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
        for body in ({"ssid": ""}, {"ssid": "x" * 33}, {"ssid": "Kunde", "psk": 5}, {"ssid": "Kunde", "psk": "x" * 65}):
            self.assertEqual(self.post("/wifi/connect", {"session": session, **body})[0], 400, body)
        self.helper.available = False
        self.assertEqual(self.post("/wifi/networks", {"session": session}), (503, {"error": "wifi setup unavailable"}))
        status, answer = self.post("/wifi/status", {"session": session})
        self.assertEqual((status, answer["status"], answer["job"]), (200, None, {"state": "idle"}))

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
