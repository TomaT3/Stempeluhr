#!/usr/bin/env bash
# Installer wiring without APT, systemd, user creation, or external servers.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
export TEST_INSTALL_WORK="$WORK"
mkdir -p "$WORK/bin"
for name in systemctl chown useradd; do
  printf '#!/bin/sh\nexit 0\n' > "$WORK/bin/$name"
done
cat > "$WORK/bin/id" <<'EOF'
#!/bin/sh
[ "${1:-}" = -u ] && echo 0
exit 0
EOF
cat > "$WORK/bin/apt-get" <<'EOF'
#!/bin/sh
echo "$*" >> "$TEST_INSTALL_WORK/apt.log"
EOF
cat > "$WORK/bin/curl" <<'EOF'
#!/bin/bash
while [ $# -gt 0 ]; do
  if [ "$1" = -o ]; then out="$2"; shift 2; else shift; fi
done
cat > "$out" <<'UPDATER'
mkdir -p "$STEMPELUHR_AGENT_DIR/current"
echo 1.0.0 > "$STEMPELUHR_AGENT_DIR/current/VERSION"
cp "$TEST_INSTALL_WORK/maintenance.py" "$STEMPELUHR_AGENT_DIR/current/pcsc_maintenance.py"
UPDATER
EOF
cat > "$WORK/maintenance.py" <<'EOF'
import os, pathlib, sys
folder = pathlib.Path(os.environ['TEST_INSTALL_WORK'])
(folder / 'migration.log').write_text(' '.join(sys.argv[1:]))
sys.exit(1 if (folder / 'fail-migration').exists() else 0)
EOF
chmod +x "$WORK/bin/"*
export PATH="$WORK/bin:$PATH"
export STEMPELUHR_CONFIG_DIR="$WORK/config"
export STEMPELUHR_AGENT_DIR="$WORK/agent"
export STEMPELUHR_POLKIT_RULE="$WORK/polkit/rule"
export STEMPELUHR_CHROMIUM_POLICY_DIR="$WORK/chromium"

bash "$ROOT/tools/pi-nfc-agent/install.sh" --server https://kiosk.test --terminal-id test --skip-apt > "$WORK/install.log"
[ ! -f "$WORK/apt.log" ] && [ ! -f "$WORK/migration.log" ]
[ -f "$WORK/chromium/stempeluhr.json" ]
CONFIG_HASH="$(sha256sum "$WORK/config/config.json")"

bash "$ROOT/tools/pi-nfc-agent/install.sh" > "$WORK/install.log"
grep -q 'pcscd pcsc-tools python3-pyscard curl' "$WORK/apt.log"
[ "$(cat "$WORK/migration.log")" = --apply ]
[ "$(sha256sum "$WORK/config/config.json")" = "$CONFIG_HASH" ]

touch "$WORK/fail-migration"
rm "$WORK/chromium/stempeluhr.json"
if bash "$ROOT/tools/pi-nfc-agent/install.sh" > "$WORK/install.log" 2>&1; then
  echo 'Installer must report a failed migration' >&2
  exit 1
fi
grep -q 'PC/SC-Paketmigration fehlgeschlagen' "$WORK/install.log"
[ ! -f "$WORK/chromium/stempeluhr.json" ]
echo 'Pi installer: new install, skip-apt, repeated install/config preservation and failed migration passed'
