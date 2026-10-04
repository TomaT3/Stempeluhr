"""Migration state machine and validation; real system calls are stubbed.

Run on Linux (fcntl/dpkg). Package installation itself is tested separately
in an arm64 Trixie container by test_pcsc_packages.sh.
"""
import hashlib
import io
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import Mock, patch

import pcsc_maintenance as maintenance

VERSION = "2.5.2-1~stempeluhr13.1"
OLD = "2.3.3-1"


class MaintenanceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.addCleanup(self.temp.cleanup)
        for name, value in (("BASE", self.root / "agent"), ("STATE", self.root / "state"),
                            ("CONFIG", self.root / "config.json"),
                            ("USB_RULE", self.root / "udev" / "99-stempeluhr-pcsc.rules"),
                            ("PCSC_RUNTIME", self.root / "run" / "pcscd")):
            p = patch.object(maintenance, name, value)
            p.start()
            self.addCleanup(p.stop)
        maintenance.CONFIG.write_text(json.dumps({"local_port": 8737, "terminal_token": "SECRET"}))

    def manifest(self):
        directory = self.root / "packages"
        directory.mkdir()
        entries = []
        for package in ("pcscd", "libpcsclite1", "libpcsclite-dev"):
            path = directory / f"{package}_{VERSION}_arm64.deb"
            path.write_bytes(package.encode())
            entries.append({"file": path.name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
        (directory / "manifest.json").write_text(json.dumps({"distribution": "trixie", "architecture": "arm64",
            "version": VERSION, "packages": entries}))
        return directory

    def info(self, path):
        package, version, _ = path.name.split("_")
        return [f"Package: {package}", f"Version: {version}", "Architecture: arm64"]

    def test_manifest_validates_digest_and_both_linked_packages(self):
        directory = self.manifest()
        with patch.object(maintenance, "package_info", side_effect=self.info):
            version, packages = maintenance.packages_from_manifest(directory, "arm64")
            self.assertEqual(version, VERSION)
            self.assertEqual(set(packages), maintenance.PACKAGES)
            next(directory.glob("pcscd_*.deb")).write_bytes(b"tampered")
            with self.assertRaisesRegex(ValueError, "prüfsumme"):
                maintenance.packages_from_manifest(directory, "arm64")

    def test_path_traversal_is_rejected_before_reading_a_package(self):
        directory = self.manifest()
        manifest = json.loads((directory / "manifest.json").read_text())
        manifest["packages"][0]["file"] = "../outside.deb"
        (directory / "manifest.json").write_text(json.dumps(manifest))
        with self.assertRaisesRegex(ValueError, "Paketname"):
            maintenance.packages_from_manifest(directory, "arm64")

    def test_simulation_rejects_unrelated_changes_and_removals(self):
        for output in ("Inst libc6 (2.43 Debian)", "Remv pcscd [2.3.3-1]"):
            with patch.object(maintenance, "command", return_value=output):
                with self.assertRaisesRegex(ValueError, "weitere Systempakete"):
                    maintenance.simulate([Path("/tmp/pcscd.deb")])

    def test_repeated_apply_with_a_suitable_newer_version_changes_nothing(self):
        with patch.object(maintenance, "installed", return_value=("2.6.0-1", "arm64")), \
             patch.object(maintenance, "command") as command:
            self.assertIsNone(maintenance.prepare())
            self.assertIsNone(maintenance.prepare())
        command.assert_not_called()

    def test_debian_epoch_does_not_imply_the_upstream_fix_is_present(self):
        with patch.object(maintenance, "installed", return_value=("1:2.3.3-1", "arm64")):
            self.assertFalse(maintenance.suitable())
        with patch.object(maintenance, "installed", return_value=("1:2.5.2-1", "arm64")):
            self.assertTrue(maintenance.suitable())

    def test_unsupported_distribution_changes_no_packages(self):
        with patch.object(maintenance, "suitable", return_value=False), \
             patch.object(maintenance, "distribution", return_value="bookworm"), \
             patch.object(maintenance, "command") as command:
            with self.assertRaisesRegex(ValueError, "nur Trixie"):
                maintenance.prepare()
        command.assert_not_called()

    def prepare(self, download_error=None):
        """prepare() on a stubbed Trixie system; returns (folder, commands)."""
        directory = self.manifest()
        with patch.object(maintenance, "package_info", side_effect=self.info):
            _, packages = maintenance.packages_from_manifest(directory, "arm64")
        calls = []
        def command(*args, **kwargs):
            calls.append(args)
            if args == ("dpkg", "--print-architecture"):
                return "arm64"
            if args == ("apt-mark", "showauto"):
                return "libpcsclite1"
            if args[:2] == ("apt-get", "download"):
                if download_error:
                    raise download_error
                p, version = args[2].split("=")
                (kwargs["cwd"] / f"{p}_{version}_arm64.deb").write_bytes(b"original")
            return ""
        with patch.object(maintenance, "suitable", return_value=False), \
             patch.object(maintenance, "distribution", return_value="trixie"), \
             patch.object(maintenance, "installed", side_effect=lambda p: None if p == "libpcsclite-dev" else (OLD, "arm64")), \
             patch.object(maintenance, "packages_from_manifest", return_value=(VERSION, packages)), \
             patch.object(maintenance, "package_info", side_effect=self.info), \
             patch.object(maintenance, "active", return_value=True), \
             patch.object(maintenance, "browser_alive", return_value=True), \
             patch.object(maintenance, "command", side_effect=command):
            return maintenance.prepare(), calls

    def test_prepare_saves_originals_and_arms_rollback_before_detached_activation(self):
        folder, calls = self.prepare()
        jobs = [args for args in calls if args[0] == "systemd-run"]
        self.assertEqual(len(jobs), 2)
        self.assertIn("--on-active=20m", jobs[0])
        self.assertIn("--rollback", jobs[0])
        self.assertIn("--activate", jobs[1])
        self.assertEqual(len(list((folder / "old").glob("*.deb"))), 2)
        self.assertEqual(len(list((folder / "new").glob("*.deb"))), 2)
        self.assertEqual(folder.stat().st_mode & 0o077, 0)
        self.assertEqual(json.loads((folder / "config.json").read_text())["terminal_token"], "SECRET")
        snapshot = json.loads((folder / "snapshot.json").read_text())
        self.assertEqual(snapshot["auto"], ["libpcsclite1"])
        # A manual run also blocks a later automatic repeat of this version.
        self.assertEqual(json.loads(maintenance.auto_marker().read_text())["targetVersion"], VERSION)
        self.assertTrue(snapshot["browserAlive"])

    def test_prepare_backs_up_existing_usb_rule_before_changing_services(self):
        maintenance.USB_RULE.parent.mkdir()
        maintenance.USB_RULE.write_text("original custom rule")
        maintenance.USB_RULE.chmod(0o600)
        folder, calls = self.prepare()
        snapshot = json.loads((folder / "snapshot.json").read_text())
        self.assertEqual((folder / "usb-rule.rules").read_text(), "original custom rule")
        self.assertEqual(snapshot["usbRule"], {"uid": maintenance.USB_RULE.stat().st_uid,
                                              "gid": maintenance.USB_RULE.stat().st_gid})
        self.assertFalse(any(args[:2] == ("systemctl", "mask") for args in calls))

    def test_failed_original_download_leaves_no_package_copies(self):
        # The updater retries temporary download errors every run.
        for attempt in range(3):
            with self.subTest(attempt=attempt):
                with self.assertRaises(subprocess.CalledProcessError):
                    self.prepare(subprocess.CalledProcessError(100, "apt-get download"))
                self.assertEqual(list(maintenance.STATE.iterdir()), [])
                shutil.rmtree(self.root / "packages")

    def test_failed_reader_probe_starts_rollback_in_its_own_unit(self):
        folder = self.root / "activation"
        folder.mkdir()
        (folder / "new").mkdir()
        (folder / "snapshot.json").write_text(json.dumps({"agentActive": True, "auto": [], "rollbackUnit": "test", "originals": {}}))
        with patch.object(maintenance, "command") as command, \
             patch.object(maintenance, "verify", side_effect=ValueError("reader missing")), \
             patch.object(maintenance, "rollback") as rollback:
            with self.assertRaisesRegex(ValueError, "reader missing"):
                maintenance.activate(folder)
        # Not inside the time-limited activation unit: it could be killed mid-dpkg.
        rollback.assert_not_called()
        self.assertEqual(maintenance.read_result(folder)["status"], "failed")
        job = command.call_args_list[-1].args
        self.assertEqual(job[:2], ("systemd-run", "--unit=test-now"))
        self.assertFalse(any(arg.startswith("--property=RuntimeMaxSec") for arg in job))
        self.assertEqual(job[-2:], ("--rollback", str(folder)))

    def test_result_wait_reports_failure_and_waits_for_the_rollback(self):
        results = [None, {"status": "failed", "detail": "a"}, {"status": "rolled-back", "detail": "b"}]
        with patch.object(maintenance, "read_result", side_effect=results), patch.object(maintenance.time, "sleep"):
            self.assertEqual(maintenance.wait_for_result(self.root), 1)

    def test_success_is_committed_before_cancelling_the_timer(self):
        folder = self.root / "activation"
        folder.mkdir()
        (folder / "new").mkdir()
        (folder / "snapshot.json").write_text(json.dumps({"agentActive": True, "auto": [], "rollbackUnit": "test", "originals": {}}))
        def stop_timer(*args, **kwargs):
            self.assertEqual(maintenance.read_result(folder)["status"], "ok")
            return Mock(returncode=0)
        with patch.object(maintenance, "command"), patch.object(maintenance, "verify"), \
             patch.object(maintenance.subprocess, "run", side_effect=stop_timer):
            maintenance.activate(folder)

    def test_pending_migration_cannot_be_reported_as_already_suitable(self):
        maintenance.STATE.mkdir()
        folder = self.root / "unfinished"
        folder.mkdir()
        (maintenance.STATE / "pending.json").write_text(json.dumps({"folder": str(folder)}))
        with patch.object(maintenance, "suitable", return_value=True):
            with self.assertRaisesRegex(ValueError, "noch offen"):
                maintenance.prepare()
            maintenance.write_result(folder, "ok", "validated")
            self.assertIsNone(maintenance.prepare())

    def test_rollback_restores_and_checks_original_versions_before_reporting_success(self):
        folder = self.root / "rollback"
        folder.mkdir()
        (folder / "old").mkdir()
        (folder / "old" / "pcscd.deb").write_bytes(b"original")
        snapshot = {"agentActive": True, "pcscActive": True, "auto": ["libpcsclite1"],
                    "originals": {"pcscd": [OLD, "arm64"]}, "conffiles": []}
        with patch.object(maintenance, "command") as command, \
             patch.object(maintenance, "installed", return_value=(OLD, "arm64")), \
             patch.object(maintenance, "verify") as verify:
            maintenance.rollback(folder, snapshot)
        self.assertEqual(maintenance.read_result(folder)["status"], "rolled-back")
        # A missing browser heartbeat cannot fail the return to the known-good packages.
        verify.assert_called_once_with(folder, {**snapshot, "browserAlive": False}, require_fix=False)
        calls = [call.args for call in command.call_args_list]
        install = next(i for i, args in enumerate(calls) if "--allow-downgrades" in args)
        # An interrupted dpkg run is completed before the originals are reinstalled.
        self.assertLess(calls.index(("dpkg", "--configure", "-a")), install)

    def test_slow_package_mutations_complete_and_restart_the_agent(self):
        for action in ("activate", "rollback"):
            with self.subTest(action=action):
                folder = self.root / action
                folder.mkdir()
                for name in ("new", "old"):
                    (folder / name).mkdir()
                    (folder / name / "pcscd.deb").write_bytes(b"package")
                snapshot = {"agentActive": True, "pcscActive": True, "auto": [],
                            "originals": {"pcscd": [OLD, "arm64"]}, "conffiles": [],
                            "targetVersion": VERSION, "rollbackUnit": "test"}
                (folder / "snapshot.json").write_text(json.dumps(snapshot))
                current = OLD if action == "activate" else VERSION
                mutations = []
                calls = []

                def run(args, **kwargs):
                    nonlocal current
                    calls.append(tuple(args))
                    if args[0] == "apt-get" or tuple(args) == ("dpkg", "--configure", "-a"):
                        # Simulate three minutes of package work, without sleeping.
                        timeout = kwargs.get("timeout")
                        if timeout is not None and timeout < 180:
                            raise subprocess.TimeoutExpired(args, timeout)
                        mutations.append(args[0])
                        if args[0] == "apt-get":
                            current = VERSION if action == "activate" else OLD
                    stdout = f"installed {current} arm64" if args[0] == "dpkg-query" else ""
                    return Mock(stdout=stdout, returncode=0)

                with patch.object(maintenance.subprocess, "run", side_effect=run), \
                     patch.object(maintenance, "verify"):
                    if action == "activate":
                        maintenance.activate(folder)
                    else:
                        maintenance.rollback(folder, snapshot)
                self.assertEqual(mutations, ["apt-get"] if action == "activate" else ["dpkg", "apt-get"])
                self.assertIn(("systemctl", "start", maintenance.AGENT_SERVICE), calls)
                self.assertEqual(maintenance.read_result(folder)["status"],
                                 "ok" if action == "activate" else "rolled-back")
                mask = ("systemctl", "mask", "--runtime", "--now", *maintenance.PCSC_UNITS)
                unmask = ("systemctl", "unmask", "--runtime", *maintenance.PCSC_UNITS)
                install_index = next(i for i, args in enumerate(calls) if args[0] == "apt-get")
                self.assertLess(calls.index(mask), install_index)
                self.assertLess(install_index, calls.index(unmask))
                self.assertLess(calls.index(unmask), calls.index(("systemctl", "start", "pcscd.service")))
                if action == "activate":
                    self.assertLess(calls.index(("udevadm", "settle", "--timeout=30")), calls.index(unmask))

    def test_wrong_restored_version_cannot_count_as_a_successful_rollback(self):
        folder = self.root / "rollback"
        folder.mkdir()
        (folder / "old").mkdir()
        snapshot = {"auto": [], "originals": {"pcscd": [OLD, "arm64"]}, "conffiles": [],
                    "targetVersion": VERSION}
        with patch.object(maintenance, "command"), \
             patch.object(maintenance, "installed", return_value=(VERSION, "arm64")), \
             patch.object(maintenance.subprocess, "run", return_value=Mock(returncode=0)):
            with self.assertRaisesRegex(ValueError, "Originalversion"):
                maintenance.rollback(folder, snapshot)
        self.assertIsNone(maintenance.read_result(folder))

    def test_external_newer_version_is_never_downgraded_by_rollback(self):
        snapshot = {"originals": {"pcscd": [OLD, "arm64"]}, "targetVersion": VERSION, "agentActive": False}
        with patch.object(maintenance, "installed", return_value=("2.6.0-1", "arm64")), \
             patch.object(maintenance, "command") as command:
            with self.assertRaisesRegex(ValueError, "kein automatisches Downgrade"):
                maintenance.rollback(self.root, snapshot)
        command.assert_not_called()

    def test_package_changes_between_prepare_and_activate_do_not_interrupt_services(self):
        folder = self.root / "activation"
        folder.mkdir()
        (folder / "snapshot.json").write_text(json.dumps({"originals": {"pcscd": [OLD, "arm64"]}, "rollbackUnit": "test"}))
        with patch.object(maintenance, "installed", return_value=("2.6.0-1", "arm64")), \
             patch.object(maintenance, "command") as command, \
             patch.object(maintenance.subprocess, "run", return_value=Mock(returncode=0)):
            maintenance.activate(folder)
        command.assert_not_called()
        self.assertEqual(maintenance.read_result(folder)["status"], "unchanged")

    def verify_with(self, snapshot, ui_status, clock=None):
        (self.root / "probe_reader.py").write_text("print('probe')")
        responses = {"/health": {"ok": True}, "/diagnostics": {"uiStatus": ui_status}}
        with patch.object(maintenance, "suitable", return_value=True), \
             patch.object(maintenance, "command", return_value=""), \
             patch.object(maintenance, "active", return_value=True), \
             patch.object(maintenance, "http_json", side_effect=lambda port, path: responses[path]), \
             patch.object(maintenance.time, "sleep"), \
             patch.object(maintenance.time, "monotonic", side_effect=clock or [0, 0]):
            maintenance.verify(self.root, {"readerFilter": "ACR122", "port": 8737, "agentActive": True, **snapshot})

    def test_browser_heartbeat_is_required_only_if_it_existed_before(self):
        self.verify_with({"browserAlive": False}, "not-seen")
        self.verify_with({"browserAlive": True}, "alive")
        with self.assertRaisesRegex(ValueError, "Browser-Lebenszeichen fehlt"):
            self.verify_with({"browserAlive": True}, "not-seen", clock=[0, 0, 100])

    def test_browser_detection_waits_for_the_first_heartbeat_after_a_restart(self):
        with patch.object(maintenance, "http_json", side_effect=[{"uiStatus": "not-seen"}, {"uiStatus": "alive"}]), \
             patch.object(maintenance.time, "sleep"):
            self.assertTrue(maintenance.browser_alive(8737))
        # Fresh install: no kiosk page ever reports.
        with patch.object(maintenance, "http_json", return_value={"uiStatus": "not-seen"}), \
             patch.object(maintenance.time, "sleep"), \
             patch.object(maintenance.time, "monotonic", side_effect=[0, 10, 31]):
            self.assertFalse(maintenance.browser_alive(8737))
        with patch.object(maintenance, "http_json", side_effect=OSError("agent down")):
            self.assertFalse(maintenance.browser_alive(8737))

    def test_automatic_migration_is_attempted_once_per_bundle_version(self):
        with patch.object(maintenance, "bundle_version", return_value=VERSION):
            self.assertTrue(maintenance.auto_possible())
            maintenance.record_auto_attempt(VERSION)
            self.assertFalse(maintenance.auto_possible())

    def test_legacy_failed_marker_allows_exactly_one_corrected_attempt(self):
        maintenance.STATE.mkdir()
        maintenance.auto_marker().write_text(json.dumps({"targetVersion": VERSION, "startedAt": 1}))
        with patch.object(maintenance, "bundle_version", return_value=VERSION):
            self.assertTrue(maintenance.auto_possible())
            maintenance.record_auto_attempt(VERSION)
            for _ in range(3):
                self.assertFalse(maintenance.auto_possible())
            with patch.object(maintenance, "MIGRATION_REVISION", maintenance.MIGRATION_REVISION + 1):
                self.assertTrue(maintenance.auto_possible())

    def test_corrected_auto_attempt_keeps_suitable_packages_untouched(self):
        with patch.object(maintenance, "suitable", return_value=True), \
             patch.object(maintenance, "prepare") as prepare, \
             patch.object(maintenance, "command") as command:
            self.assertEqual(self.main("--apply", "--auto"), 0)
        prepare.assert_not_called()
        command.assert_not_called()
        self.assertFalse(maintenance.auto_marker().exists())

    def test_usb_rule_is_scoped_and_activated_for_already_connected_devices(self):
        with patch.object(maintenance, "command") as command:
            maintenance.install_usb_rule()
        rule = maintenance.USB_RULE.read_text()
        self.assertIn('ENV{DEVTYPE}=="usb_device"', rule)
        self.assertIn('ATTR{idVendor}=="072f", ATTR{idProduct}=="2200"', rule)
        self.assertIn('GROUP="pcscd", MODE="0660"', rule)
        self.assertEqual(maintenance.USB_RULE.stat().st_mode & 0o777, 0o644)
        calls = [call.args for call in command.call_args_list]
        self.assertEqual(calls[0], ("udevadm", "control", "--reload-rules"))
        self.assertIn("--attr-match=idProduct=2200", calls[1])
        self.assertEqual(calls[2], ("udevadm", "settle", "--timeout=30"))

    def test_rollback_restores_original_usb_rule_or_removes_new_rule(self):
        for existed in (False, True):
            with self.subTest(existed=existed):
                maintenance.USB_RULE.parent.mkdir(exist_ok=True)
                maintenance.USB_RULE.write_text("new rule")
                backup = self.root / "usb-rule.rules"
                backup.write_text("original rule")
                backup.chmod(0o600)
                with patch.object(maintenance, "command") as command, \
                     patch.object(maintenance.os, "chown") as chown:
                    maintenance.restore_usb_rule(self.root, {"usbRule": {"uid": 12, "gid": 34} if existed else None})
                self.assertEqual(maintenance.USB_RULE.exists(), existed)
                if existed:
                    self.assertEqual(maintenance.USB_RULE.read_text(), "original rule")
                    self.assertEqual(maintenance.USB_RULE.stat().st_mode & 0o777, 0o600)
                    chown.assert_called_once_with(maintenance.USB_RULE, 12, 34)
                else:
                    chown.assert_not_called()
                self.assertEqual(command.call_count, 3)

    def test_runtime_reset_removes_old_user_artifacts_but_rejects_unknown_files(self):
        runtime = maintenance.PCSC_RUNTIME
        runtime.mkdir(parents=True)
        (runtime / "pcscd.pid").write_text("123")
        (runtime / "pcscd.comm").touch()
        maintenance.reset_pcsc_runtime()
        self.assertFalse(runtime.exists())
        runtime.mkdir()
        (runtime / "unexpected").touch()
        with self.assertRaises(OSError):
            maintenance.reset_pcsc_runtime()
        self.assertTrue((runtime / "unexpected").exists())

    def test_service_transition_masks_socket_and_recreates_runtime_before_start(self):
        with patch.object(maintenance, "command") as command, \
             patch.object(maintenance, "reset_pcsc_runtime") as reset:
            maintenance.pause_pcsc()
            maintenance.resume_pcsc()
        calls = [call.args for call in command.call_args_list]
        self.assertEqual(calls, [
            ("systemctl", "mask", "--runtime", "--now", "pcscd.socket", "pcscd.service"),
            ("systemctl", "daemon-reload"),
            ("systemd-tmpfiles", "--create", "--prefix=/run/pcscd"),
            ("systemctl", "unmask", "--runtime", "pcscd.socket", "pcscd.service"),
            ("systemctl", "start", "pcscd.service")])
        reset.assert_called_once()

    def test_probe_stderr_is_logged_without_config_or_probe_command(self):
        (self.root / "probe_reader.py").write_text("PUBLIC_PROBE_SOURCE")
        error = subprocess.CalledProcessError(1, "PROBE_COMMAND", stderr="No readers: LIBUSB_ERROR_ACCESS")
        with patch.object(maintenance, "suitable", return_value=True), \
             patch.object(maintenance, "command", side_effect=error), \
             patch.object(maintenance.sys, "stderr", new_callable=io.StringIO) as stderr:
            with self.assertRaises(subprocess.CalledProcessError):
                maintenance.verify(self.root, {"readerFilter": "ACR122"})
        self.assertIn("LIBUSB_ERROR_ACCESS", stderr.getvalue())
        self.assertNotIn("PUBLIC_PROBE_SOURCE", stderr.getvalue())
        self.assertNotIn("PROBE_COMMAND", stderr.getvalue())
        self.assertNotIn("SECRET", stderr.getvalue())
        with patch.object(maintenance, "bundle_version", return_value="2.5.3-1~stempeluhr13.1"):
            self.assertTrue(maintenance.auto_possible())
        # Bundles without packages (plain script builds) never migrate automatically.
        with patch.object(maintenance, "bundle_version", return_value=None):
            self.assertFalse(maintenance.auto_possible())

    def test_a_running_migration_is_not_reported_as_needing_manual_action(self):
        maintenance.STATE.mkdir()
        folder = self.root / "running"
        folder.mkdir()
        (maintenance.STATE / "pending.json").write_text(json.dumps({"folder": str(folder)}))
        self.assertTrue(maintenance.migration_running())
        maintenance.write_result(folder, "failed", "rollback follows")
        self.assertTrue(maintenance.migration_running())
        maintenance.write_result(folder, "rolled-back", "done")
        self.assertFalse(maintenance.migration_running())

    def pending(self, boot, result=None):
        maintenance.STATE.mkdir(exist_ok=True)
        folder = self.root / "interrupted"
        folder.mkdir(exist_ok=True)
        (folder / "snapshot.json").write_text(json.dumps({"rollbackUnit": "rollback-unit"}))
        (maintenance.STATE / "pending.json").write_text(json.dumps({"folder": str(folder), "bootId": boot}))
        if result:
            maintenance.write_result(folder, result, "")
        return folder

    def main(self, *args):
        with patch.object(maintenance.sys, "argv", ["pcsc_maintenance.py", *args]), \
             patch.object(maintenance.os, "geteuid", return_value=0, create=True):
            return maintenance.main()

    def test_a_reboot_during_the_migration_is_detected_and_rolled_back(self):
        folder = self.pending("boot-1", "failed")
        with patch.object(maintenance, "current_boot", return_value="boot-1"):
            self.assertIsNone(maintenance.interrupted_migration())
            self.assertTrue(maintenance.migration_running())
        with patch.object(maintenance, "current_boot", return_value="boot-2"):
            # Transient units and rollback timer are gone: not "running" forever.
            self.assertEqual(maintenance.interrupted_migration(), folder)
            self.assertFalse(maintenance.migration_running())
            with patch.object(maintenance, "suitable", return_value=True):
                self.assertEqual(self.main("--check"), 1)

            def rollback_unit(*args, **kwargs):
                maintenance.write_result(folder, "rolled-back", "restored")
            with patch.object(maintenance, "command", side_effect=rollback_unit) as command, \
                 patch.object(maintenance, "suitable", return_value=False), \
                 patch.object(maintenance, "prepare") as prepare:
                self.assertEqual(self.main("--apply", "--auto"), 0)
            job = command.call_args.args
            self.assertEqual(job[:2], ("systemd-run", "--unit=rollback-unit-recover"))
            self.assertEqual(job[-2:], ("--rollback", str(folder)))
            prepare.assert_not_called()
            self.assertIsNone(maintenance.interrupted_migration())

    def test_recover_only_rolls_back_an_interrupted_migration(self):
        # The updater starts --recover even after a failed agent update.
        with patch.object(maintenance, "command") as command, \
             patch.object(maintenance, "suitable", return_value=False), \
             patch.object(maintenance, "platform_supported", return_value=True), \
             patch.object(maintenance, "bundle_version", return_value=VERSION), \
             patch.object(maintenance, "prepare") as prepare:
            self.assertEqual(self.main("--recover"), 0)
            command.assert_not_called()
            folder = self.pending("boot-1")
            command.side_effect = lambda *args, **kwargs: maintenance.write_result(folder, "rolled-back", "restored")
            with patch.object(maintenance, "current_boot", return_value="boot-2"):
                self.assertEqual(self.main("--recover"), 0)
        self.assertEqual(command.call_args.args[-2:], ("--rollback", str(folder)))
        prepare.assert_not_called()

    def test_finished_migrations_are_not_recovered_after_a_reboot(self):
        for status in sorted(maintenance.TERMINAL):
            with self.subTest(status=status):
                self.pending("boot-1", status)
                with patch.object(maintenance, "current_boot", return_value="boot-2"):
                    self.assertIsNone(maintenance.interrupted_migration())

    def test_platforms_without_bundled_packages_need_no_action(self):
        with patch.object(maintenance, "suitable", return_value=False), \
             patch.object(maintenance, "bundle_version", return_value=VERSION), \
             patch.object(maintenance, "distribution", return_value="bookworm"), \
             patch.object(maintenance, "prepare") as prepare:
            self.assertEqual(self.main("--check"), 3)
            self.assertEqual(self.main("--apply", "--auto"), 0)
        prepare.assert_not_called()
        self.assertFalse(maintenance.auto_marker().exists())

    def test_only_a_rejected_system_uses_up_the_automatic_attempt(self):
        failures = (subprocess.CalledProcessError(100, "apt-get download"), ValueError("gehalten"))
        for error in failures:
            with self.subTest(error=type(error).__name__):
                maintenance.auto_marker().unlink(missing_ok=True)
                with patch.object(maintenance, "suitable", return_value=False), \
                     patch.object(maintenance, "platform_supported", return_value=True), \
                     patch.object(maintenance, "bundle_version", return_value=VERSION), \
                     patch.object(maintenance, "prepare", side_effect=error):
                    with self.assertRaises(type(error)):
                        self.main("--apply", "--auto")
                # A temporary download error before any change is retried next run.
                self.assertEqual(maintenance.auto_marker().exists(), isinstance(error, ValueError))

    def test_failed_rollback_restarts_the_agent(self):
        folder = self.root / "rollback"
        (folder / "old").mkdir(parents=True)
        snapshot = {"agentActive": True, "auto": [], "originals": {"pcscd": [OLD, "arm64"]},
                    "conffiles": [], "targetVersion": VERSION}
        def command(*args, **kwargs):
            if args[:2] == ("dpkg", "--configure"):
                raise subprocess.CalledProcessError(1, args, stderr="dpkg: error: broken")
            return ""
        with patch.object(maintenance, "installed", return_value=(VERSION, "arm64")), \
             patch.object(maintenance, "command", side_effect=command), \
             patch.object(maintenance.subprocess, "run", return_value=Mock(returncode=0)) as run:
            with self.assertRaises(subprocess.CalledProcessError):
                maintenance.rollback(folder, snapshot)
        self.assertIn(unittest.mock.call(["systemctl", "unmask", "--runtime", *maintenance.PCSC_UNITS], check=False),
                      run.call_args_list)
        self.assertEqual(run.call_args_list[-1],
                         unittest.mock.call(["systemctl", "start", maintenance.AGENT_SERVICE], check=False))

    def test_dpkg_lock_of_another_apt_run_is_waited_for(self):
        locked = subprocess.CalledProcessError(2, "dpkg", stderr="dpkg: error: dpkg frontend lock was locked by another process")
        with patch.object(maintenance, "command", side_effect=[locked, locked, ""]) as command, \
             patch.object(maintenance.time, "sleep"):
            maintenance.configure_interrupted_packages()
        self.assertEqual(command.call_count, 3)
        self.assertEqual(command.call_args.kwargs["env"]["LC_ALL"], "C")
        self.assertIn("DPkg::Lock::Timeout=600", maintenance.APT_INSTALL)

    def test_health_alone_cannot_pass_verification(self):
        (self.root / "probe_reader.py").write_text("print('probe')")
        with patch.object(maintenance, "suitable", return_value=True), \
             patch.object(maintenance, "command", side_effect=subprocess.CalledProcessError(1, "reader-probe")), \
             patch.object(maintenance, "http_json", return_value={"ok": True}) as health:
            with self.assertRaises(subprocess.CalledProcessError):
                maintenance.verify(self.root, {"readerFilter": "ACR122"})
        health.assert_not_called()


if __name__ == "__main__":
    unittest.main()
