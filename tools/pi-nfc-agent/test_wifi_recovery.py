"""WLAN recovery tests with a fake nmcli and clock, without NetworkManager or root."""
import fcntl
import json
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch

import wifi_recovery
from wifi_recovery import CommandResult, Recovery, Settings

UUID = "0b9f6e4a-1111-4c2e-9a3f-000000000001"
OTHER = "0b9f6e4a-2222-4c2e-9a3f-000000000002"


class FakeNmcli:
    """Answers the nmcli calls of wifi_recovery from plain attributes."""

    def __init__(self):
        self.running = True
        self.missing = False
        self.radio = "enabled"
        self.devices = [("wlan0", "wifi", "disconnected"), ("eth0", "ethernet", "unavailable"),
                        ("lo", "loopback", "connected (externally)"),
                        ("tailscale0", "tun", "connected (externally)")]
        self.reason = "7 (Secrets were required, but not provided)"
        # uuid, type, autoconnect, priority, timestamp, interface-name
        self.profiles = [(UUID, "802-11-wireless", "yes", "0", "1760300000", "")]
        self.up_results = []
        self.radio_results = {}
        self.calls = []

    def __call__(self, args, timeout):
        if self.missing:
            raise FileNotFoundError("nmcli")
        self.calls.append(list(args))
        if args == ["-t", "-f", "RUNNING", "general"]:
            return CommandResult(0 if self.running else 8, "running\n" if self.running else "",
                                 "" if self.running else "Error: NetworkManager is not running.")
        if args == ["radio", "wifi"]:
            return CommandResult(0, self.radio + "\n")
        if args[:2] == ["radio", "wifi"]:
            result = self.radio_results.get(args[2], CommandResult(0))
            if result.returncode == 0:
                self.radio = "enabled" if args[2] == "on" else "disabled"
            return result
        if args[-2:] == ["device", "status"]:
            return CommandResult(0, "".join(f"{n}:{t}:{s}\n" for n, t, s in self.devices))
        if args[-3:-1] == ["device", "show"]:
            state = next(s for n, _, s in self.devices if n == args[-1])
            return CommandResult(0, f"GENERAL.STATE:30 ({state})\nGENERAL.REASON:{self.reason}\n")
        if args[-2:] == ["connection", "show"]:
            return CommandResult(0, "".join(":".join(p[:5]) + "\n" for p in self.profiles))
        if args[:2] == ["-g", "connection.interface-name"]:
            return CommandResult(0, next(p[5] for p in self.profiles if p[0] == args[-1]) + "\n")
        if args == ["connection", "reload"]:
            return CommandResult(0)
        if "up" in args:
            return self.up_results.pop(0) if self.up_results else CommandResult(0)
        raise AssertionError(f"unexpected nmcli call {args}")

    def set_wifi(self, state):
        self.devices[0] = ("wlan0", "wifi", state)

    def ups(self):
        return [call for call in self.calls if "up" in call]

    def radio_switches(self):
        return [call[2] for call in self.calls if call[:2] == ["radio", "wifi"] and len(call) == 3]


class RecoveryTests(unittest.TestCase):
    def setUp(self):
        self.nmcli = FakeNmcli()
        self.now = 1000.0
        self.logs = []
        self.state = {}
        self.settings = Settings()
        self.persisted = []
        self.recovery = Recovery(self.nmcli, clock=lambda: self.now, sleep=lambda seconds: None,
                                 log=self.logs.append, persist=lambda state: self.persisted.append(dict(state)))

    def tick(self, advance=0):
        self.now += advance
        return self.recovery.tick(self.state, self.settings).action

    def test_connected_wifi_is_left_alone(self):
        self.nmcli.set_wifi("connected")
        for _ in range(5):
            self.assertEqual(self.tick(60), wifi_recovery.NONE)
        self.assertEqual(self.nmcli.ups(), [])
        self.assertEqual(self.logs, [])

    def test_short_outage_triggers_nothing(self):
        self.assertEqual(self.tick(), wifi_recovery.WAIT)
        self.assertEqual(self.tick(60), wifi_recovery.WAIT)
        self.nmcli.set_wifi("connected")
        self.assertEqual(self.tick(45), wifi_recovery.NONE)
        self.assertEqual(self.nmcli.ups(), [])
        self.assertEqual(self.state.get("disconnected_since"), None)
        self.assertIn("wieder verbunden nach 105 s", self.logs[-1])
        # A new outage starts its own grace period.
        self.nmcli.set_wifi("disconnected")
        self.assertEqual(self.tick(10), wifi_recovery.WAIT)
        self.assertEqual(self.tick(60), wifi_recovery.WAIT)

    def test_long_outage_activates_saved_profile_without_secrets(self):
        self.tick()
        self.assertEqual(self.tick(120), wifi_recovery.ACTIVATE)
        self.assertEqual(self.nmcli.ups(), [["--wait", "30", "connection", "up", "uuid", UUID, "ifname", "wlan0"]])
        self.assertIn(["connection", "reload"], self.nmcli.calls)
        for call in self.nmcli.calls:
            self.assertFalse({"--ask", "-a", "-s", "--show-secrets"} & set(call), call)
        self.assertIn("Secrets were required", self.logs[0])
        self.assertIn("Profil aktiviert", self.logs[-1])
        self.nmcli.set_wifi("connected")
        self.assertEqual(self.tick(30), wifi_recovery.NONE)
        self.assertIn("wieder verbunden", self.logs[-1])
        self.assertEqual(self.state.get("attempts"), None)

    def test_running_connection_attempt_is_not_interrupted(self):
        self.nmcli.set_wifi("connecting (need authentication)")
        self.tick()
        self.assertEqual(self.tick(150), wifi_recovery.WAIT)
        # NetworkManager gives up on need-auth itself; a hanging attempt is interrupted.
        self.assertEqual(self.tick(30), wifi_recovery.ACTIVATE)

    def test_connecting_after_long_disconnect_gets_its_own_window(self):
        self.tick()
        self.nmcli.set_wifi("connecting (configuring)")
        self.assertEqual(self.tick(150), wifi_recovery.WAIT)
        self.assertEqual(self.tick(170), wifi_recovery.WAIT)
        self.assertEqual(self.tick(15), wifi_recovery.ACTIVATE)

    def test_deliberate_or_unrecoverable_states_are_skipped(self):
        cases = {
            "radio off": lambda n: setattr(n, "radio", "disabled"),
            "unmanaged": lambda n: n.set_wifi("unmanaged"),
            "unavailable": lambda n: n.set_wifi("unavailable"),
            "networkmanager down": lambda n: setattr(n, "running", False),
            "nmcli missing": lambda n: setattr(n, "missing", True),
            "ethernet": lambda n: n.devices.__setitem__(1, ("eth0", "ethernet", "connected")),
            "no wifi device": lambda n: n.devices.pop(0),
            "no autoconnect profile": lambda n: setattr(n, "profiles", [(UUID, "802-11-wireless", "no", "0", "1", "")]),
            "profile bound elsewhere": lambda n: setattr(n, "profiles", [(UUID, "802-11-wireless", "yes", "0", "1", "wlan1")]),
        }
        for name, change in cases.items():
            with self.subTest(name):
                self.setUp()
                change(self.nmcli)
                for _ in range(6):
                    self.assertEqual(self.tick(60), wifi_recovery.SKIP)
                self.assertEqual(self.nmcli.ups(), [])
                self.assertEqual(self.nmcli.radio_switches(), [])
                self.assertEqual(len(self.logs), 1, self.logs)
                self.assertTrue(self.logs[0].startswith("Keine WLAN-Recovery"), self.logs)

    def test_disabled_in_config(self):
        self.settings.enabled = False
        for _ in range(5):
            self.assertEqual(self.tick(120), wifi_recovery.SKIP)
        self.assertEqual(self.nmcli.ups(), [])
        self.assertEqual(self.logs, ["Keine WLAN-Recovery: per config.json abgeschaltet."])

    def test_skip_ends_outage(self):
        self.tick()
        self.nmcli.radio = "disabled"
        self.assertEqual(self.tick(100), wifi_recovery.SKIP)
        self.nmcli.radio = "enabled"
        self.assertEqual(self.tick(60), wifi_recovery.WAIT)

    def test_failures_back_off_and_timeouts_count(self):
        self.nmcli.up_results = [CommandResult(4, "", "Error: Connection activation failed: Secrets were required."),
                                 CommandResult(None)]
        self.tick()
        self.assertEqual(self.tick(120), wifi_recovery.ACTIVATE)
        self.assertIn("Exitcode 4", self.logs[-1])
        self.assertIn("Secrets were required", self.logs[-1])
        self.assertIn("60 s", self.logs[-1])
        self.assertEqual(self.tick(45), wifi_recovery.WAIT)
        self.assertEqual(self.tick(15), wifi_recovery.ACTIVATE)
        self.assertIn("Timeout", self.logs[-1])
        self.assertEqual(self.tick(119), wifi_recovery.WAIT)
        self.assertEqual(self.tick(1), wifi_recovery.ACTIVATE)
        self.assertEqual(len(self.nmcli.ups()), 3)
        self.assertEqual(self.nmcli.calls.count(["connection", "reload"]), 1)

    def test_backoff_is_capped(self):
        self.assertEqual([wifi_recovery.backoff(n) for n in range(1, 8)], [60, 120, 240, 480, 900, 900, 900])

    def test_radio_reset_after_repeated_failures_then_cooldown(self):
        self.nmcli.up_results = [CommandResult(4)] * 20
        self.tick()
        self.tick(120)
        self.tick(60)
        self.tick(120)
        self.assertEqual(len(self.nmcli.ups()), 3)
        self.assertEqual(self.tick(240), wifi_recovery.RADIO_RESET)
        self.assertEqual(self.nmcli.radio_switches(), ["off", "on"])
        self.assertTrue(self.persisted[-1]["radio_reset_pending"])
        self.assertFalse(self.state["radio_reset_pending"])
        self.assertEqual(self.tick(60), wifi_recovery.ACTIVATE)
        # Further failures within the cooldown only activate again.
        self.assertEqual(self.tick(900), wifi_recovery.ACTIVATE)
        self.assertEqual(self.nmcli.radio_switches(), ["off", "on"])
        self.assertEqual(self.tick(900), wifi_recovery.RADIO_RESET)

    def test_radio_reset_can_be_disabled(self):
        self.settings.radio_reset = False
        self.nmcli.up_results = [CommandResult(4)] * 20
        self.tick()
        for _ in range(10):
            self.tick(900)
        self.assertEqual(self.nmcli.radio_switches(), [])

    def test_radio_stays_on_when_switch_off_fails(self):
        self.state.update(disconnected_since=0, attempts=3, next_attempt_at=0)
        self.nmcli.radio_results["off"] = CommandResult(1, "", "Error: not authorized")
        self.assertEqual(self.tick(), wifi_recovery.RADIO_RESET)
        self.assertEqual(self.nmcli.radio, "enabled")
        self.assertFalse(self.state["radio_reset_pending"])

    def test_interrupted_radio_reset_is_repaired(self):
        self.nmcli.radio = "disabled"
        self.state["radio_reset_pending"] = True
        self.assertEqual(self.tick(), wifi_recovery.RESTORE_RADIO)
        self.assertEqual(self.nmcli.radio, "enabled")
        self.assertFalse(self.state["radio_reset_pending"])
        # Afterwards a deliberate switch-off is respected.
        self.nmcli.radio = "disabled"
        self.assertEqual(self.tick(60), wifi_recovery.SKIP)
        self.assertEqual(self.nmcli.radio_switches(), ["on"])

    def test_failed_radio_restore_is_retried(self):
        self.nmcli.radio = "disabled"
        self.state["radio_reset_pending"] = True
        self.nmcli.radio_results["on"] = CommandResult(1, "", "Error")
        self.assertEqual(self.tick(), wifi_recovery.RESTORE_RADIO)
        self.assertTrue(self.state["radio_reset_pending"])
        del self.nmcli.radio_results["on"]
        self.assertEqual(self.tick(60), wifi_recovery.RESTORE_RADIO)
        self.assertEqual(self.nmcli.radio, "enabled")

    def test_configured_interface_and_uuid(self):
        self.nmcli.devices.insert(0, ("wlan1", "wifi", "connected"))
        self.nmcli.profiles.append((OTHER, "802-11-wireless", "no", "0", "1", ""))
        self.settings.interface = "wlan0"
        self.settings.connection_uuid = OTHER
        self.tick()
        self.assertEqual(self.tick(120), wifi_recovery.ACTIVATE)
        self.assertEqual(self.nmcli.ups()[0][-4:], ["uuid", OTHER, "ifname", "wlan0"])

    def test_missing_configured_profile_is_reported(self):
        self.settings.connection_uuid = OTHER
        self.assertEqual(self.tick(), wifi_recovery.SKIP)
        self.assertIn(f"{OTHER} fehlt", self.logs[0])


class ProfileSelectionTests(unittest.TestCase):
    @staticmethod
    def profile(uuid, priority=0, timestamp=0, autoconnect=True, interface="", kind="802-11-wireless"):
        return {"uuid": uuid, "type": kind, "autoconnect": autoconnect, "priority": priority,
                "timestamp": timestamp, "interface_name": interface}

    def test_priority_then_last_success(self):
        profiles = [self.profile("old", timestamp=10), self.profile("recent", timestamp=20),
                    self.profile("ethernet", priority=9, kind="802-3-ethernet")]
        self.assertEqual(wifi_recovery.select_profile(profiles, "wlan0", "")[0], "recent")
        profiles.append(self.profile("preferred", priority=5, timestamp=1))
        self.assertEqual(wifi_recovery.select_profile(profiles, "wlan0", "")[0], "preferred")

    def test_interface_binding_and_autoconnect(self):
        profiles = [self.profile("other-device", priority=9, interface="wlan1"),
                    self.profile("manual", priority=8, autoconnect=False),
                    self.profile("bound", interface="wlan0")]
        self.assertEqual(wifi_recovery.select_profile(profiles, "wlan0", "")[0], "bound")

    def test_terse_escaping(self):
        self.assertEqual(wifi_recovery.split_terse(r"a\:b:c\\d:"), ["a:b", "c\\d", ""])


class ConfigAndMainTests(unittest.TestCase):
    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.root = Path(folder.name)
        self.config = self.root / "config.json"

    def test_defaults_for_missing_or_broken_config(self):
        self.assertEqual(wifi_recovery.load_settings(self.root / "missing.json"), Settings())
        self.config.write_text("{kaputt")
        self.assertEqual(wifi_recovery.load_settings(self.config), Settings())
        # Existing config.json without the block, as on every installed Pi.
        self.config.write_text(json.dumps({"api_base_url": "https://x", "wifi_recovery": "ja"}))
        self.assertEqual(wifi_recovery.load_settings(self.config), Settings())

    def test_settings_from_config(self):
        self.config.write_text(json.dumps({"wifi_recovery": {
            "enabled": False, "interface": " wlan1 ", "connection_uuid": UUID,
            "disconnect_grace_seconds": 5, "radio_reset": False}}))
        self.assertEqual(wifi_recovery.load_settings(self.config),
                         Settings(False, "wlan1", UUID, wifi_recovery.MIN_GRACE_SECONDS, False))

    def test_main_persists_state_between_runs(self):
        nmcli = FakeNmcli()
        with patch.object(wifi_recovery, "STATE_DIR", self.root / "state"):
            self.assertEqual(wifi_recovery.main(["--config", str(self.config)], nmcli), 0)
            first = json.loads((self.root / "state" / "state.json").read_text())
            self.assertIsNotNone(first["disconnected_since"])
            self.assertEqual(wifi_recovery.main(["--config", str(self.config)], nmcli), 0)
            second = json.loads((self.root / "state" / "state.json").read_text())
            self.assertEqual(second["disconnected_since"], first["disconnected_since"])
            self.assertEqual(oct((self.root / "state").stat().st_mode & 0o777), "0o700")

    def test_parallel_run_is_skipped(self):
        nmcli = FakeNmcli()
        state = self.root / "state"
        state.mkdir()
        with patch.object(wifi_recovery, "STATE_DIR", state), (state / "lock").open("a") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            result = []
            # flock locks per open file description: a second open in another thread conflicts.
            thread = threading.Thread(target=lambda: result.append(
                wifi_recovery.main(["--config", str(self.config)], nmcli)))
            thread.start()
            thread.join(5)
            self.assertEqual(result, [0])
            self.assertEqual(nmcli.calls, [])
            self.assertFalse((state / "state.json").exists())


if __name__ == "__main__":
    unittest.main()
