#!/usr/bin/env python3
"""Touch setup of the Chromium kiosk under labwc (Raspberry Pi OS, Wayland).

Raspberry Pi OS ships /etc/xdg/labwc/rc.xml with mouseEmulation="yes" for its
DSI touchscreens: labwc turns every touch into mouse events, so a swipe over a
list selects text instead of scrolling it. labwc runs with --merge-config, so a
user rc.xml with just these <touch> entries and mouseEmulation="no" overrides
them. Without merging such a file would replace the whole configuration;
therefore it is only written while a merging labwc runs, and an existing user
rc.xml is never touched.

The kiosk autostart additionally gets --disable-pinch: real touch events allow
pinch zoom, and desktop Chromium ignores the viewport meta tag.

Both take effect with the next session start (reboot). Idempotent; prints one
line per change.

  --user USER   install.sh --kiosk-user; also reports why nothing was changed
  --discover    update.sh: every user with the Stempeluhr kiosk autostart
"""
from __future__ import annotations

import argparse
import os
from pathlib import Path
import pwd
import re
import sys
from xml.etree import ElementTree
from xml.sax.saxutils import quoteattr

AUTOSTART = Path(".config/autostart/stempeluhr-kiosk.desktop")
USER_RC = Path(".config/labwc/rc.xml")
SYSTEM_RC = Path(os.environ.get("STEMPELUHR_LABWC_SYSTEM_RC", "/etc/xdg/labwc/rc.xml"))
PROC = Path(os.environ.get("STEMPELUHR_PROC", "/proc"))
PINCH_FLAG = "--disable-pinch"
CHROMIUM_EXEC = re.compile(r"^(Exec=\S*chromium\S*)(.*)$", re.MULTILINE)


def emulated_touch_entries(system_rc: Path) -> list[dict[str, str]]:
    """Attributes of all <touch> entries that emulate a mouse."""
    try:
        root = ElementTree.parse(system_rc).getroot()
    except (OSError, ElementTree.ParseError):
        return []
    entries = []
    for element in root.iter():
        if element.tag.rsplit("}", 1)[-1] == "touch" \
                and element.get("mouseEmulation", "").lower() in ("yes", "true", "on"):
            entries.append(dict(element.attrib))
    return entries


def labwc_merges_config(proc: Path) -> bool | None:
    """True/False for a running labwc with/without --merge-config, None if none runs."""
    found = None
    for cmdline in proc.glob("[0-9]*/cmdline"):
        try:
            args = cmdline.read_bytes().split(b"\0")
        except OSError:
            continue
        if not args or os.path.basename(args[0]) != b"labwc":
            continue
        if b"-m" in args or b"--merge-config" in args:
            return True
        found = False
    return found


def user_rc_content(entries: list[dict[str, str]]) -> str:
    lines = ['<?xml version="1.0"?>',
             '<!-- Stempeluhr-Kiosk: Touch als Touch an Chromium (Wischen scrollt),',
             '     ergänzt /etc/xdg/labwc/rc.xml (labwc mit Merge-Option). -->',
             '<openbox_config xmlns="http://openbox.org/3.4/rc">']
    for attributes in entries:
        attributes = {**attributes, "mouseEmulation": "no"}
        rendered = " ".join(f"{name}={quoteattr(value)}" for name, value in attributes.items())
        lines.append(f"  <touch {rendered} />")
    lines.append("</openbox_config>")
    return "\n".join(lines) + "\n"


def write_as(path: Path, content: str, uid: int, gid: int, mode: int = 0o644) -> None:
    """Replaces path atomically, owned by the kiosk user."""
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(content, encoding="utf-8")
    os.chown(temporary, uid, gid)
    os.chmod(temporary, mode)
    os.replace(temporary, path)


def make_dirs_as(path: Path, home: Path, uid: int, gid: int) -> None:
    missing = []
    while path != home and not path.exists():
        missing.append(path)
        path = path.parent
    for folder in reversed(missing):
        folder.mkdir()
        os.chown(folder, uid, gid)


def ensure_touch(home: Path, uid: int, gid: int, system_rc: Path, proc: Path) -> str | None:
    """Writes the user rc.xml; returns a message for a change or a reason to skip."""
    user_rc = home / USER_RC
    if user_rc.exists():
        if 'mouseEmulation="no"' in user_rc.read_text(encoding="utf-8", errors="replace"):
            return None
        return (f'HINWEIS: {user_rc} existiert bereits und bleibt unverändert; '
                f'Touch ggf. von Hand umstellen (mouseEmulation="no").')
    entries = emulated_touch_entries(system_rc)
    if not entries:
        return None
    merges = labwc_merges_config(proc)
    if merges is None:
        return "HINWEIS: labwc läuft nicht; Touch-Einstellung folgt beim nächsten Update-Lauf."
    if not merges:
        return f"HINWEIS: labwc läuft ohne --merge-config; {user_rc} wird nicht angelegt (würde die Systemkonfiguration ersetzen)."
    make_dirs_as(user_rc.parent, home, uid, gid)
    write_as(user_rc, user_rc_content(entries), uid, gid)
    return f"Touch ohne Mausemulation in {user_rc} (wirkt nach Neustart)."


def ensure_pinch_disabled(home: Path) -> str | None:
    autostart = home / AUTOSTART
    try:
        content = autostart.read_text(encoding="utf-8")
    except OSError:
        return None
    exec_line = CHROMIUM_EXEC.search(content)
    if not exec_line or PINCH_FLAG in exec_line.group(2).split():
        return None
    stat = autostart.stat()
    command_end = exec_line.end(1)
    write_as(autostart, f"{content[:command_end]} {PINCH_FLAG}{content[command_end:]}",
             stat.st_uid, stat.st_gid, stat.st_mode & 0o777)
    return f"Zoom gesperrt ({PINCH_FLAG}) in {autostart} (wirkt nach Neustart)."


def configure(user: pwd.struct_passwd, report_skips: bool) -> None:
    home = Path(user.pw_dir)
    messages = [ensure_touch(home, user.pw_uid, user.pw_gid, SYSTEM_RC, PROC), ensure_pinch_disabled(home)]
    for message in messages:
        if message and (report_skips or not message.startswith("HINWEIS")):
            print(f"Kiosk {user.pw_name}: {message}")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--user")
    group.add_argument("--discover", action="store_true")
    args = parser.parse_args(argv)
    if args.user:
        configure(pwd.getpwnam(args.user), report_skips=True)
        return 0
    for user in pwd.getpwall():
        if user.pw_dir and (Path(user.pw_dir) / AUTOSTART).is_file():
            configure(user, report_skips=False)
    return 0


if __name__ == "__main__":
    sys.exit(main())
