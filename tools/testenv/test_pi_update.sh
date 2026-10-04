#!/usr/bin/env bash
# Test für build-bundle.sh + update.sh ohne Raspberry Pi und ohne systemd.
#
# Ein python3-http.server spielt den Stempeluhr-Server (/pi/...), ein
# Fake-systemctl startet den echten Agenten (mit gestubbtem pyscard) aus
# releases/<version>. Geprüft werden Erstinstallation, "nichts zu tun",
# Update, manipulierte Prüfsumme, Rollback bei defektem Agenten, Server
# offline und Dev-Server.
#
# Voraussetzungen: bash, python3, curl, sha256sum, tar (Linux).
# Usage: bash tools/testenv/test_pi_update.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
AGENT_SRC="$ROOT/tools/pi-nfc-agent"
WORK="$(mktemp -d /tmp/stempeluhr-pi-update.XXXXXX)"
SERVER_PORT=18090
AGENT_PORT=18738
PASS=0
FAIL=0

ok() { echo "  ✅ $*"; PASS=$((PASS + 1)); }
bad() { echo "  ❌ $*"; FAIL=$((FAIL + 1)); }
say() { echo; echo "== $* =="; }

cleanup() {
  [ -f "$WORK/agent.pid" ] && kill "$(cat "$WORK/agent.pid")" 2>/dev/null || true
  [ -n "${SERVER_PID:-}" ] && kill "$SERVER_PID" 2>/dev/null || true
  rm -rf "$WORK"
}
trap cleanup EXIT

mkdir -p "$WORK/www/pi" "$WORK/opt" "$WORK/systemd" "$WORK/stub/smartcard" "$WORK/bin"

# Fail only the optional journal installation, keeping unit installation real.
INSTALL_COMMAND="$(command -v install)"
cat > "$WORK/bin/install" <<EOF
#!/usr/bin/env bash
if [ -f "$WORK/fail-journal-install" ] && [[ "\$*" == *journald-stempeluhr.conf* ]]; then
  exit 1
fi
exec "$INSTALL_COMMAND" "\$@"
EOF
chmod +x "$WORK/bin/install"

# No PC/SC packages installed; systemd-run only records the detached migration.
printf '#!/bin/sh\nexit 1\n' > "$WORK/bin/dpkg-query"
cat > "$WORK/bin/systemd-run" <<EOF
#!/usr/bin/env bash
echo "\$*" >> "$WORK/systemd-run.log"
EOF
# Platform with bundled packages, independent of the test host.
printf '#!/bin/sh\n[ "$1" = --print-architecture ] && echo arm64\n' > "$WORK/bin/dpkg"
printf 'ID=debian\nVERSION_CODENAME=trixie\n' > "$WORK/os-release"
chmod +x "$WORK/bin/dpkg-query" "$WORK/bin/systemd-run" "$WORK/bin/dpkg"

# pyscard-Stub: der Agent importiert smartcard beim Start.
cat > "$WORK/stub/smartcard/__init__.py" <<'EOF'
EOF
cat > "$WORK/stub/smartcard/Exceptions.py" <<'EOF'
class CardConnectionException(Exception):
    pass


class NoCardException(Exception):
    pass
EOF
cat > "$WORK/stub/smartcard/System.py" <<'EOF'
def readers():
    return []
EOF
# Reader loop: a context without any reader, so the agent idles.
cat > "$WORK/stub/smartcard/scard.py" <<'EOF'
SCARD_S_SUCCESS = 0
SCARD_SCOPE_USER = 0
SCARD_E_NO_READERS_AVAILABLE = 0x8010002E


def SCardEstablishContext(scope):
    return SCARD_S_SUCCESS, 1


def SCardListReaders(context, groups):
    return SCARD_E_NO_READERS_AVAILABLE, []


def SCardReleaseContext(context):
    return SCARD_S_SUCCESS
EOF

cat > "$WORK/config.json" <<EOF
{"api_base_url": "http://127.0.0.1:$SERVER_PORT/", "terminal_id": "test", "local_port": $AGENT_PORT}
EOF

# Fake-systemctl: "restart" startet den Agenten aus current/ neu.
cat > "$WORK/systemctl" <<EOF
#!/usr/bin/env bash
case "\$1" in
  restart)
    if [ "\$2" = "systemd-journald.service" ]; then
      echo restart >> "$WORK/journald-restarts"
      [ -f "$WORK/fail-journald" ] && exit 1
      exit 0
    fi
    [ -f "$WORK/agent.pid" ] && kill "\$(cat "$WORK/agent.pid")" 2>/dev/null || true
    sleep 0.3
    PYTHONPATH="$WORK/stub" nohup python3 "$WORK/opt/current/stempeluhr_nfc_agent.py" \
      --config "$WORK/config.json" > "$WORK/agent.log" 2>&1 &
    echo \$! > "$WORK/agent.pid"
    ;;
  *) ;;
esac
EOF
chmod +x "$WORK/systemctl"

run_update() {
  STEMPELUHR_AGENT_CONFIG="$WORK/config.json" \
  STEMPELUHR_AGENT_DIR="$WORK/opt" \
  STEMPELUHR_SYSTEMD_DIR="$WORK/systemd" \
  STEMPELUHR_JOURNALD_DIR="${TEST_JOURNALD_DIR:-$WORK/journald}" \
  STEMPELUHR_HEALTH_TIMEOUT=8 \
  STEMPELUHR_PCSC_STATE="$WORK/pcsc-state" \
  STEMPELUHR_OS_RELEASE="$WORK/os-release" \
  PATH="$WORK/bin:$PATH" \
  SYSTEMCTL="$WORK/systemctl" \
    bash "$AGENT_SRC/update.sh" "$@" > "$WORK/update.log" 2>&1
}

publish() { # version [quelle]
  rm -rf "$WORK/www/pi" && mkdir -p "$WORK/www/pi"
  sh "${2:-$AGENT_SRC}/build-bundle.sh" "$1" "$WORK/www/pi"
}

current_version() { cat "$WORK/opt/current/VERSION" 2>/dev/null || echo none; }
health_version() {
  curl -fsS --max-time 2 "http://127.0.0.1:$AGENT_PORT/health" 2>/dev/null \
    | python3 -c 'import json,sys; print(json.load(sys.stdin)["version"])' 2>/dev/null || echo none
}

start_server() {
  (cd "$WORK/www" && exec python3 -m http.server "$SERVER_PORT" --bind 127.0.0.1 >/dev/null 2>&1) &
  SERVER_PID=$!
  for _ in $(seq 1 20); do
    curl -fsS -o /dev/null "http://127.0.0.1:$SERVER_PORT/" 2>/dev/null && return 0
    sleep 0.2
  done
  echo "Testserver startet nicht" >&2
  exit 1
}

start_server

say "1: Erstinstallation (--force)"
publish 1.0.0
if run_update --force; then ok "update.sh --force endet erfolgreich"; else bad "update.sh --force: $(tail -3 "$WORK/update.log")"; fi
[ "$(current_version)" = "1.0.0" ] && ok "current -> 1.0.0" || bad "current ist $(current_version)"
[ "$(health_version)" = "1.0.0" ] && ok "/health meldet 1.0.0" || bad "/health meldet $(health_version)"
[ -f "$WORK/systemd/stempeluhr-nfc-agent-update.timer" ] && ok "systemd-Units installiert" || bad "Units fehlen"
[ -f "$WORK/opt/current/terminal_diagnostics.py" ] && ok "Diagnosemodul im Bundle" || bad "Diagnosemodul fehlt"
[ -f "$WORK/opt/current/pcsc_maintenance.py" ] && [ -f "$WORK/opt/current/probe_reader.py" ] \
  && ok "PC/SC-Migration und Leserprüfung im Bundle" || bad "PC/SC-Hilfsdateien fehlen"
grep -q '^Storage=persistent' "$WORK/journald/stempeluhr.conf" \
  && ok "Journal überlebt Neustarts" || bad "Persistentes Journal fehlt"
grep -q "current/stempeluhr_nfc_agent.py" "$WORK/systemd/stempeluhr-nfc-agent.service" \
  && ok "Service startet aus current/" || bad "Service-Unit zeigt nicht auf current/"

# Existing config deliberately has no terminal_token. Capability discovery must
# fail explicitly while the original scan bridge stays usable after update.
AUTH_STATUS=$(curl -s -o /dev/null -w '%{http_code}' -H "Origin: http://127.0.0.1:$SERVER_PORT" \
  "http://127.0.0.1:$AGENT_PORT/terminal/catalog")
[ "$AUTH_STATUS" = "503" ] && ok "Alte config ohne Token: Katalog meldet 503 (Legacy-Fallback)" || bad "Katalog: $AUTH_STATUS"
SCAN_STATUS=$(curl -s -o /dev/null -w '%{http_code}' -H "Origin: http://127.0.0.1:$SERVER_PORT" \
  "http://127.0.0.1:$AGENT_PORT/scan/latest")
[ "$SCAN_STATUS" = "404" ] && ok "Alte config: Scan-Brücke läuft ohne Token" || bad "Scan-Brücke: $SCAN_STATUS"

say "2: Gleiche Version - nichts zu tun"
PID_BEFORE="$(cat "$WORK/agent.pid")"
# Emulate an upgrade performed by the old updater, which installed the new
# bundle/units but knew nothing about journald. The next timer run must repair
# this at the same server version, without restarting the agent.
rm "$WORK/journald/stempeluhr.conf"
if run_update; then ok "endet erfolgreich"; else bad "Fehler: $(tail -3 "$WORK/update.log")"; fi
[ "$(cat "$WORK/agent.pid")" = "$PID_BEFORE" ] && ok "Agent wurde nicht neu gestartet" || bad "Agent unnötig neu gestartet"
[ -f "$WORK/journald/stempeluhr.conf" ] && ok "Gleiche Version repariert Journal nach altem Updater" || bad "Journal fehlt weiterhin"

say "3: Server liefert neue Version"
publish 1.1.0
rm "$WORK/journald/stempeluhr.conf"
touch "$WORK/fail-journald"
if run_update; then ok "Update endet erfolgreich"; else bad "Update: $(tail -3 "$WORK/update.log")"; fi
[ "$(health_version)" = "1.1.0" ] && ok "/health meldet 1.1.0" || bad "/health meldet $(health_version)"
grep -q 'WARNUNG: journald' "$WORK/update.log" && ok "journald-Fehler wird toleriert und gemeldet" || bad "journald-Warnung fehlt"
rm "$WORK/fail-journald"

say "3b: Fehler der Journal-Installation blockieren kein Agent-Update"
for failure in mkdir install; do
  rm -f "$WORK/journald-restarts"
  if [ "$failure" = mkdir ]; then
    # A regular file cannot be used as a directory, even when running as root.
    TEST_JOURNALD_DIR="$WORK/journald-unavailable"
    touch "$TEST_JOURNALD_DIR"
  else
    rm "$WORK/journald/stempeluhr.conf"
    touch "$WORK/fail-journal-install"
  fi
  # --force exercises both the current-bundle repair and install_units path.
  if run_update --force; then ok "$failure-Fehler: Update erfolgreich"; else bad "$failure-Fehler: $(tail -3 "$WORK/update.log")"; fi
  [ "$(health_version)" = "1.1.0" ] && ok "$failure-Fehler: Agent läuft" || bad "$failure-Fehler: Agent nicht gesund"
  grep -q 'WARNUNG: journald-Konfiguration nicht installiert' "$WORK/update.log" \
    && ok "$failure-Fehler wird gemeldet" || bad "$failure-Warnung fehlt"
  [ ! -f "$WORK/journald-restarts" ] && ok "$failure-Fehler: kein journald-Neustart" || bad "$failure-Fehler: journald trotzdem neu gestartet"
  unset TEST_JOURNALD_DIR
  rm -f "$WORK/fail-journal-install"
done
if run_update; then ok "Journal-Installation wird nach Fehler erneut versucht"; else bad "Journal-Reparatur: $(tail -3 "$WORK/update.log")"; fi
[ -f "$WORK/journald/stempeluhr.conf" ] && ok "Journal nach Fehler repariert" || bad "Journal fehlt nach Reparatur"

say "4: Manipulierte Prüfsumme"
publish 1.2.0
sed -i 's/"sha256":"[0-9a-f]*"/"sha256":"'"$(printf '0%.0s' $(seq 1 64))"'"/' "$WORK/www/pi/agent.json"
if run_update; then bad "update.sh akzeptiert falsche SHA-256"; else ok "update.sh bricht ab"; fi
[ "$(current_version)" = "1.1.0" ] && ok "current bleibt 1.1.0" || bad "current ist $(current_version)"
[ ! -d "$WORK/opt/releases/1.2.0" ] && ok "kein Release-Verzeichnis für 1.2.0" || bad "1.2.0 wurde trotzdem entpackt"

say "5: Defekter Agent -> Rollback"
cp -r "$AGENT_SRC" "$WORK/broken-src"
sed -i '1a raise SystemExit("kaputt")' "$WORK/broken-src/stempeluhr_nfc_agent.py"
publish 1.3.0 "$WORK/broken-src"
if run_update; then bad "update.sh meldet Erfolg trotz defektem Agenten"; else ok "update.sh meldet Fehler"; fi
[ "$(current_version)" = "1.1.0" ] && ok "current zurück auf 1.1.0" || bad "current ist $(current_version)"
sleep 1
[ "$(health_version)" = "1.1.0" ] && ok "alter Agent läuft wieder" || bad "/health meldet $(health_version)"

say "6: Server offline"
kill "$SERVER_PID"; wait "$SERVER_PID" 2>/dev/null || true; SERVER_PID=""
if run_update; then ok "offline endet ohne Fehler"; else bad "offline: $(tail -3 "$WORK/update.log")"; fi
[ "$(current_version)" = "1.1.0" ] && ok "current unverändert" || bad "current ist $(current_version)"

say "7: Dev-Server (0.0.0-local) wird ignoriert"
start_server
publish 0.0.0-local
if run_update; then ok "endet erfolgreich"; else bad "Fehler: $(tail -3 "$WORK/update.log")"; fi
[ "$(current_version)" = "1.1.0" ] && ok "kein Downgrade auf den Dev-Build" || bad "current ist $(current_version)"

say "8: Server zurückgerollt -> Agent folgt"
publish 1.0.0
if run_update; then ok "endet erfolgreich"; else bad "Fehler: $(tail -3 "$WORK/update.log")"; fi
[ "$(health_version)" = "1.0.0" ] && ok "Agent folgt auf 1.0.0" || bad "/health meldet $(health_version)"

say "9: Fehlender PC/SC-Fix startet die Paketmigration automatisch"
[ ! -f "$WORK/systemd-run.log" ] && ok "Bundles ohne Pakete starten keine Migration" || bad "Migration ohne Pakete gestartet"
cp -r "$AGENT_SRC" "$WORK/pcsc-src"
mkdir -p "$WORK/pcsc-src/pcsc"
printf '{"version": "2.5.2-1~stempeluhr13.1", "packages": []}\n' > "$WORK/pcsc-src/pcsc/manifest.json"
publish 1.4.0 "$WORK/pcsc-src"
if run_update; then ok "Update endet erfolgreich"; else bad "Update: $(tail -3 "$WORK/update.log")"; fi
grep -q -- '--unit=stempeluhr-pcsc-migration .*current/pcsc_maintenance.py --apply --auto' "$WORK/systemd-run.log" 2>/dev/null \
  && ok "Migration nach dem Update losgelöst gestartet" || bad "Migration nicht gestartet: $(cat "$WORK/systemd-run.log" 2>/dev/null)"
rm -f "$WORK/systemd-run.log"
STEMPELUHR_PCSC_AUTO=0 run_update --force || bad "Update ohne Auto-Migration: $(tail -3 "$WORK/update.log")"
[ ! -f "$WORK/systemd-run.log" ] && ok "Installer-Modus startet keine zweite Migration" || bad "Migration trotz STEMPELUHR_PCSC_AUTO=0"
mkdir -p "$WORK/pcsc-state"
printf '{"targetVersion": "2.5.2-1~stempeluhr13.1"}\n' > "$WORK/pcsc-state/auto-attempt.json"
if run_update; then ok "Update mit altem Fehlversuchsmarker erfolgreich"; else bad "Update: $(tail -3 "$WORK/update.log")"; fi
grep -q -- '--apply --auto' "$WORK/systemd-run.log" 2>/dev/null \
  && ok "Altem Marker folgt ein Versuch mit korrigierter Migrationsrevision" || bad "Korrigierte Migration nicht gestartet"
rm -f "$WORK/systemd-run.log"
python3 - "$WORK/opt/current" "$WORK/pcsc-state/auto-attempt.json" <<'PY'
import json, sys
sys.path.insert(0, sys.argv[1])
from pcsc_maintenance import MIGRATION_REVISION
with open(sys.argv[2], 'w') as marker:
    json.dump({'targetVersion': '2.5.2-1~stempeluhr13.1', 'migrationRevision': MIGRATION_REVISION}, marker)
PY
if run_update; then ok "Update nach Migrationsversuch erfolgreich"; else bad "Update: $(tail -3 "$WORK/update.log")"; fi
[ ! -f "$WORK/systemd-run.log" ] && grep -q 'bereits versucht' "$WORK/update.log" \
  && ok "Kein zweiter automatischer Versuch, Hinweis im Journal" || bad "Wiederholter Versuch oder Hinweis fehlt"

say "10: Bookworm ohne Pakete bleibt still"
rm -f "$WORK/pcsc-state/auto-attempt.json"
printf 'ID=debian\nVERSION_CODENAME=bookworm\n' > "$WORK/os-release"
if run_update; then ok "Update endet erfolgreich"; else bad "Update: $(tail -3 "$WORK/update.log")"; fi
[ ! -f "$WORK/systemd-run.log" ] && ! grep -q 'PC/SC' "$WORK/update.log" \
  && ok "Keine Migration, keine Warnung" || bad "Migration oder Warnung auf Bookworm: $(grep 'PC/SC' "$WORK/update.log")"

say "11: Unterbrochene Migration wird auch nach fehlgeschlagenem Update zurückgerollt"
printf 'ID=debian\nVERSION_CODENAME=trixie\n' > "$WORK/os-release"
mkdir -p "$WORK/pcsc-state/interrupted"
printf '{"folder": "%s", "bootId": "previous-boot"}\n' "$WORK/pcsc-state/interrupted" > "$WORK/pcsc-state/pending.json"
cp -r "$WORK/broken-src" "$WORK/broken-pcsc-src"
cp -r "$WORK/pcsc-src/pcsc" "$WORK/broken-pcsc-src/"
publish 1.5.0 "$WORK/broken-pcsc-src"
if run_update; then bad "update.sh meldet Erfolg trotz defektem Agenten"; else ok "update.sh meldet Fehler"; fi
[ "$(current_version)" = "1.4.0" ] && ok "current zurück auf 1.4.0" || bad "current ist $(current_version)"
grep -q -- '--unit=stempeluhr-pcsc-migration .*current/pcsc_maintenance.py --recover$' "$WORK/systemd-run.log" 2>/dev/null \
  && ok "Wiederherstellung trotz Updatefehler gestartet" || bad "Keine Wiederherstellung: $(cat "$WORK/systemd-run.log" 2>/dev/null)"
! grep -q -- '--apply' "$WORK/systemd-run.log" 2>/dev/null \
  && ok "Kein neuer Paketwechsel nach Updatefehler" || bad "Paketwechsel nach Updatefehler gestartet"

say "Ergebnis: $PASS bestanden, $FAIL fehlgeschlagen"
[ "$FAIL" -eq 0 ]
