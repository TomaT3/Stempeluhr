"""Migration state machine and validation; real system calls are stubbed.

Run on Linux (fcntl/dpkg). Package installation itself is tested separately
in an arm64 Trixie container by test_pcsc_packages.sh.
"""
import hashlib
import json
from pathlib import Path
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
                            ("CONFIG", self.root / "config.json")):
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

    def test_unsupported_distribution_changes_no_packages(self):
        with patch.object(maintenance, "suitable", return_value=False), \
             patch.object(maintenance, "distribution", return_value="bookworm"), \
             patch.object(maintenance, "command") as command:
            with self.assertRaisesRegex(ValueError, "nur Trixie"):
                maintenance.prepare()
        command.assert_not_called()

    def test_prepare_saves_originals_and_arms_rollback_before_detached_activation(self):
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
                p, version = args[2].split("=")
                (kwargs["cwd"] / f"{p}_{version}_arm64.deb").write_bytes(b"original")
            return ""
        with patch.object(maintenance, "suitable", return_value=False), \
             patch.object(maintenance, "distribution", return_value="trixie"), \
             patch.object(maintenance, "installed", side_effect=lambda p: None if p == "libpcsclite-dev" else (OLD, "arm64")), \
             patch.object(maintenance, "packages_from_manifest", return_value=(VERSION, packages)), \
             patch.object(maintenance, "package_info", side_effect=self.info), \
             patch.object(maintenance, "active", return_value=True), \
             patch.object(maintenance, "command", side_effect=command):
            folder = maintenance.prepare()
        jobs = [args for args in calls if args[0] == "systemd-run"]
        self.assertEqual(len(jobs), 2)
        self.assertIn("--on-active=5m", jobs[0])
        self.assertIn("--rollback", jobs[0])
        self.assertIn("--activate", jobs[1])
        self.assertEqual(len(list((folder / "old").glob("*.deb"))), 2)
        self.assertEqual(len(list((folder / "new").glob("*.deb"))), 2)
        self.assertEqual(folder.stat().st_mode & 0o077, 0)
        self.assertEqual(json.loads((folder / "config.json").read_text())["terminal_token"], "SECRET")
        self.assertEqual(json.loads((folder / "snapshot.json").read_text())["auto"], ["libpcsclite1"])

    def test_failed_reader_probe_after_activation_rolls_back(self):
        folder = self.root / "activation"
        folder.mkdir()
        (folder / "new").mkdir()
        (folder / "snapshot.json").write_text(json.dumps({"agentActive": True, "auto": [], "rollbackUnit": "test"}))
        with patch.object(maintenance, "command"), \
             patch.object(maintenance, "verify", side_effect=ValueError("reader missing")), \
             patch.object(maintenance, "rollback") as rollback:
            with self.assertRaisesRegex(ValueError, "reader missing"):
                maintenance.activate(folder)
        rollback.assert_called_once()
        self.assertIsNone(maintenance.read_result(folder))

    def test_success_is_committed_before_cancelling_the_timer(self):
        folder = self.root / "activation"
        folder.mkdir()
        (folder / "new").mkdir()
        (folder / "snapshot.json").write_text(json.dumps({"agentActive": True, "auto": [], "rollbackUnit": "test"}))
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
        verify.assert_called_once_with(folder, snapshot, require_fix=False)
        self.assertTrue(any("--allow-downgrades" in call.args for call in command.call_args_list))

    def test_wrong_restored_version_cannot_count_as_a_successful_rollback(self):
        folder = self.root / "rollback"
        folder.mkdir()
        (folder / "old").mkdir()
        snapshot = {"auto": [], "originals": {"pcscd": [OLD, "arm64"]}, "conffiles": []}
        with patch.object(maintenance, "command"), \
             patch.object(maintenance, "installed", return_value=(VERSION, "arm64")):
            with self.assertRaisesRegex(ValueError, "Originalversion"):
                maintenance.rollback(folder, snapshot)
        self.assertIsNone(maintenance.read_result(folder))

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
