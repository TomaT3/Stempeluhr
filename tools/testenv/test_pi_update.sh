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

mkdir -p "$WORK/www/pi" "$WORK/opt" "$WORK/systemd" "$WORK/stub/smartcard"

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

cat > "$WORK/config.json" <<EOF
{"api_base_url": "http://127.0.0.1:$SERVER_PORT/", "terminal_id": "test", "local_port": $AGENT_PORT}
EOF

# Fake-systemctl: "restart" startet den Agenten aus current/ neu.
cat > "$WORK/systemctl" <<EOF
#!/usr/bin/env bash
case "\$1" in
  restart)
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
  STEMPELUHR_HEALTH_TIMEOUT=8 \
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
grep -q "current/stempeluhr_nfc_agent.py" "$WORK/systemd/stempeluhr-nfc-agent.service" \
  && ok "Service startet aus current/" || bad "Service-Unit zeigt nicht auf current/"

say "2: Gleiche Version - nichts zu tun"
PID_BEFORE="$(cat "$WORK/agent.pid")"
if run_update; then ok "endet erfolgreich"; else bad "Fehler: $(tail -3 "$WORK/update.log")"; fi
[ "$(cat "$WORK/agent.pid")" = "$PID_BEFORE" ] && ok "Agent wurde nicht neu gestartet" || bad "Agent unnötig neu gestartet"

say "3: Server liefert neue Version"
publish 1.1.0
if run_update; then ok "Update endet erfolgreich"; else bad "Update: $(tail -3 "$WORK/update.log")"; fi
[ "$(health_version)" = "1.1.0" ] && ok "/health meldet 1.1.0" || bad "/health meldet $(health_version)"

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

say "Ergebnis: $PASS bestanden, $FAIL fehlgeschlagen"
[ "$FAIL" -eq 0 ]
