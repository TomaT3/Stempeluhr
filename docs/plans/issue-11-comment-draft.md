# Kommentar-Entwurf für Issue #11 (Offline-NFC-Identifikation)

> **Hinweis:** Entwurf – NICHT direkt posten, vorher von Owner gegenprüfen.

---

## Umsetzung

Der Zweig `feat/offline-nfc-identification` ändert die Semantik eines
Karten-Scans grundlegend: Ein Scan ist jetzt **immer nur eine
Identifikation** – online wie offline. Gestempelt wird ausschließlich über
den Button am Kiosk (`/api/kiosk/clock`).

**Agent (`tools/pi-nfc-agent/`):**

- Neuer **LocalScanServer**: Loopback-HTTP-Server (bindet nur auf
  `127.0.0.1:8737`). Die Kiosk-UI pollt `GET /scan/latest` und bestätigt die
  Übernahme mit `POST /scan/ack`.
- **Publish statt Toggle:** Jeder Scan wird zunächst veröffentlicht; erst
  wenn innerhalb von `selection_timeout_seconds` (Default 10 s) kein Ack
  eintrifft, greift der konfigurierte Fallback.
- **Fallback-Config** `fallback_mode`:
  - `none` (neuer Default): Scan wird verworfen (Log + Fehler-Signal) – kein
    heimliches Buchen mehr.
  - `toggle`: Legacy-Verhalten, Scan landet als Toggle in der persistierenden
    Offline-Queue und wird beim Drain über `/api/nfc/clock/sync` angewendet.
- Late-Ack-Race geschlossen: Ein Ack kurz nach dem Timeout zählt noch und
  löst **keinen** Fallback aus.

**Angular-Client:**

- `LocalNfcScanService`: Polling des lokalen Agent-Servers, Ack nach Annahme.
- Offline-Scans werden angenommen und angezeigt; Card-Cache merkt sich den
  letzten bekannten Status pro Karte.
- Ehrlicher „Status unbekannt“-Badge, wenn der Zustand nicht verlässlich
  ermittelt werden kann.

## Konfiguration (Auszug)

```json
{
  "api_base_url": "https://stempeluhr.example.local",
  "terminal_id": "stempeluhr-pi-01",
  "reader_token": "…",
  "local_port": 8737,
  "selection_timeout_seconds": 10,
  "fallback_mode": "none"
}
```

Für das alte Verhalten (offline Scans als Toggle nachreichen):

```json
{ "fallback_mode": "toggle" }
```

Trade-offs sind in `tools/pi-nfc-agent/README.md` dokumentiert.

## Bekannte Einschränkungen

- Der Ack-Watchdog blockiert den Reader-Loop bis zu
  `selection_timeout_seconds`; die Erkennung, dass die Karte abgenommen
  wurde, verzögert sich entsprechend.
- `beep()` ist ein Terminal-Bell (`\a`) – ein kopfloser Pi ohne Konsole gibt
  kein akustisches Signal. Echtes Feedback braucht einen Buzzer/GPIO.
- Der Card-Cache invalidiert Karten-Entzug bzw. fremde Buchungen nicht
  proaktiv; im Toggle-Fallback kann der angenommene Zustand falsch sein.

## Tests

- Unit-Tests Agent + Queue: grün (`python3 test_*.py`)
- E2E (`bash tools/testenv/run_e2e_test.sh`): API-Zyklen, Offline-Pufferung,
  Idempotenz sowie neue Agent-Level-Simulation (Publish → Ack → consumed;
  Timeout + `none` → kein Queue-Event; Timeout + `toggle` → Queue-Event).
