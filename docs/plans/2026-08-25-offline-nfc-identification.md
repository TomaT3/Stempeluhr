# Offline-NFC-Identifikation (Issue #11) Implementation Plan

> **For Hermes:** Use subagent-driven-development skill to implement this plan task-by-task.

**Goal:** NFC-Scan hat online UND offline dieselbe Semantik (Identifikation); Start/Pause/Ende werden offline über die Browser-Queue gestempelt. Der Pi-Agent stellt dafür einen lokalen Scan-Endpoint bereit.

**Architecture:** Der Agent (Python) bekommt einen Loopback-HTTP-Server (`127.0.0.1:8737`), der den letzten Scan als JSON bereitstellt (analog `/api/nfc/events/latest`). Die Angular-Clock-Page pollt bei Backend-Ausfall zusätzlich lokal, meldet den Mitarbeiter an und stempelt über die bestehende Offline-Queue (kiosk sync, Rückdatierung). Reagiert niemand im UI innerhalb `selection_timeout` (10 s), greift der konfigurierbare Fallback (Default: kein Stempel; optional Toggle wie heute).

**Tech Stack:** Python stdlib (`http.server` + Threading, keine neuen Deps), Angular signals + existing offline-queue, ASP.NET Core unverändert.

**Semantik nach dem Umbau (eine Regel, überall gültig):**
Scan identifiziert immer. Stempeln tut nur der Button (UI) — oder der Fallback, wenn das UI den Scan nicht annimmt.

---

## Task 1: Lokaler Scan-Server im Agent

**Objective:** Agent betreibt einen Loopback-HTTP-Server, der den letzten Scan abfragbar macht.

**Files:**
- Modify: `tools/pi-nfc-agent/stempeluhr_nfc_agent.py`
- Test: `tools/pi-nfc-agent/test_local_scan_server.py` (neu)

**Step 1: Failing test**

```python
def test_latest_scan_endpoint_returns_scan_and_clears_on_ack():
    server = LocalScanServer("127.0.0.1", 0)
    server.publish_scan(card_id="04AB", scanned_at_epoch=1000.0)
    body = http_get_json(server.url + "/scan/latest")
    assert body == {"cardId": "04AB", "scannedAt": "…iso…", "consumed": False}
    # Acknowledgement durch UI:
    requests.post(server.url + "/scan/ack")
    body = http_get_json(server.url + "/scan/latest")
    assert body["consumed"] is True
```

**Step 2: Run** — `python3 tools/pi-nfc-agent/test_local_scan_server.py` → FAIL.

**Step 3: Implementieren** — Klasse `LocalScanServer` mit `http.server.ThreadingHTTPServer`:
- `GET /scan/latest` → letzter Scan + `consumed`-Flag
- `POST /scan/ack` → markiert Scan als konsumiert
- Thread-Safe via Lock; bind nur auf `127.0.0.1`; Port in `config.json` (`local_port`, Default 8737).
- In `main()`/`run()` neben dem bestehenden Retry-Thread starten.

**Step 4: Tests grün**, **Step 5: Commit** `feat(agent): local loopback scan server for offline identification`.

## Task 2: Scan-Handling umschreiben (kein Auto-Toggle mehr)

**Objective:** Offline-Scan wird nicht mehr als Toggle gequeued, sondern published; Fallback erst nach Timeout.

**Files:**
- Modify: `tools/pi-nfc-agent/stempeluhr_nfc_agent.py` (`handle_card_scan`, `run`)
- Test: `tools/pi-nfc-agent/test_offline_queue.py` erweitern

**Verhalten:**
1. Scan → immer `LocalScanServer.publish_scan(...)` + Status-Beep.
2. Watchdog-Thread: nach `selection_timeout` (Default 10 s, config) ohne `ack` → Fallback:
   - `fallback_mode: none` (Default): nichts queuen, Log + Error-Beep.
   - `fallback_mode: toggle`: heutiges Verhalten (Queue + Status-Cache-Toggle) — Code bleibt erhalten.
3. Kommt ein neuer Scan vor Ablauf, ersetzt er den vorherigen (nur letzter zählt).

**Test:** Publish statt Queue bei ack; Toggle-Fallback bei Timeout+toggle-mode; kein Event bei Timeout+none.

**Commit:** `feat(agent): scans identify instead of toggling, fallback configurable`.

## Task 3: Angular — lokalen Scan-Poll bei Offline

**Objective:** Clock-Page erkennt offline Scans vom lokalen Agent und meldet den Mitarbeiter an.

**Files:**
- Create: `stempeluhr-client/src/app/core/services/local-nfc-scan.service.ts` (+ spec)
- Modify: `stempeluhr-client/src/app/features/clock/clock-workflow.ts`

**Verhalten:**
- `LocalNfcScanService.poll()` — GET `http://127.0.0.1:<port>/scan/latest`, nur aktiv wenn `isOffline()`.
- Neuer Scan (`scannedAt` frischer als letzter gesehener, `consumed === false`) → Mitarbeiter anhand `cardId` aus dem bekannten Katalog zuordnen (offline verfügbar, da die Clock-Page die Employee-Liste vom letzten Online-Stand cached — prüfen/falls nötig in localStorage persistieren) → `selectedEmployee` setzen, `POST /scan/ack`.
- Kein Match → Message „Unbekannte Karte".
- Nach Anmeldung gelten die normalen Buttons; deren Events landen automatisch in der bestehenden Offline-Queue mit korrektem `performedAt`.

**Commit:** `feat(client): accept NFC scans from local agent while offline`.

## Task 4: UI-Hinweise für den Fallback-Fall

**Objective:** Nutzer verstehen, was ein offline Scan bedeutet.

**Files:**
- Modify: `stempeluhr-client/src/app/features/clock/clock-page/clock-page.html/.ts`

**Verhalten:**
- Offline-Banner-Text ergänzen: „NFC-Scan meldet Sie an – stempeln Sie dann per Button."
- Fallback `none` + verstrichener Scan: dezente Message „Scan am Terminal erkannt, aber nicht bestätigt".

**Commit:** `feat(client): offline scan guidance messages`.

## Task 5: E2E + Doku

**Files:**
- Modify: `tools/testenv/run_e2e_test.sh` (Szenario: Backend down → Scan → Button start/pause/stop → Backend up → Sync-Reihenfolge prüfen)
- Modify: `tools/pi-nfc-agent/README.md` (neue Config-Keys, neue Semantik)
- Modify: Issue #11 (Kommentar mit Umsetzung)

**Akzeptanzkriterien (aus Issue abgeleitet):**
- [ ] Offline-Scan erzeugt KEINE Phantom-Buchung (Toggle-Ketten weg)
- [ ] Pause offline möglich (einloggen per Karte, Pause drücken, später synchronisiert)
- [ ] Keine parallelen Agent-/Browser-Queues im Normalfall (Agent-Queue nur noch im Toggle-Fallback)
- [ ] Konfigurierbarer Fallback, Default sicher (kein Stempel)

**Commit:** `test+docs: offline NFC identification e2e and docs`.
