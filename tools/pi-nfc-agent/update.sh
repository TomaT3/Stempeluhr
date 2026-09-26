#!/usr/bin/env bash
# Hält den NFC-Agenten auf der Version, die der Stempeluhr-Server ausliefert.
#
# Läuft als root über stempeluhr-nfc-agent-update.timer (alle 15 min und kurz
# nach dem Boot). Ablauf:
#   1. <api_base_url>/pi/agent.json lesen (Version, Dateiname, SHA-256)
#   2. bei abweichender Version: Bundle laden, SHA-256 prüfen, nach
#      releases/<version> entpacken, geänderte systemd-Units übernehmen
#   3. Link "current" atomar umsetzen, Agent neu starten
#   4. Health-Check über http://127.0.0.1:<port>/health - schlägt er fehl,
#      zurück auf die vorherige Version
#
# Die Agent-Version folgt der Server-Version, auch nach unten (Rollback des
# Containers). Ist der Server nicht erreichbar, endet das Skript ohne Fehler:
# offline ist ein normaler Betriebszustand.
#
# Verwendung: update.sh [--force]   (--force installiert auch bei gleicher
#                                    Version, z. B. aus install.sh)
# Für Tests lassen sich Pfade und systemctl per Umgebung umbiegen.
set -euo pipefail

CONFIG="${STEMPELUHR_AGENT_CONFIG:-/etc/stempeluhr-nfc-agent/config.json}"
BASE_DIR="${STEMPELUHR_AGENT_DIR:-/opt/stempeluhr-nfc-agent}"
SYSTEMD_DIR="${STEMPELUHR_SYSTEMD_DIR:-/etc/systemd/system}"
SYSTEMCTL="${SYSTEMCTL:-systemctl}"
HEALTH_TIMEOUT_SECONDS="${STEMPELUHR_HEALTH_TIMEOUT:-30}"
KEEP_RELEASES=3
UNITS=(stempeluhr-nfc-agent.service stempeluhr-nfc-agent-update.service stempeluhr-nfc-agent-update.timer)
DEV_VERSION="0.0.0-local"

FORCE=0
if [ "${1:-}" = "--force" ]; then
  FORCE=1
fi

log() { echo "stempeluhr-agent-update: $*"; }
fail() { log "FEHLER: $*" >&2; exit 1; }

# Liest einen Wert aus einer JSON-Datei (python3 ist für den Agenten ohnehin da).
json_value() { # datei schluessel default
  python3 - "$1" "$2" "$3" <<'PY'
import json, sys
path, key, default = sys.argv[1:4]
try:
    with open(path, encoding="utf-8") as handle:
        value = json.load(handle).get(key)
except (OSError, ValueError, AttributeError):
    value = None
print(default if value in (None, "") else value)
PY
}

[ -f "$CONFIG" ] || fail "$CONFIG fehlt"
BASE_URL="$(json_value "$CONFIG" api_base_url "")"
BASE_URL="${BASE_URL%/}"
PORT="$(json_value "$CONFIG" local_port 8737)"
[ -n "$BASE_URL" ] || fail "api_base_url fehlt in $CONFIG"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

if ! curl -fsS --max-time 20 -o "$WORK/agent.json" "$BASE_URL/pi/agent.json"; then
  log "Server nicht erreichbar oder liefert kein Agent-Bundle - nächster Versuch später."
  exit 0
fi

VERSION="$(json_value "$WORK/agent.json" version "")"
FILE="$(json_value "$WORK/agent.json" file "")"
SHA256="$(json_value "$WORK/agent.json" sha256 "")"

# Version und Dateiname landen in Pfaden - nur harmlose Zeichen zulassen.
[[ "$VERSION" =~ ^[0-9A-Za-z.+-]+$ ]] || fail "ungültige Version im Manifest: '$VERSION'"
[[ "$FILE" =~ ^agent-[0-9A-Za-z.+-]+\.tar\.gz$ ]] || fail "ungültiger Dateiname im Manifest: '$FILE'"
[[ "$SHA256" =~ ^[0-9a-f]{64}$ ]] || fail "ungültige SHA-256 im Manifest"

CURRENT_VERSION="$(cat "$BASE_DIR/current/VERSION" 2>/dev/null || echo none)"
if [ "$FORCE" -eq 0 ]; then
  if [ "$VERSION" = "$CURRENT_VERSION" ]; then
    exit 0
  fi
  if [ "$VERSION" = "$DEV_VERSION" ]; then
    log "Server ist ein lokaler Entwicklungs-Build ($DEV_VERSION) - kein Update."
    exit 0
  fi
fi

log "Aktualisiere Agent $CURRENT_VERSION -> $VERSION"
curl -fsS --max-time 120 -o "$WORK/$FILE" "$BASE_URL/pi/$FILE" || fail "Download von $FILE fehlgeschlagen"
echo "$SHA256  $WORK/$FILE" | sha256sum -c --quiet - || fail "SHA-256 von $FILE stimmt nicht"

RELEASE="$BASE_DIR/releases/$VERSION"
mkdir -p "$BASE_DIR/releases"
rm -rf "$RELEASE.tmp"
mkdir -p "$RELEASE.tmp"
tar -xzf "$WORK/$FILE" -C "$RELEASE.tmp" --no-same-owner
[ "$(cat "$RELEASE.tmp/VERSION" 2>/dev/null)" = "$VERSION" ] || fail "Bundle enthält nicht Version $VERSION"
chmod -R u=rwX,go=rX "$RELEASE.tmp"
rm -rf "$RELEASE"
mv "$RELEASE.tmp" "$RELEASE"

PREVIOUS="$(readlink "$BASE_DIR/current" 2>/dev/null || true)"

install_units() { # release-verzeichnis
  local changed=0 unit
  for unit in "${UNITS[@]}"; do
    if ! cmp -s "$1/$unit" "$SYSTEMD_DIR/$unit"; then
      install -m 644 "$1/$unit" "$SYSTEMD_DIR/$unit"
      changed=1
    fi
  done
  if [ "$changed" -eq 1 ]; then
    "$SYSTEMCTL" daemon-reload
  fi
}

switch_current() { # release-verzeichnis
  ln -sfn "$1" "$BASE_DIR/current.new"
  mv -Tf "$BASE_DIR/current.new" "$BASE_DIR/current"
}

agent_healthy() { # erwartete-version
  local deadline=$((SECONDS + HEALTH_TIMEOUT_SECONDS)) body
  while [ "$SECONDS" -lt "$deadline" ]; do
    if body="$(curl -fsS --max-time 2 "http://127.0.0.1:$PORT/health" 2>/dev/null)" \
      && [[ "$body" == *"\"version\": \"$1\""* ]]; then
      return 0
    fi
    sleep 1
  done
  return 1
}

install_units "$RELEASE"
switch_current "$RELEASE"
"$SYSTEMCTL" restart stempeluhr-nfc-agent.service

if ! agent_healthy "$VERSION"; then
  log "Agent $VERSION meldet sich nicht über /health - Rollback." >&2
  if [ -n "$PREVIOUS" ] && [ -d "$PREVIOUS" ]; then
    install_units "$PREVIOUS"
    switch_current "$PREVIOUS"
    "$SYSTEMCTL" restart stempeluhr-nfc-agent.service
    log "Zurück auf $(cat "$PREVIOUS/VERSION" 2>/dev/null || echo "$PREVIOUS")." >&2
  fi
  exit 1
fi

log "Agent $VERSION läuft."

# Alte Releases aufräumen, das aktive bleibt immer erhalten.
ACTIVE="$(readlink -f "$BASE_DIR/current")"
{ ls -1dt "$BASE_DIR"/releases/*/ 2>/dev/null || true; } | tail -n +$((KEEP_RELEASES + 1)) | while read -r old; do
  old="${old%/}"
  if [ "$(readlink -f "$old")" != "$ACTIVE" ]; then
    rm -rf "$old"
  fi
done
