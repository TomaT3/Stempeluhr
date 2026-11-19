"""Tests of wifi_setup.py with a fake nmcli, without NetworkManager or root."""
import fcntl
import io
import json
import os
from pathlib import Path
import re
import stat
import tempfile
import threading
import unittest
import urllib.error

import wifi_setup
from wifi_recovery import CommandResult
from wifi_setup import NetworkManager, RecoveryPause, SetupError

OLD = "0b9f6e4a-1111-4c2e-9a3f-000000000001"
SECRET = "geheim pass\\wort!"
PIN_HASH = "pbkdf2-sha256$200000$" + "ab" * 16 + "$" + "cd" * 32


class FakeNmcli:
    """NetworkManager in memory: profiles, one WLAN device, a scan list."""

    def __init__(self):
        self.calls = []
        self.state, self.connection = "connected", "Altes WLAN"
        # name, uuid
        self.profiles = [("Altes WLAN", OLD)]
        self.active = OLD
        self.scan = ("*:Altes WLAN:70:WPA2\n:Kunde:40:WPA2\n:Kunde:80:WPA2\n:Offen:50:\n"
                     ":Neu3:60:WPA3\n:Firma:55:WPA2 802.1X\n::30:WPA2\n")
        self.up_results = {}
        self.rescan_fails = False
        self.dir = None

    def __call__(self, args, timeout):
        self.calls.append(list(args))
        if args[:3] == ["-t", "-f", "DEVICE,TYPE,STATE,CONNECTION"]:
            return CommandResult(0, f"wlan0:wifi:{self.state}:{self.connection}\neth0:ethernet:unavailable:\n")
        if args[:3] == ["-t", "-f", "NAME,UUID,TYPE,DEVICE"]:
            active = "--active" in args
            return CommandResult(0, "".join(
                f"{name}:{uuid}:802-11-wireless:{'wlan0' if uuid == self.active else ''}\n"
                for name, uuid in self.profiles if not active or uuid == self.active))
        if args[:2] == ["connection", "load"]:
            content = Path(args[2]).read_text()
            name = re.search(r"^id=(.*)$", content, re.M).group(1)
            uuid = re.search(r"^uuid=(.*)$", content, re.M).group(1)
            self.profiles = [p for p in self.profiles if p[1] != uuid] + [(name, uuid)]
            return CommandResult(0)
        if args[:2] == ["connection", "delete"]:
            self.profiles = [p for p in self.profiles if p[1] != args[3]]
            # Like NetworkManager: the keyfile goes with the profile.
            for path in (self.dir.glob("*.nmconnection") if self.dir else []):
                if f"uuid={args[3]}" in path.read_text():
                    path.unlink()
            return CommandResult(0)
        if "up" in args:
            uuid = args[args.index("uuid") + 1]
            result = self.up_results.get(uuid, CommandResult(0))
            if result.returncode == 0:
                self.active = uuid
                self.connection = next(name for name, u in self.profiles if u == uuid)
            return result
        if args[:3] == ["-t", "-f", "IN-USE,SSID,SIGNAL,SECURITY"]:
            if self.rescan_fails and args[-1] == "yes":
                return CommandResult(1, "", "Error: Scanning not allowed while already scanning.")
            return CommandResult(0, self.scan)
        if args[:2] == ["-g", "IP4.ADDRESS"]:
            return CommandResult(0, "192.168.1.23/24 | 10.0.0.2/8\n")
        if args[:3] == ["-t", "-f", "ACTIVE,SSID"]:
            return CommandResult(0, f"yes:{self.connection}\nno:Kunde\n")
        raise AssertionError(f"unexpected nmcli call {args}")

    def argv(self):
        return " ".join(" ".join(call) for call in self.calls)


class SetupTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self.tmp.name)
        self.nmcli = FakeNmcli()
        self.nmcli.dir = self.dir
        self.logs = []
        self.manager = NetworkManager(self.nmcli, self.dir, self.logs.append)

    def tearDown(self):
        self.tmp.cleanup()

    def files(self):
        return sorted(path.name for path in self.dir.glob("*.nmconnection"))

    def assert_secret_hidden(self, secret=SECRET):
        self.assertNotIn(secret, self.nmcli.argv())
        self.assertNotIn(secret, "\n".join(self.logs))


class KeyfileTest(unittest.TestCase):
    def test_ssid_as_bytes_and_psk_escaped(self):
        content = wifi_setup.keyfile("stempeluhr-wlan", OLD, "Kü;1", " a\\b ", "wpa-psk", 10)
        self.assertIn("ssid=75;195;188;59;49;\n", content)
        self.assertIn("psk=\\sa\\\\b\\s\n", content)
        self.assertIn("autoconnect-priority=10\n", content)
        self.assertNotIn("hidden", content)

    def test_open_network_without_security(self):
        content = wifi_setup.keyfile("x", OLD, "Offen", "", None, 10, hidden=True)
        self.assertNotIn("[wifi-security]", content)
        self.assertIn("hidden=true", content)

    def test_key_management(self):
        self.assertEqual(wifi_setup.key_management("WPA1 WPA2", "passwort1"), "wpa-psk")
        self.assertEqual(wifi_setup.key_management("WPA2 WPA3", "passwort1"), "wpa-psk")
        self.assertEqual(wifi_setup.key_management("WPA3", "passwort1"), "sae")
        self.assertIsNone(wifi_setup.key_management("", ""))
        for security, psk in (("WPA2 802.1X", "passwort1"), ("WPA2", "")):
            with self.assertRaises(SetupError):
                wifi_setup.key_management(security, psk)

    def test_validation(self):
        for ssid in ("", "x" * 33, "a\nb", None):
            with self.assertRaises(SetupError):
                wifi_setup.validate_ssid(ssid)
        for psk in ("kurz", "x" * 64, "ä" * 10, None):
            with self.assertRaises(SetupError):
                wifi_setup.validate_psk(psk)
        self.assertEqual(wifi_setup.validate_psk("a" * 64), "a" * 64)


class ConnectTest(SetupTest):
    def test_success_replaces_old_customer_profile(self):
        old_customer = "0b9f6e4a-3333-4c2e-9a3f-000000000003"
        self.nmcli.profiles.append(("stempeluhr-wlan", old_customer))

        status = self.manager.connect("Kunde", SECRET)

        self.assertEqual(status["state"], "connected")
        self.assertEqual(status["ip"], "192.168.1.23")
        names = [name for name, _ in self.nmcli.profiles]
        self.assertEqual(names.count("stempeluhr-wlan"), 1)
        self.assertNotIn(old_customer, [uuid for _, uuid in self.nmcli.profiles])
        [file_name] = self.files()
        path = self.dir / file_name
        self.assertEqual(stat.S_IMODE(path.stat().st_mode), 0o600)
        self.assertIn("key-mgmt=wpa-psk", path.read_text())
        self.assertIn("autoconnect-priority=10", path.read_text())
        self.assert_secret_hidden()

    def test_wrong_password_restores_previous_connection(self):
        original_call = self.nmcli.__call__

        def failing(args, timeout):
            if "up" in args and args[args.index("uuid") + 1] != OLD:
                self.nmcli.calls.append(list(args))
                return CommandResult(4, "", "Error: Connection activation failed: Secrets were required, "
                                            "but not provided.")
            return original_call(args, timeout)

        manager = NetworkManager(failing, self.dir, self.logs.append)
        with self.assertRaisesRegex(SetupError, "Passwort prüfen"):
            manager.connect("Kunde", SECRET)

        self.assertEqual(self.files(), [])
        self.assertEqual(self.nmcli.profiles, [("Altes WLAN", OLD)])
        self.assertEqual(self.nmcli.calls[-1][-4:], ["uuid", OLD, "ifname", "wlan0"])
        self.assert_secret_hidden()

    def test_timeout_reports_timeout(self):
        def hanging(args, timeout):
            if "up" in args and args[args.index("uuid") + 1] != OLD:
                return CommandResult(None)
            return self.nmcli(args, timeout)

        with self.assertRaisesRegex(SetupError, "Zeitüberschreitung"):
            NetworkManager(hanging, self.dir, self.logs.append).connect("Kunde", SECRET)

    def test_unknown_network_writes_nothing(self):
        with self.assertRaisesRegex(SetupError, "nicht gefunden"):
            self.manager.connect("Nachbar", SECRET)
        self.assertEqual(self.files(), [])

    def test_hidden_network_skips_scan(self):
        self.manager.connect("Versteckt", SECRET, hidden=True)
        [file_name] = self.files()
        self.assertIn("hidden=true", (self.dir / file_name).read_text())
        self.assertFalse(any(call[:3] == ["-t", "-f", "IN-USE,SSID,SIGNAL,SECURITY"] for call in self.nmcli.calls))

    def test_wpa3_and_open_networks(self):
        self.manager.connect("Neu3", "passwort1")
        self.assertIn("key-mgmt=sae", (self.dir / self.files()[0]).read_text())
        self.manager.connect("Offen", "")
        self.assertNotIn("[wifi-security]", (self.dir / self.files()[0]).read_text())


class ScanStatusTest(SetupTest):
    def test_scan_merges_duplicates_and_sorts(self):
        networks = self.manager.scan()
        self.assertEqual([n["ssid"] for n in networks], ["Altes WLAN", "Kunde", "Neu3", "Firma", "Offen"])
        self.assertEqual(networks[1]["signal"], 80)
        self.assertTrue(networks[0]["inUse"])
        self.assertEqual(networks[-1]["security"], "")

    def test_scan_falls_back_to_last_result(self):
        self.nmcli.rescan_fails = True
        self.assertEqual(len(self.manager.scan()), 5)

    def test_status(self):
        status = self.manager.status()
        self.assertEqual((status["ssid"], status["ip"], status["serviceWifi"]), ("Altes WLAN", "192.168.1.23", False))
        self.nmcli.state, self.nmcli.connection = "disconnected", ""
        self.assertEqual(self.manager.status()["ip"], None)


class ServiceWifiTest(SetupTest):
    WIFI = {"ssid": "Stempeluhr-Service", "psk": "hotspot-geheim"}

    def loads(self):
        return sum(1 for call in self.nmcli.calls if call[:2] == ["connection", "load"])

    def test_create_unchanged_and_update(self):
        self.assertEqual(self.manager.sync_service_wifi(self.WIFI), "eingerichtet")
        path = self.dir / "stempeluhr-service.nmconnection"
        self.assertEqual(stat.S_IMODE(path.stat().st_mode), 0o600)
        self.assertIn("autoconnect-priority=-10", path.read_text())
        uuid = wifi_setup.uuid_of(path.read_text())

        self.assertEqual(self.manager.sync_service_wifi(self.WIFI), "unverändert")
        self.assertEqual(self.loads(), 1)

        self.assertEqual(self.manager.sync_service_wifi({**self.WIFI, "psk": "neues-geheim"}), "eingerichtet")
        self.assertEqual(wifi_setup.uuid_of(path.read_text()), uuid)
        self.assertEqual([u for name, u in self.nmcli.profiles if name == "stempeluhr-service"], [uuid])
        self.assert_secret_hidden("hotspot-geheim")
        self.assert_secret_hidden("neues-geheim")

    def test_replaces_profile_created_by_hand(self):
        by_hand = "0b9f6e4a-4444-4c2e-9a3f-000000000004"
        self.nmcli.profiles.append(("stempeluhr-service", by_hand))
        self.manager.sync_service_wifi(self.WIFI)
        names = [(name, uuid) for name, uuid in self.nmcli.profiles if name == "stempeluhr-service"]
        self.assertEqual(len(names), 1)
        self.assertNotEqual(names[0][1], by_hand)

    def test_removed_on_server(self):
        self.manager.sync_service_wifi(self.WIFI)
        self.assertEqual(self.manager.sync_service_wifi(None), "entfernt")
        self.assertEqual(self.files(), [])
        self.assertNotIn("stempeluhr-service", [name for name, _ in self.nmcli.profiles])
        self.assertEqual(self.manager.sync_service_wifi(None), "unverändert")

    def test_pin_hash_file(self):
        path = self.dir / "maintenance.json"
        self.assertEqual(wifi_setup.sync_pin_hash(PIN_HASH, path), "eingerichtet")
        self.assertEqual(json.loads(path.read_text()), {"maintenancePinHash": PIN_HASH})
        self.assertEqual(stat.S_IMODE(path.stat().st_mode), 0o640)
        self.assertEqual(wifi_setup.sync_pin_hash(PIN_HASH, path), "unverändert")
        with self.assertRaises(SetupError):
            wifi_setup.sync_pin_hash("md5$1$00$00", path)
        self.assertEqual(wifi_setup.sync_pin_hash(None, path), "entfernt")
        self.assertFalse(path.exists())


class SyncServiceTest(SetupTest):
    def config(self, **values):
        path = self.dir / "config.json"
        path.write_text(json.dumps({"api_base_url": "https://stempeluhr.test/", "terminal_id": "pi-1", **values}))
        return path

    def test_without_token_nothing_is_fetched_or_changed(self):
        self.assertEqual(wifi_setup.fetch_maintenance({"api_base_url": "https://x"}), None)
        result = wifi_setup.sync_service(self.config(), self.manager, wifi_setup.fetch_maintenance, self.logs.append)
        self.assertEqual(result, 0)
        self.assertEqual(self.nmcli.calls, [])

    def test_server_unreachable_keeps_everything(self):
        self.manager.sync_service_wifi({"ssid": "Service", "psk": "hotspot-geheim"})
        before = list(self.nmcli.profiles)

        def offline(_config):
            raise urllib.error.URLError("down")

        wifi_setup.sync_service(self.config(terminal_token="t" * 40), self.manager, offline, self.logs.append)
        self.assertEqual(self.nmcli.profiles, before)
        self.assertIn("nichts geändert", self.logs[-1])

    def test_applies_wifi_and_pin_hash(self):
        answer = {"serviceWifi": {"ssid": "Service", "psk": "hotspot-geheim"}, "maintenancePinHash": PIN_HASH}
        config = self.config(terminal_token="t" * 40)
        wifi_setup.sync_service(config, self.manager, lambda _config: answer, self.logs.append)
        self.assertIn("stempeluhr-service", [name for name, _ in self.nmcli.profiles])
        self.assertTrue((self.dir / "maintenance.json").exists())
        self.assertEqual(self.logs, ["Wartungs-WLAN eingerichtet.", "Wartungs-PIN eingerichtet."])

    def test_invalid_answer_warns_per_part(self):
        answer = {"serviceWifi": {"ssid": "Service", "psk": "kurz"}, "maintenancePinHash": PIN_HASH}
        wifi_setup.sync_service(self.config(terminal_token="t" * 40), self.manager, lambda _c: answer, self.logs.append)
        self.assertTrue(self.logs[0].startswith("WARNUNG: Wartungs-WLAN"))
        self.assertEqual(self.logs[1], "Wartungs-PIN eingerichtet.")


class ServeTest(SetupTest):
    def call(self, raw: bytes):
        out = io.StringIO()
        wifi_setup.serve(io.BytesIO(raw), out, self.manager)
        return json.loads(out.getvalue())

    def test_requests(self):
        self.assertEqual(self.call(b"kaputt\n")["ok"], False)
        self.assertEqual(self.call(b'{"command": "rm"}\n')["error"], "Unbekannter Befehl.")
        self.assertEqual(self.call(b'{"command": "status"}\n')["status"]["ssid"], "Altes WLAN")
        self.assertEqual(len(self.call(b'{"command": "scan"}\n')["networks"]), 5)
        self.assertIn("zu groß", self.call(b"x" * 5000)["error"])
        answer = self.call(b'{"command": "connect", "ssid": "Nachbar", "psk": "passwort1"}\n')
        self.assertEqual(answer, {"ok": False, "error": "WLAN nicht gefunden. Ist es in Reichweite?"})

    def test_connect_pauses_wifi_recovery(self):
        held = []

        def probe():
            pause = RecoveryPause(self.dir / "recovery", wait_seconds=1)
            held.append(pause)
            return pause

        original_connect = self.manager.connect

        def connect(*args):
            # Another open file description, as wifi_recovery.py uses one.
            with (self.dir / "recovery" / "lock").open("a") as lock:
                with self.assertRaises(BlockingIOError):
                    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            return original_connect(*args)

        self.manager.connect = connect
        answer = wifi_setup.handle_request(b'{"command": "connect", "ssid": "Kunde", "psk": "passwort1"}',
                                           self.manager, probe)
        self.assertTrue(answer["ok"], answer)
        self.assertEqual(len(held), 1)
        with (self.dir / "recovery" / "lock").open("a") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)  # released afterwards

    def test_pause_gives_up_after_wait(self):
        directory = self.dir / "recovery"
        directory.mkdir()
        with (directory / "lock").open("a") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            result = []

            def enter():
                with RecoveryPause(directory, wait_seconds=0.2, sleep=lambda _s: None):
                    result.append("entered")

            thread = threading.Thread(target=enter)
            thread.start()
            thread.join(5)
            self.assertEqual(result, ["entered"])


if __name__ == "__main__":
    os.umask(0o022)
    unittest.main()
