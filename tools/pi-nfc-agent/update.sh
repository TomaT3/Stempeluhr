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
#   0. vorab bei jedem Lauf: journald-Konfiguration und Kiosk-Touch
#      (kiosk_setup.py) des installierten Bundles nachziehen
#   5. nach jedem erfolgreichen Lauf: fehlt der PC/SC-Fix, die Paketmigration
#      des installierten Bundles losgelöst starten (pcsc_maintenance.py);
#      eine durch Neustart unterbrochene Migration auch nach Fehlern zurückrollen
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
JOURNALD_DIR="${STEMPELUHR_JOURNALD_DIR:-/etc/systemd/journald.conf.d}"
SYSTEMCTL="${SYSTEMCTL:-systemctl}"
SYSTEMD_RUN="${SYSTEMD_RUN:-systemd-run}"
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

# Serialize agent changes with the explicitly started PC/SC package migration.
mkdir -p "$BASE_DIR"
if [ "${STEMPELUHR_AGENT_UPDATE_LOCKED:-}" != 1 ]; then
  # --close prevents restarted child processes from keeping the lock alive.
  exec env STEMPELUHR_AGENT_UPDATE_LOCKED=1 flock -x --close "$BASE_DIR/.maintenance.lock" bash "$0" "$@"
fi

# After every run - also offline or without a new version - the installed
# bundle decides whether the PC/SC packages still need the fix. The job runs
# detached and waits for the lock, i.e. until this updater has finished. A new
# package change starts only after a successful run, once per bundled package
# version. A migration interrupted by a reboot is rolled back regardless of
# this run's result, so repeated agent update errors cannot block it.
schedule_pcsc_migration() { # exit status of this updater
  local script="$BASE_DIR/current/pcsc_maintenance.py" status=0 action=(--apply --auto)
  [ -f "$script" ] || return 0
  python3 "$script" --check >/dev/null 2>&1 || status=$?
  case "$status" in
    0) return 0 ;;
    1) ;;
    # No packages for this platform (e.g. Bookworm): pcscd stays as it is.
    3) return 0 ;;
    *) log "WARNUNG: PC/SC-Fixstand fehlt; automatische Migration bereits versucht - manuell prüfen (siehe Pi-Anleitung)." >&2
       return 0 ;;
  esac
  if [ "${STEMPELUHR_PCSC_AUTO:-1}" != 1 ] || ! command -v "$SYSTEMD_RUN" >/dev/null 2>&1; then
    return 0
  fi
  # --recover only rolls back an interrupted migration and is a no-op otherwise.
  if [ "$1" -ne 0 ]; then action=(--recover); fi
  if "$SYSTEMD_RUN" --unit=stempeluhr-pcsc-migration --collect --quiet \
    python3 "$script" "${action[@]}" >/dev/null 2>&1; then
    if [ "$1" -eq 0 ]; then
      log "PC/SC-Fixstand fehlt - automatische Paketmigration gestartet (journalctl -u stempeluhr-pcsc-migration)."
    else
      log "Prüfung auf eine unterbrochene PC/SC-Paketmigration gestartet (journalctl -u stempeluhr-pcsc-migration)." >&2
    fi
  else
    log "PC/SC-Paketmigration läuft bereits oder ließ sich nicht starten." >&2
  fi
}

WORK=""
on_exit() {
  local status=$?
  if [ -n "$WORK" ]; then rm -rf "$WORK"; fi
  schedule_pcsc_migration "$status"
}
trap on_exit EXIT

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

install_journal() { # release-verzeichnis (auch alte Bundles ohne Datei)
  if [ -f "$1/journald-stempeluhr.conf" ] \
    && ! cmp -s "$1/journald-stempeluhr.conf" "$JOURNALD_DIR/stempeluhr.conf"; then
    if ! mkdir -p "$JOURNALD_DIR" \
      || ! install -m 644 "$1/journald-stempeluhr.conf" "$JOURNALD_DIR/stempeluhr.conf"; then
      log "WARNUNG: journald-Konfiguration nicht installiert; nächster Versuch beim nächsten Update-Lauf." >&2
      return 0
    fi
    if ! "$SYSTEMCTL" restart systemd-journald.service; then
      log "WARNUNG: journald-Neustart fehlgeschlagen; Konfiguration greift beim nächsten Neustart." >&2
    fi
  fi
}

# Touch scrolling and pinch lock of the kiosk session, also for terminals set up
# before (install.sh is not re-run there). Changes only the kiosk user's files
# and takes effect with the next session start.
configure_kiosk() { # release-verzeichnis (auch alte Bundles ohne Datei)
  if [ -f "$1/kiosk_setup.py" ] && ! python3 "$1/kiosk_setup.py" --discover; then
    log "WARNUNG: Kiosk-Touch-Einrichtung fehlgeschlagen; nächster Versuch beim nächsten Update-Lauf." >&2
  fi
}

# The first upgrade may still run an old updater. On its next timer run,
# configure the installed bundle before same-version/offline early exits.
install_journal "$BASE_DIR/current"
configure_kiosk "$BASE_DIR/current"

[ -f "$CONFIG" ] || fail "$CONFIG fehlt"
BASE_URL="$(json_value "$CONFIG" api_base_url "")"
BASE_URL="${BASE_URL%/}"
PORT="$(json_value "$CONFIG" local_port 8737)"
[ -n "$BASE_URL" ] || fail "api_base_url fehlt in $CONFIG"

WORK="$(mktemp -d)"

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
  install_journal "$1"
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
