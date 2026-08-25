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
  for port in "$API_PORT" "$KIMAI_PORT"; do
    pid=$(ss -tlnp 2>/dev/null | grep ":${port} " | grep -oP 'pid=\K[0-9]+' | head -1 || true)
    [[ -n "$pid" ]] && kill "$pid" 2>/dev/null || true
  done
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

post_sync() { # json
  curl -s -m 15 -X POST "$API_URL/api/kiosk/clock/sync" \
    -H 'Content-Type: application/json' -d "$1"
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
      "apiToken": "test-token", "projectId": 1, "activityId": 1 },
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
  "Stempeluhr:SettingsPath=$WORK/settings.json" "Stempeluhr:NfcReaderToken=test-reader-token" \
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

# ---------------------------------------------------------------- Fazit
say "Ergebnis: $PASS bestanden, $FAIL fehlgeschlagen"
if [ "$FAIL" = "0" ]; then
  echo "🎉 Alle Integrationstests bestanden!"
  exit 0
fi
echo "API-Log: $WORK/api.log"
echo "Kimai-Log: $WORK/fake_kimai_log.jsonl"
exit 1
