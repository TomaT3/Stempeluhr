# Stempeluhr NFC-Agent (Raspberry Pi)

Python-Dienst für einen ACR122U-Kartenleser (PC/SC). Der Agent liest die UID
einer aufgelegten Karte, speichert den Scan zuerst persistent in einer
Offline-Queue und versucht danach, ihn an die Stempeluhr-API zuzustellen.
Ist die API (oder das Internet) nicht erreichbar, bleibt das Ereignis in der
Queue liegen; ein Hintergrund-Thread holt es mit begrenztem Backoff nach,
sobald die Verbindung wieder steht. Zeitstempel werden zum Scan-Zeitpunkt
erfasst und beim Nachreichen mit dem ursprünglichen Zeitpunkt an den Server
übergeben.

## Dateien

- `stempeluhr_nfc_agent.py` – Hauptschleife: Reader-Auswahl, UID-Erkennung,
  Debouncing, Queueing und Zustellung; inklusive Retry-Thread.
- `offline_queue.py` – persistente JSON-Queue (Datei-fsync + atomarer
  Rewrite via tmp-Datei und `os.replace`, danach fsync des Verzeichnisses).
- `test_offline_queue.py` – kleiner Selbsttest: `python3 test_offline_queue.py`
- `config.example.json` – Beispielkonfiguration.
- `stempeluhr-nfc-agent.service` – systemd-Unit (`Restart=always`).

## Konfiguration

`config.example.json` nach `/etc/stempeluhr-nfc-agent/config.json` kopieren
und anpassen:

| Key | Default | Bedeutung |
| --- | --- | --- |
| `api_base_url` | – (Pflicht) | Basis-URL der Stempeluhr-API |
| `terminal_id` | `default` | Terminal-Kennung für Queue-Events |
| `reader_token` | – | Token für `/api/nfc/*` |
| `debounce_seconds` | `3` | Entprellung pro Karte |
| `reader_name_contains` | – | Filter auf den PC/SC-Reader-Namen (z. B. `ACR122`) |
| `queue_path` | `/var/lib/stempeluhr-nfc-agent/offline-queue.json` | Persistente Offline-Queue |
| `local_port` | `8737` | Loopback-Port des LocalScanServers (bindet nur auf `127.0.0.1`) |
| `selection_timeout_seconds` | `10` | Wie lange der Agent auf ein Ack der Kiosk-UI wartet, bevor der Fallback greift |
| `fallback_mode` | `none` | Verhalten nach Ack-Timeout: siehe unten |

### `fallback_mode`: Trade-offs

- **`none`** (Standard): Ein Scan ist **nur eine Identifikation** – online wie
  offline. Ohne Ack der UI wird der Scan verworfen (Log + Fehler-Piep), es
  entsteht **keine Buchung**. Sauber und vorhersehbar; Nachteil: Ist der
  Kiosk-Browser tot oder hängt, geht ein Stempelwunsch verloren.
- **`toggle`**: Nach dem Timeout wird der Scan als Toggle-Event in die
  Offline-Queue gestellt und beim Drain über `/api/nfc/clock/sync`
  angewendet (nicht eingestempelt → einstempeln, sonst ausstempeln).
  Vorteil: Ein Stempelwusch überlebt auch ohne laufende UI. Nachteile:
  Die Wirkung hängt vom (möglicherweise unbekannten) Zustand ab, wiederholtes
  Scannen schaltet hin und her, und der Toggle kann parallel zum Button-Event
  eine laufende Session verschieben oder aufheben.

Unbekannte Werte werden mit Warnung wie `none` behandelt.

## Semantik eines Karten-Scans

Ein Scan bedeutet **Identifikation**, nicht Stempeln: Der Agent legt die UID
auf dem LocalScanServer ab (`GET /scan/latest`, Bestätigung via
`POST /scan/ack`) und die Kiosk-Oberfläche zeigt dem Mitarbeiter den
aktuellen Status; die eigentliche Buchung löst der Button am Kiosk über
`/api/kiosk/clock` aus. Nur wenn innerhalb von
`selection_timeout_seconds` kein Ack eintrifft, greift `fallback_mode`
(s. o.) – bei `none` bleibt es beim reinen Identifizieren.

Historisch (und mit `fallback_mode=toggle` weiterhin aktiv) gilt die
Divergenz: Online identifiziert `/api/nfc/clock` nur (`IdentifyWithNfcCardAsync`),
offline wird der Queuedrain per `/api/nfc/clock/sync` zum **Toggle**
(`ApplyScanAsync`).

### Konsequenzen

1. Die Wirkung ein und desselben Scans hängt vom Verbindungsstatus ab:
   online findet buchhalterisch nichts statt (nur Identifikation), offline
   ändert der Drain die Buchung.
2. Wiederholtes Scannen ohne sichtbares UI-Feedback erzeugt offline mehrere
   Events; beim Nachziehen schalten die Toggles dadurch hin und her
   (start → stop → start …).
3. Mitten in einer laufenden Session wird der Offline-Toggle zusätzlich zum
   Button-Event angewendet und kann dieses damit verschieben oder aufheben.

### Empfehlung

Die Karte pro Vorgang nur **einmal** scannen. Ist die Verbindung online,
zeigt die Oberfläche den aktuellen Status an – vor einem erneuten Scan dort
nachsehen, statt die Karte noch einmal aufzulegen.

## Bekannte Einschränkungen

- **Watchdog blockiert den Reader-Loop:** `handle_card_scan` wartet bis zu
  `selection_timeout_seconds` synchron auf das Ack. In dieser Zeit wird
  keine neue Karte erkannt und das Abnehmen der Karte (`wait_until_card_removed`)
  wird um dieselbe Zeitspanne verzögert.
- **`beep()` ist ein Terminal-Bell:** Das akustische Feedback ist `\a` auf
  stderr – ein kopfloser Pi ohne angeschlossene/öffnende Konsole gibt nichts
  von sich. Für echte Rückmeldung ist ein Buzzer/GPIO-Feedback nötig.
- **Card-Cache invalidiert nicht proaktiv:** Der CardStatusCache merkt sich
  den letzten bekannten Buchungsstatus pro Karte, erfährt aber nichts davon,
  wenn die Karte zwischenzeitlich woanders (anderes Terminal, direkte UI-
  Bedienung) gestempelt hat – im Toggle-Fallback kann die Annahme dann falsch
  sein.
