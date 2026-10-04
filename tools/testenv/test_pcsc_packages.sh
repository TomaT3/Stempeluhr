#!/usr/bin/env bash
# Disposable Debian 13/arm64 container only; no Pi or systemd required.
# bash tools/testenv/test_pcsc_packages.sh /packages
set -euo pipefail
PACKAGES="$(realpath "${1:?package artifact directory required}")"
[ "$(dpkg --print-architecture)" = arm64 ] || { echo 'Expected arm64' >&2; exit 1; }
apt-get update -qq
apt-get install -y -qq --no-install-recommends pcscd libpcsclite1 libpcsclite-dev python3-pyscard binutils
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
apt-get install -y -qq --allow-downgrades --no-install-recommends "$WORK"/*.deb
[ "$(dpkg-query -W '-f=${Version}' pcscd)" = "$ORIGINAL" ]
[ "$(dpkg-query -W '-f=${Version}' libpcsclite1)" = "$ORIGINAL" ]
echo 'PC/SC packages: upgrade, Polkit/libudev, GLib fix symbols and rollback passed'
