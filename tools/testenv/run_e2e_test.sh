#!/usr/bin/env bash
# End-to-End-Integrationstest für die Offline-Stempel-Funktion.
#
# Startet Fake-Kimai + Stempeluhr-API lokal, spielt einen kompletten
# Arbeitstag durch (offline stempeln, Kimai "wieder anschalten", Nachtrag
# prüfen) und verifiziert die resultierenden Buchungen.
#
# Voraussetzungen: dotnet 10 (PATH), python3, curl. Kein Docker nötig.
#
# Usage:  bash tools/testenv/run_e2e_test.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
WORK="$(mktemp -d /tmp/stempeluhr-e2e.XXXXXX)"
# Eindeutige Event-ID-Präfixe pro Lauf, damit der persistente Event-ID-Store
# (data/offline-event-ids.json) eines früheren Laufs nichts als duplicate markiert.
RUN="$(date +%s)-$$"
API_PORT=5100
KIMAI_PORT=8099
API_URL="http://127.0.0.1:${API_PORT}"
KIMAI_URL="http://127.0.0.1:${KIMAI_PORT}"
PASS=0
FAIL=0

cleanup() {
  [[ -n "${API_PID:-}" ]] && kill "$API_PID" 2>/dev/null || true
  [[ -n "${KIMAI_PID:-}" ]] && kill "$KIMAI_PID" 2>/dev/null || true
  # Port ggf. freimachen (falls ein alter Prozess noch hängt)
  for port in "$API_PORT" "$KIMAI_PORT" "${SIM_PORT:-}"; do
    [[ -z "$port" ]] && continue
    pid=$(ss -tlnp 2>/dev/null | grep ":${port} " | grep -oP 'pid=\K[0-9]+' | head -1 || true)
    [[ -n "$pid" ]] && kill "$pid" 2>/dev/null || true
  done
  # Agent-Sim sauber beenden (FIFO-Schreibende + Prozess)
  exec 3>&- 2>/dev/null || true
  [[ -n "${SIM_PID:-}" ]] && kill "$SIM_PID" 2>/dev/null || true
}
trap cleanup EXIT

say()  { echo; echo "== $* =="; }
ok()   { echo "  ✅ $*"; PASS=$((PASS+1)); }
bad()  { echo "  ❌ $*"; FAIL=$((FAIL+1)); }

assert_status() { # expected_substring actual actual_label
  if echo "$2" | grep -q "$1"; then ok "$3 → enthält '$1'"; else bad "$3 → erwartet '$1', war: $2"; fi
}

wait_for() { # url timeout_s
  for _ in $(seq 1 "$2"); do
    curl -s -o /dev/null -m 2 "$1" && return 0
    sleep 1
  done
  return 1
}

# Der Sync ist pro Client-IP auf 20 Einheiten/min gedrosselt, der Test
# braucht mehr. Die API vertraut im Test 127.0.0.1 als Proxy
# (Stempeluhr:KnownProxies), und jeder Aufruf kommt mit eigener
# X-Forwarded-For-IP. Der Zähler liegt in einer Datei, weil post_sync meist
# in einer Subshell ($(...)) läuft.
next_client_ip() {
  local n
  n=$(( $(cat "$WORK/client-ip-counter" 2>/dev/null || echo 0) + 1 ))
  echo "$n" > "$WORK/client-ip-counter"
  echo "10.0.$(( n / 256 )).$(( n % 256 ))"
}

post_sync() { # json
  curl -s -m 15 -X POST "$API_URL/api/kiosk/clock/sync" \
    -H 'Content-Type: application/json' -H "X-Forwarded-For: $(next_client_ip)" -d "$1"
}

# ---------------------------------------------------------------- Setup
say "Setup: Test-Settings + Workspace ($WORK)"

cat > "$WORK/settings.json" <<EOF
{
  "baseUrl": "$KIMAI_URL",
  "defaultProjectId": 1,
  "defaultActivityId": 1,
  "pauseActivityId": 2,
  "employees": [
    { "id": "test-max",  "displayName": "Max Mustermann", "pin": "1234", "nfcCardId": "04A2B3C4",
      "apiToken": "test-token", "projectId": 1, "activityId": 1,
      "tasks": [ { "id": "kx", "label": "Kunde X", "projectId": 5, "activityId": 6 } ] },
    { "id": "test-anna", "displayName": "Anna Beispiel",  "pin": "4321", "nfcCardId": "04D5E6F7",
      "apiToken": "test-token", "projectId": 1, "activityId": 1 }
  ]
}
EOF

export PATH="$HOME/.dotnet:$PATH"
command -v dotnet >/dev/null || { echo "dotnet nicht gefunden (~/.dotnet im PATH?)"; exit 1; }

echo "  Baue API..."
dotnet build "$ROOT/Stempeluhr.Api/Stempeluhr.Api.csproj" -v q --nologo > /dev/null

# ---------------------------------------------------------------- Start
say "Starte Fake-Kimai (:${KIMAI_PORT}) und Stempeluhr-API (:${API_PORT})"

python3 "$ROOT/tools/testenv/fake_kimai.py" > /dev/null 2>&1 & KIMAI_PID=$!
KIMAI_LOG="$WORK/fake_kimai_log.jsonl"
wait_for "$KIMAI_URL/_bookings" 10 || { echo "Fake-Kimai startete nicht"; exit 1; }
echo "  Fake-Kimai läuft (PID $KIMAI_PID)"

dotnet run --project "$ROOT/Stempeluhr.Api/Stempeluhr.Api.csproj" --no-build \
  --urls "$API_URL" -- \
  "Stempeluhr:SettingsPath=$WORK/settings.json" \
  "Stempeluhr:KnownProxies:0=127.0.0.1" \
  > "$WORK/api.log" 2>&1 & API_PID=$!
wait_for "$API_URL/healthz" 30 || wait_for "$API_URL/api/health" 5 || {
  # Fallback: erster Endpoint, der eine Antwort liefert
  for _ in $(seq 1 20); do
    curl -s -o /dev/null -m 2 -X POST "$API_URL/api/kiosk/clock/sync" -H 'Content-Type: application/json' -d '{"events":[]}' && break
    sleep 1
  done
}
echo "  API läuft (PID $API_PID)"
curl -s -X POST "$API_URL/api/kiosk/clock/sync" -H 'Content-Type: application/json' -d '{"events":[]}' | grep -q '"results"' \
  && echo "  API antwortet" || { echo "  API antwortet nicht - Log:"; tail -30 "$WORK/api.log"; exit 1; }

# ------------------------------------------------- Test 1: Live-Zyklus
say "Test 1: Live-Zyklus (Max: Start → Pause → PauseEnde → Stop)"

R=$(post_sync "{\"events\":[{\"eventId\":\"${RUN}-live-1\",\"employeeId\":\"test-max\",\"pin\":\"1234\",\"action\":\"start\",\"performedAt\":\"2026-08-23T08:00:00+02:00\"}]}")
assert_status '"applied"' "$R" "Einstempeln"

R=$(post_sync "{\"events\":[{\"eventId\":\"${RUN}-live-2\",\"employeeId\":\"test-max\",\"pin\":\"1234\",\"action\":\"pauseStart\",\"performedAt\":\"2026-08-23T09:30:00+02:00\"}]}")
assert_status '"applied"' "$R" "Pausenbeginn"
assert_status '"paused"'  "$R" "Zustand nach Pausenbeginn"

R=$(post_sync "{\"events\":[{\"eventId\":\"${RUN}-live-3\",\"employeeId\":\"test-max\",\"pin\":\"1234\",\"action\":\"pauseEnd\",\"performedAt\":\"2026-08-23T10:15:00+02:00\"}]}")
assert_status '"applied"' "$R" "Pausenende"
assert_status '"working"' "$R" "Zustand nach Pausenende"

R=$(post_sync "{\"events\":[{\"eventId\":\"${RUN}-live-4\",\"employeeId\":\"test-max\",\"pin\":\"1234\",\"action\":\"stop\",\"performedAt\":\"2026-08-23T17:00:00+02:00\"}]}")
assert_status '"applied"'    "$R" "Ausstempeln"
assert_status '"clockedOut"' "$R" "Zustand nach Ausstempeln"

BOOKINGS=$(curl -s "$KIMAI_URL/_bookings")
echo "$BOOKINGS" | grep -q '"activity": 2' && ok "Pause wurde mit Pause-Aktivität gebucht" || bad "Keine Pause-Aktivität im Kimai-Log"

# ------------------------------------------------- Test 1a: Tätigkeitswechsel
say "Test 1a: Tätigkeitswechsel (Max: Start → Kunde X → Pause → Fortsetzen auf Kunde X → zurück → Stop)"

sync_max() { # event-suffix action performedAt [taskId]
  local task=""
  [[ -n "${4:-}" ]] && task=",\"taskId\":\"$4\""
  post_sync "{\"events\":[{\"eventId\":\"${RUN}-$1\",\"employeeId\":\"test-max\",\"pin\":\"1234\",\"action\":\"$2\",\"performedAt\":\"$3\"$task}]}"
}
login_max() {
  curl -s -m 15 -X POST "$API_URL/api/kiosk/pin-login" -H 'Content-Type: application/json' -d '{"pin":"1234"}'
}

R=$(login_max)
assert_status '"tasks":\[{"id":"kx","label":"Kunde X"}\]' "$R" "Kiosk erhält die Tätigkeiten des Mitarbeiters"

R=$(sync_max task-1 start 2026-08-24T08:00:00+02:00)
assert_status '"applied"' "$R" "Einstempeln"
R=$(sync_max task-2 switch 2026-08-24T10:00:00+02:00 kx)
assert_status 'Wechsel zu Kunde X' "$R" "Wechsel zu Kunde X nachgetragen"
assert_status '"activeTaskId":"kx"' "$(login_max)" "Status zeigt Kunde X"
R=$(sync_max task-3 switch 2026-08-24T10:00:00+02:00 kx)
assert_status 'Lief bereits' "$R" "Doppelter Wechsel wird zum No-op"

R=$(sync_max task-4 pauseStart 2026-08-24T12:00:00+02:00)
assert_status '"paused"' "$R" "Pause während Kunde X"
R=$(sync_max task-5 pauseEnd 2026-08-24T12:30:00+02:00)
assert_status '"working"' "$R" "Pausenende"
assert_status '"activeTaskId":"kx"' "$(login_max)" "Nach der Pause läuft wieder Kunde X"

HTTP=$(curl -s -o /dev/null -w '%{http_code}' -m 15 -X POST "$API_URL/api/kiosk/clock" -H 'Content-Type: application/json' \
  -d '{"employeeId":"test-max","pin":"1234","action":"switch","taskId":"gibt-es-nicht"}')
[ "$HTTP" = "400" ] && ok "Live-Wechsel auf unbekannte Tätigkeit -> 400" || bad "Unbekannte Tätigkeit erwartet 400, war $HTTP"

R=$(sync_max task-6 switch 2026-08-24T14:00:00+02:00)
assert_status 'Standard-Taetigkeit' "$R" "Wechsel zurück zur Standard-Tätigkeit"
assert_status '"activeTaskId":null' "$(login_max)" "Status zeigt wieder die Standard-Tätigkeit"
assert_status '"activeIsDefaultTask":true' "$(login_max)" "Status erkennt die Standard-Tätigkeit"
R=$(sync_max task-7 stop 2026-08-24T17:00:00+02:00)
assert_status '"clockedOut"' "$R" "Ausstempeln"

BOOKINGS=$(curl -s "$KIMAI_URL/_bookings")
KX=$(echo "$BOOKINGS" | grep -o '"project": 5' | wc -l)
[ "$KX" -ge 2 ] && ok "Kunde X zweimal gebucht (vor und nach der Pause, $KX Einträge)" || bad "Erwartet 2 Buchungen auf Projekt 5, war $KX"

R=$(sync_max task-8 start 2026-08-25T08:00:00+02:00 kx)
assert_status 'Nachgetragen: Einstempeln' "$R" "Einstempeln direkt auf Kunde X nachgetragen"
assert_status '"activeTaskId":"kx"' "$(login_max)" "Status zeigt Kunde X ab dem Einstempeln"
START_UNKNOWN='{"employeeId":"test-max","pin":"1234","action":"start","taskId":"gibt-es-nicht"}'
R=$(curl -s -m 15 -X POST "$API_URL/api/kiosk/clock" -H 'Content-Type: application/json' -d "$START_UNKNOWN")
assert_status 'Schon eingestempelt' "$R" "Live-Einstempeln auf unbekannte Tätigkeit bei laufender Arbeit bleibt No-op"
R=$(sync_max task-9 stop 2026-08-25T12:00:00+02:00)
assert_status '"clockedOut"' "$R" "Ausstempeln nach Einstempeln auf Kunde X"
HTTP=$(curl -s -o /dev/null -w '%{http_code}' -m 15 -X POST "$API_URL/api/kiosk/clock" -H 'Content-Type: application/json' -d "$START_UNKNOWN")
[ "$HTTP" = "400" ] && ok "Live-Einstempeln auf unbekannte Tätigkeit -> 400" || bad "Unbekannte Tätigkeit erwartet 400, war $HTTP"

R=$(sync_max task-10 start 2026-08-26T08:00:00+02:00 gibt-es-nicht)
assert_status 'Nachgetragen: Einstempeln' "$R" "Offline-Einstempeln auf gelöschte Tätigkeit wird nachgetragen"
assert_status '"activeIsDefaultTask":true' "$(login_max)" "... und läuft auf der Haupttätigkeit"
# Umlaute kommen JSON-escaped an - nur der ASCII-Anfang des Vermerks.
assert_status 'offline gew' "$(curl -s "$KIMAI_URL/_bookings")" "... mit Vermerk im Timesheet"
R=$(sync_max task-11 stop 2026-08-26T12:00:00+02:00)
assert_status '"clockedOut"' "$R" "Ausstempeln nach dem Nachtrag auf die Haupttätigkeit"

# ------------------------------------------------- Test 1b: Stundenübersicht
say "Test 1b: Stundenübersicht (Max stempelt heute bis zu 2h -> /api/kiosk/hours)"

# Die Übersicht zählt eine Buchung zum Tag ihres Beginns, in der Zeitzone des
# Kimai-Benutzers (Fake: Europe/Berlin). "Vor 2 h" läge zwischen 00:00 und
# 02:00 im Vortag (montags in der Vorwoche) - dann beginnt die Buchung kurz
# nach Mitternacht und ist entsprechend kürzer. Um Mitternacht selbst warten,
# bis der Tag sicher gewechselt hat.
while [[ "$(TZ=Europe/Berlin date +%H%M)" > "2357" || "$(TZ=Europe/Berlin date +%H%M)" < "0002" ]]; do
  sleep 20
done
NOW_S=$(date +%s)
MIDNIGHT_S=$(TZ=Europe/Berlin date -d "$(TZ=Europe/Berlin date -d "@$NOW_S" +%F) 00:00" +%s)
START_S=$(( NOW_S - 7200 > MIDNIGHT_S + 60 ? NOW_S - 7200 : MIDNIGHT_S + 60 ))
HOURS_S=$(( NOW_S - START_S ))
START_ISO=$(date -d "@$START_S" +%Y-%m-%dT%H:%M:%S%:z)
STOP_ISO=$(date -d "@$NOW_S" +%Y-%m-%dT%H:%M:%S%:z)

R=$(post_sync "{\"events\":[{\"eventId\":\"${RUN}-hours-1\",\"employeeId\":\"test-max\",\"pin\":\"1234\",\"action\":\"start\",\"performedAt\":\"$START_ISO\"}]}")
assert_status '"applied"' "$R" "Stundenübersicht: Start heute ($START_ISO)"

R=$(post_sync "{\"events\":[{\"eventId\":\"${RUN}-hours-2\",\"employeeId\":\"test-max\",\"pin\":\"1234\",\"action\":\"stop\",\"performedAt\":\"$STOP_ISO\"}]}")
assert_status '"applied"' "$R" "Stundenübersicht: Stop jetzt"

R=$(curl -s -m 15 -X POST "$API_URL/api/kiosk/hours" -H 'Content-Type: application/json' -d '{"pin":"1234"}')
assert_status "\"todaySeconds\":${HOURS_S}," "$R" "Stundenübersicht: Heute = ${HOURS_S}s (Netto)"
assert_status '"todayPauseSeconds":0' "$R" "Stundenübersicht: Pause heute = 0"
WEEK=$(echo "$R" | grep -oP '"weekSeconds":\K[0-9]+' || true)
if [[ -n "$WEEK" ]] && [ "$WEEK" -ge "$HOURS_S" ]; then
  ok "Stundenübersicht: Woche >= ${HOURS_S}s (war $WEEK)"
else
  bad "Stundenübersicht: Woche erwartet >= ${HOURS_S}, war: $R"
fi
MONTH=$(echo "$R" | grep -oP '"monthSeconds":\K[0-9]+' || true)
# Monat ist laufzeitabhängig (Backdate-Events vom 23.08. liegen je nach
# Ausführmonat im Zeitraum) - die Buchung von heute muss auf jeden Fall
# enthalten sein.
if [[ -n "$MONTH" ]] && [ "$MONTH" -ge "$HOURS_S" ]; then
  ok "Stundenübersicht: Monat >= ${HOURS_S}s (war $MONTH)"
else
  bad "Stundenübersicht: Monat erwartet >= ${HOURS_S}, war: $R"
fi

HTTP=$(curl -s -o /dev/null -w '%{http_code}' -m 15 -X POST "$API_URL/api/kiosk/hours" -H 'Content-Type: application/json' -d '{"pin":"9999"}')
[ "$HTTP" = "401" ] && ok "Stundenübersicht: unbekannter PIN -> 401" || bad "Stundenübersicht: unbekannter PIN erwartet 401, war $HTTP"

# ------------------------------------------------- Test 2: Offline-Szenario
say "Test 2: Offline-Szenario (Kimai stoppen → Anna stempelt offline → Kimai starten → Nachtrag)"

kill "$KIMAI_PID" 2>/dev/null; KIMAI_PID=""
sleep 1

R=$(post_sync "{\"events\":[{\"eventId\":\"${RUN}-off-1\",\"employeeId\":\"test-anna\",\"pin\":\"4321\",\"action\":\"start\",\"performedAt\":\"2026-08-23T06:55:00+02:00\"}]}")
assert_status '"buffered"' "$R" "Offline-Einstempeln wird gepuffert (NICHT rejected!)"

R=$(post_sync "{\"events\":[{\"eventId\":\"${RUN}-off-2\",\"employeeId\":\"test-anna\",\"pin\":\"4321\",\"action\":\"pauseStart\",\"performedAt\":\"2026-08-23T09:00:00+02:00\"}]}")
assert_status '"buffered"' "$R" "Offline-Pause wird gepuffert"

R=$(post_sync "{\"events\":[{\"eventId\":\"${RUN}-off-3\",\"employeeId\":\"test-anna\",\"pin\":\"4321\",\"action\":\"stop\",\"performedAt\":\"2026-08-23T16:45:00+02:00\"}]}")
assert_status '"buffered"' "$R" "Offline-Ausstempeln wird gepuffert"

echo "  Starte Fake-Kimai neu..."
(cd /tmp && python3 "$ROOT/tools/testenv/fake_kimai.py" > /dev/null 2>&1 & echo $! > "$WORK/kimai.pid")
KIMAI_PID=$(cat "$WORK/kimai.pid")
# Das Kimai-Log wird im cwd des Prozesses geschrieben.
KIMAI_LOG="/tmp/fake_kimai_log.jsonl"
rm -f "$KIMAI_LOG"
wait_for "$KIMAI_URL/_bookings" 10 || { echo "Fake-Kimai startete nicht"; exit 1; }

echo "  Warte auf Outbox-Flush (Background-Service)..."
FLUSHED=0
for _ in $(seq 1 12); do
  sleep 5
  # Nachtrag verifizieren am rückdatierten Startzeitpunkt (06:55) aus dem
  # Offline-Einstempel-Event - unabhängig davon, ob spätere Events neue
  # Timesheets anlegen oder nur patchen.
  if grep -q '06:55:00' "$KIMAI_LOG" 2>/dev/null; then FLUSHED=1; break; fi
done

if [ "$FLUSHED" = "1" ]; then
  ok "Outbox-Flush hat offline Events rückdatiert nachgetragen (begin 06:55 gefunden)"
else
  bad "Outbox-Flush hat keine Events nachgetragen (Log: $KIMAI_LOG)"
fi

# Idempotenz: gleiche Event-IDs erneut senden → duplicates
R=$(post_sync "{\"events\":[{\"eventId\":\"${RUN}-off-1\",\"employeeId\":\"test-anna\",\"pin\":\"4321\",\"action\":\"start\",\"performedAt\":\"2026-08-23T06:55:00+02:00\"}]}")
assert_status '"duplicate"' "$R" "Re-Send wird als duplicate erkannt (Idempotenz)"

# ------------------------------------------------- Test 3: Permanente Fehler
say "Test 3: Permanente Fehler werden rejected (nicht gepuffert)"

R=$(post_sync "{\"events\":[{\"eventId\":\"${RUN}-bad-pin-1\",\"employeeId\":\"test-max\",\"pin\":\"9999\",\"action\":\"start\",\"performedAt\":\"2026-08-23T08:00:00+02:00\"}]}")
assert_status '"rejected"' "$R" "Falsche PIN → rejected"

R=$(post_sync "{\"events\":[{\"eventId\":\"${RUN}-bad-card-1\",\"employeeId\":\"unbekannt\",\"pin\":\"1234\",\"action\":\"start\",\"performedAt\":\"2026-08-23T08:00:00+02:00\"}]}")
assert_status '"rejected"' "$R" "Unbekannter Mitarbeiter → rejected"

# ------------------------------------------------- Test 3a: Rate-Limit Sync
say "Test 3a: Sync ist pro Client auf 20 Einheiten/min gedrosselt (Issue #53)"

limited_post() { # path json
  curl -s -o /dev/null -w '%{http_code}' -m 5 -X POST "$API_URL$1" \
    -H 'Content-Type: application/json' -H 'X-Forwarded-For: 10.99.0.1' -d "$2"
}

LIMIT_OK=1
for _ in $(seq 1 20); do
  [[ "$(limited_post /api/kiosk/clock/sync '{"events":[]}')" == "200" ]] || LIMIT_OK=0
done
[[ $LIMIT_OK == 1 ]] && ok "20 Sync-Anfragen eines Clients gehen durch" \
  || bad "Sync drosselt vor der 20. Anfrage"
CODE=$(limited_post /api/kiosk/clock/sync '{"events":[]}')
[[ "$CODE" == "429" ]] && ok "21. Sync-Anfrage desselben Clients → 429" \
  || bad "21. Sync-Anfrage desselben Clients → erwartet 429, war $CODE"
CODE=$(limited_post /api/kiosk/identify '{"cardId":"04000000","terminalId":"e2e-limit"}')
[[ "$CODE" != "429" ]] && ok "Identify hat ein eigenes Budget (war $CODE)" \
  || bad "Identify teilt das erschöpfte Sync-Budget (429)"

# ------------------------------------------------- Test 3b: Karte zuordnen (Admin)
say "Test 3b: Kiosk-Identifikation erscheint als letzte Karte für die Admin-Seite"

R=$(curl -s -m 10 -X POST "$API_URL/api/kiosk/identify" -H 'Content-Type: application/json' \
  -d '{"cardId":"04 99 88 77","terminalId":"e2e-admin"}')
assert_status '"success":false' "$R" "Unbekannte Karte wird nicht identifiziert"
R=$(curl -s -m 5 "$API_URL/api/nfc/events/latest?terminalId=e2e-admin")
assert_status '"cardId":"04998877"' "$R" "Admin sieht die unbekannte Karte zum Zuordnen"

R=$(curl -s -m 10 -X POST "$API_URL/api/kiosk/identify" -H 'Content-Type: application/json' \
  -d '{"cardId":"04A2B3C4","terminalId":"e2e-admin"}')
assert_status '"success":true' "$R" "Bekannte Karte wird identifiziert"
R=$(curl -s -m 5 "$API_URL/api/nfc/events/latest?terminalId=e2e-admin")
assert_status '"displayName":"Max Mustermann"' "$R" "Admin sieht den zugeordneten Mitarbeiter"

# Der SPA-Fallback darf hier antworten - entscheidend ist, dass kein
# Sync-Ergebnis mehr zurückkommt.
R=$(curl -s -m 5 -X POST "$API_URL/api/nfc/clock/sync" -H 'Content-Type: application/json' -d '{"events":[]}')
[[ "$R" != *'"results"'* ]] && ok "Alter NFC-Toggle-Endpunkt ist entfernt" \
  || bad "/api/nfc/clock/sync liefert noch ein Sync-Ergebnis"

# ------------------------------------------------- Test 4: Agent-Level-Offline-Identifikation
# Grenze: Ein vollständiges Browser-/Angular-E2E ist hier nicht machbar - der
# NFC-Agent braucht einen PC/SC-Reader (pyscard). Stattdessen steuert
# local_scan_sim.py den ECHTEN Agent-Code (LocalScanServer, Ack-Watchdog)
# an; die Kiosk-UI wird per curl gegen 127.0.0.1:<port>
# simuliert. Die API selbst ist in Test 1-3 bereits abgedeckt.
say "Test 4: Agent-Level-Simulation (LocalScanServer: Publish → Ack / Timeout → Drop)"

SIM_PORT=18737
cat > "$WORK/agent-config.json" <<EOF
{
  "api_base_url": "$API_URL",
  "terminal_id": "e2e-sim",
  "local_port": $SIM_PORT,
  "selection_timeout_seconds": 1.5
}
EOF

start_sim() { # config_file
  # FIFO als stdin, damit wir dem Sim laufend Kommandos schicken können.
  local fifo="$WORK/sim-stdin.fifo"
  rm -f "$fifo"; mkfifo "$fifo"
  python3 "$ROOT/tools/testenv/local_scan_sim.py" "$1" < "$fifo" > "$WORK/sim.log" 2>&1 & SIM_PID=$!
  exec 3>"$fifo"   # Schreibende offen halten
  for _ in $(seq 1 20); do
    grep -q '^SIM_READY' "$WORK/sim.log" 2>/dev/null && break
    sleep 0.5
  done
  grep -q '^SIM_READY' "$WORK/sim.log" || { bad "Sim startete nicht ($(tail -5 "$WORK/sim.log"))"; return 1; }
}

stop_sim() {
  exec 3>&- 2>/dev/null || true
  [[ -n "${SIM_PID:-}" ]] && kill "$SIM_PID" 2>/dev/null || true
  wait "${SIM_PID:-0}" 2>/dev/null || true
  SIM_PID=""
}

sim_cmd() { echo "$*" >&3; }

scan_url="http://127.0.0.1:${SIM_PORT}/scan/latest"

wait_drained() {
  # drain only queues the command - wait until the worker actually finished
  # (watchdog timeout elapsed) before grepping the log.
  for _ in $(seq 1 40); do
    grep -q '^SIM_DRAINED' "$WORK/sim.log" 2>/dev/null && break
    sleep 0.5
  done
}

# --- 4a: Publish + Ack-Pfad ---
if start_sim "$WORK/agent-config.json"; then
  ok "Agent-Sim läuft auf Port $SIM_PORT"

  R=$(curl -s -m 5 "http://127.0.0.1:${SIM_PORT}/health")
  assert_status '"version"' "$R" "/health meldet die Agent-Version"

  sim_cmd handle 04A2B3C4
  sleep 0.4
  R=$(curl -s -m 5 "$scan_url")
  assert_status '"cardId": "04A2B3C4"' "$R" "/scan/latest liefert den Scan"
  assert_status '"consumed": false'    "$R" "Scan noch nicht consumed vor Ack"

  curl -s -m 5 -X POST "${scan_url%/latest}/ack" | grep -q '"ok"' \
    && ok "/scan/ack bestätigt den Scan" || bad "/scan/ack fehlgeschlagen"

  R=$(curl -s -m 5 "$scan_url")
  assert_status '"consumed": true' "$R" "/scan/latest zeigt consumed nach Ack"

  sim_cmd drain
  wait_drained
  grep -q 'SIM_HANDLED 04A2B3C4 outcome=acked' "$WORK/sim.log" \
    && ok "Acked Scan wird als acked gemeldet" \
    || bad "Erwartete outcome=acked, sim.log: $(tail -5 "$WORK/sim.log")"
fi
stop_sim

# --- 4b: Timeout ohne Ack → Scan verworfen und abgelaufen ---
if start_sim "$WORK/agent-config.json"; then
  sim_cmd handle 04D5E6F7
  sim_cmd drain
  wait_drained
  grep -q 'SIM_HANDLED 04D5E6F7 outcome=dropped' "$WORK/sim.log" \
    && ok "Timeout ohne Ack → Scan verworfen (keine Buchung)" \
    || bad "Erwartete outcome=dropped, sim.log: $(tail -5 "$WORK/sim.log")"
  R=$(curl -s -m 5 "$scan_url")
  assert_status '"consumed": true' "$R" "Verworfener Scan ist abgelaufen (spätes Ack wirkungslos)"
fi
stop_sim

# ---------------------------------------------------------------- Fazit
say "Ergebnis: $PASS bestanden, $FAIL fehlgeschlagen"
if [ "$FAIL" = "0" ]; then
  echo "🎉 Alle Integrationstests bestanden!"
  exit 0
fi
echo "API-Log: $WORK/api.log"
echo "Kimai-Log: $WORK/fake_kimai_log.jsonl"
exit 1
