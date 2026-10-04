#!/usr/bin/env bash
# Disposable Debian 13/arm64 container only; no Pi or systemd required.
# bash tools/testenv/test_pcsc_packages.sh /packages
set -euo pipefail
PACKAGES="$(realpath "${1:?package artifact directory required}")"
[ "$(dpkg --print-architecture)" = arm64 ] || { echo 'Expected arm64' >&2; exit 1; }
apt-get update -qq
apt-get install -y -qq --no-install-recommends pcscd libpcsclite1 libpcsclite-dev python3-pyscard binutils udev systemd
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
cd "$WORK"
ORIGINAL="$(dpkg-query -W '-f=${Version}' pcscd)"
apt-get download "pcscd=$ORIGINAL" "libpcsclite1=$ORIGINAL" "libpcsclite-dev=$ORIGINAL"
python3 - "$PACKAGES" <<'PY'
import sys
sys.path.insert(0, '/work/tools/pi-nfc-agent')
from pathlib import Path
from pcsc_maintenance import packages_from_manifest, simulate
version, packages = packages_from_manifest(Path(sys.argv[1]), 'arm64')
simulate(packages.values())
print('Manifest, checksums, metadata and dependency simulation passed:', version)
PY
apt-get install -y -qq --no-install-recommends "$PACKAGES"/*.deb
VERSION="$(pcscd --version)"
[[ "$VERSION" == *2.5.2* && "${VERSION,,}" == *polkit* && "${VERSION,,}" == *libudev* ]]
SYMBOLS="$(nm -D /usr/sbin/pcscd)"
[[ "$SYMBOLS" == *g_main_context_pending* && "$SYMBOLS" == *g_main_context_iteration* ]]
python3 -c 'import ctypes; ctypes.CDLL("libpcsclite.so.1"); from smartcard.System import readers; print("pyscard and upgraded PC/SC library load successfully (no daemon/hardware in container)")'
python3 - <<'PY'
import grp
import pwd
import sys
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0, '/work/tools/pi-nfc-agent')
import pcsc_maintenance as maintenance

# The pinned package remains unchanged; its systemd service runs unprivileged.
assert pwd.getpwnam('pcscd').pw_uid != 0
assert pwd.getpwnam('pcscd').pw_gid == grp.getgrnam('pcscd').gr_gid
service = Path('/usr/lib/systemd/system/pcscd.service').read_text()
socket = Path('/usr/lib/systemd/system/pcscd.socket').read_text()
assert 'User=pcscd' in service and 'RuntimeDirectory=pcscd' in service
assert 'RuntimeDirectoryPreserve=yes' in service
assert 'PIDFile=/run/pcscd/pcscd.pid' in service
assert 'SocketUser=pcscd' in socket and 'SocketGroup=pcscd' in socket
assert 'SocketMode=0666' in socket  # stempeluhr still connects via Polkit.

# Install the exact bundled rule, validate its syntax with the real Trixie udev.
# No daemon/hardware in this container: activation ordering is tested separately.
with patch.object(maintenance, 'refresh_usb_rule'):
    maintenance.install_usb_rule()
assert maintenance.USB_RULE.stat().st_mode & 0o777 == 0o644
maintenance.command('udevadm', 'verify', str(maintenance.USB_RULE))

# Exercise cleanup with an actual Unix socket and stale PID/user-owned directory.
import socket as sockets
runtime = maintenance.PCSC_RUNTIME
runtime.mkdir(parents=True, exist_ok=True)
(runtime / 'pcscd.pid').write_text('123')
with sockets.socket(sockets.AF_UNIX) as listener:
    listener.bind(str(runtime / 'pcscd.comm'))
import os
os.chown(runtime, pwd.getpwnam('pcscd').pw_uid, grp.getgrnam('pcscd').gr_gid)
maintenance.reset_pcsc_runtime()
assert not runtime.exists()
print('Unprivileged service/socket user, scoped udev rule and stale runtime cleanup passed')
PY
apt-get install -y -qq --allow-downgrades --no-install-recommends "$WORK"/*.deb
[ "$(dpkg-query -W '-f=${Version}' pcscd)" = "$ORIGINAL" ]
[ "$(dpkg-query -W '-f=${Version}' libpcsclite1)" = "$ORIGINAL" ]
python3 - <<'PY'
import sys
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0, '/work/tools/pi-nfc-agent')
import pcsc_maintenance as maintenance
with patch.object(maintenance, 'refresh_usb_rule'):
    maintenance.restore_usb_rule(Path('.'), {'usbRule': None})
assert not maintenance.USB_RULE.exists()
real_command = maintenance.command
def without_systemctl(*args, **kwargs):
    if args[0] != 'systemctl':
        return real_command(*args, **kwargs)
with patch.object(maintenance, 'command', side_effect=without_systemctl):
    maintenance.resume_pcsc()
runtime = maintenance.PCSC_RUNTIME.stat()
assert runtime.st_uid == 0 and runtime.st_gid == 0
service = Path('/usr/lib/systemd/system/pcscd.service').read_text()
assert 'User=pcscd' not in service
print('Rollback removes migration rule and recreates the root-owned legacy runtime directory')
PY
echo 'PC/SC packages: upgrade, Polkit/libudev, GLib fix symbols and rollback passed'
