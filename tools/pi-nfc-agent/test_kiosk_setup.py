"""Kiosk touch setup tests on temporary homes, without labwc or root."""
import contextlib
import io
import os
from pathlib import Path
import pwd
import tempfile
import unittest
from unittest.mock import patch
from xml.etree import ElementTree

import kiosk_setup

# Shortened from Raspberry Pi OS (Trixie) /etc/xdg/labwc/rc.xml.
SYSTEM_RC = """<?xml version="1.0"?>
<openbox_config xmlns="http://openbox.org/3.4/rc">
  <keyboard><default /></keyboard>
  <touch deviceName="11-0038 generic ft5x06 (79)" mapToOutput="DSI-2" mouseEmulation="yes" />
  <touch deviceName="raspberrypi-ts" mapToOutput="DSI-1" mouseEmulation="yes" />
  <touch deviceName="already-touch" mouseEmulation="no" />
</openbox_config>
"""
AUTOSTART = """[Desktop Entry]
Type=Application
Name=Stempeluhr Kiosk
Exec=chromium --kiosk --app=https://kiosk.test/terminal?terminalId=test
X-GNOME-Autostart-enabled=true
"""


class KioskSetupTests(unittest.TestCase):
    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.root = Path(folder.name)
        self.home = self.root / "home"
        self.home.mkdir()
        self.system_rc = self.root / "system-rc.xml"
        self.system_rc.write_text(SYSTEM_RC, encoding="utf-8")
        self.proc = self.root / "proc"
        self.proc.mkdir()
        self.user_rc = self.home / ".config/labwc/rc.xml"
        self.autostart = self.home / ".config/autostart/stempeluhr-kiosk.desktop"

    def run_process(self, pid, *args):
        folder = self.proc / str(pid)
        folder.mkdir()
        (folder / "cmdline").write_bytes(b"\0".join(arg.encode() for arg in args) + b"\0")

    def ensure_touch(self):
        return kiosk_setup.ensure_touch(self.home, kiosk_setup.emulated_touch_entries(self.system_rc),
                                        kiosk_setup.labwc_merges_config(self.proc))

    def configure_quietly(self, user):
        """configure() with patched system paths; the forked child's traceback is muted."""
        stderr = os.dup(2)
        with open(os.devnull, "w") as devnull, \
                patch.object(kiosk_setup, "SYSTEM_RC", self.system_rc), \
                patch.object(kiosk_setup, "PROC", self.proc), \
                contextlib.redirect_stdout(io.StringIO()):
            os.dup2(devnull.fileno(), 2)
            try:
                return kiosk_setup.configure(user, report_skips=True)
            finally:
                os.dup2(stderr, 2)
                os.close(stderr)

    def test_detects_labwc_merge_flag(self):
        self.assertIsNone(kiosk_setup.labwc_merges_config(self.proc))
        self.run_process(10, "/usr/bin/lwrespawn", "-m")
        self.run_process(11, "labwc")
        self.assertFalse(kiosk_setup.labwc_merges_config(self.proc))
        self.run_process(12, "/usr/bin/labwc", "-m")
        self.assertTrue(kiosk_setup.labwc_merges_config(self.proc))

    def test_writes_user_rc_without_mouse_emulation_for_merging_labwc(self):
        self.run_process(20, "/usr/bin/labwc", "--merge-config")

        message = self.ensure_touch()

        self.assertIn("Touch ohne Mausemulation", message)
        root = ElementTree.parse(self.user_rc).getroot()
        touch = [element.attrib for element in root.iter() if element.tag.endswith("touch")]
        self.assertEqual(touch, [
            {"deviceName": "11-0038 generic ft5x06 (79)", "mapToOutput": "DSI-2", "mouseEmulation": "no"},
            {"deviceName": "raspberrypi-ts", "mapToOutput": "DSI-1", "mouseEmulation": "no"},
        ])
        self.assertEqual(root.tag, "{http://openbox.org/3.4/rc}openbox_config")
        self.assertIsNone(self.ensure_touch())

    def test_keeps_system_config_without_merge_or_without_labwc(self):
        self.assertIn("labwc läuft nicht", self.ensure_touch())
        self.run_process(30, "/usr/bin/labwc")
        self.assertIn("ohne --merge-config", self.ensure_touch())
        self.assertFalse(self.user_rc.exists())

    def test_never_overwrites_existing_user_rc(self):
        self.run_process(40, "labwc", "-m")
        self.user_rc.parent.mkdir(parents=True)
        own = '<openbox_config xmlns="http://openbox.org/3.4/rc"><theme /></openbox_config>\n'
        self.user_rc.write_text(own, encoding="utf-8")

        self.assertIn("bleibt unverändert", self.ensure_touch())
        self.assertEqual(self.user_rc.read_text(encoding="utf-8"), own)

    def test_no_emulated_touchscreen_needs_no_user_rc(self):
        self.run_process(50, "labwc", "-m")
        self.system_rc.write_text('<openbox_config><touch deviceName="x" /></openbox_config>', encoding="utf-8")
        self.assertIsNone(self.ensure_touch())
        self.system_rc.unlink()
        self.assertIsNone(self.ensure_touch())
        self.assertFalse(self.user_rc.exists())

    def test_disables_pinch_once_in_kiosk_autostart(self):
        self.assertIsNone(kiosk_setup.ensure_pinch_disabled(self.home))
        self.autostart.parent.mkdir(parents=True)
        self.autostart.write_text(AUTOSTART, encoding="utf-8")
        self.autostart.chmod(0o600)

        self.assertIn("--disable-pinch", kiosk_setup.ensure_pinch_disabled(self.home))
        self.assertIsNone(kiosk_setup.ensure_pinch_disabled(self.home))

        content = self.autostart.read_text(encoding="utf-8")
        self.assertEqual(content, AUTOSTART.replace("Exec=chromium ", "Exec=chromium --disable-pinch "))
        self.assertEqual(self.autostart.stat().st_mode & 0o777, 0o600)

    def test_planted_temporary_symlinks_are_not_followed(self):
        # The former fixed temporary names; a kiosk user could pre-create them.
        victim = self.root / "victim"
        victim.write_text("geschützt\n", encoding="utf-8")
        self.autostart.parent.mkdir(parents=True)
        self.autostart.write_text(AUTOSTART, encoding="utf-8")
        self.user_rc.parent.mkdir(parents=True)
        planted = [self.autostart.with_name(self.autostart.name + ".tmp"),
                   self.user_rc.with_name(self.user_rc.name + ".tmp")]
        for link in planted:
            link.symlink_to(victim)
        self.run_process(60, "labwc", "-m")

        self.assertIsNotNone(kiosk_setup.ensure_pinch_disabled(self.home))
        self.assertIn("Touch ohne Mausemulation", self.ensure_touch())

        self.assertEqual(victim.read_text(encoding="utf-8"), "geschützt\n")
        self.assertTrue(all(link.is_symlink() for link in planted))
        self.assertIn("--disable-pinch", self.autostart.read_text(encoding="utf-8"))
        self.assertFalse(self.autostart.is_symlink())
        leftovers = {path.name for path in self.home.rglob(".*") if path.is_file()}
        self.assertEqual(leftovers, set())

    @unittest.skipUnless(hasattr(os, "geteuid") and os.geteuid() == 0, "needs root (privilege drop)")
    def test_root_accesses_home_only_with_the_users_rights(self):
        nobody = pwd.getpwnam("nobody")
        self.root.chmod(0o755)
        root_only = self.root / "root-only"
        root_only.mkdir(mode=0o755)
        self.autostart.parent.mkdir(parents=True)
        self.autostart.write_text(AUTOSTART, encoding="utf-8")
        for path in (self.home, self.home / ".config", self.autostart.parent, self.autostart):
            os.chown(path, nobody.pw_uid, nobody.pw_gid)
        user = pwd.struct_passwd(("kiosk", "x", nobody.pw_uid, nobody.pw_gid, "", str(self.home), "/bin/sh"))

        # labwc not running: only the autostart changes, written by the user.
        self.assertTrue(self.configure_quietly(user))
        self.assertIn("--disable-pinch", self.autostart.read_text(encoding="utf-8"))
        self.assertEqual(self.autostart.stat().st_uid, nobody.pw_uid)

        # The user redirects the labwc folder to a root-owned one: root must not write there.
        (self.home / ".config/labwc").symlink_to(root_only)
        self.run_process(70, "labwc", "-m")
        self.assertFalse(self.configure_quietly(user))
        self.assertEqual(list(root_only.iterdir()), [])

    def test_discover_configures_only_kiosk_users_and_stays_quiet_on_skips(self):
        self.autostart.parent.mkdir(parents=True)
        self.autostart.write_text(AUTOSTART, encoding="utf-8")
        other = self.root / "other"
        other.mkdir()
        users = [pwd.struct_passwd(("kiosk", "x", os.getuid(), os.getgid(), "", str(self.home), "/bin/sh")),
                 pwd.struct_passwd(("admin", "x", os.getuid(), os.getgid(), "", str(other), "/bin/sh"))]
        output = io.StringIO()
        with patch.object(kiosk_setup, "SYSTEM_RC", self.system_rc), \
                patch.object(kiosk_setup, "PROC", self.proc), \
                patch("pwd.getpwall", return_value=users), \
                patch("os.geteuid", return_value=1000), \
                contextlib.redirect_stdout(output):
            # Unprivileged run: no fork, so the output stays in this process.
            self.assertEqual(kiosk_setup.main(["--discover"]), 0)

        # labwc is not running: no touch hint every 15 minutes, only the change.
        self.assertEqual(output.getvalue().splitlines(),
                         [f"Kiosk kiosk: Zoom gesperrt (--disable-pinch) in {self.autostart} (wirkt nach Neustart)."])
        self.assertFalse((other / ".config").exists())


if __name__ == "__main__":
    unittest.main()
