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
und anpassen: `api_base_url`, `terminal_id`, `reader_token`,
`debounce_seconds`, `reader_name_contains`, `queue_path`.

## Semantik eines Karten-Scans: online vs. offline

Diese Divergenz ist bewusst so dokumentiert (Owner-Entscheidung); sie ist
kein Verhaltensänderung dieses Zweigs, sondern Beschreibung des Ist-Zustands.

- **Online:** `/api/nfc/clock` identifiziert die Karte lediglich
  (`IdentifyWithNfcCardAsync`). Es wird **kein Stempel** gesetzt – die
  eigentliche Buchung löst der Knopf am Kiosk über `/api/kiosk/clock` aus.
- **Offline:** Der Scan wird in der Queue vorgehalten und beim Drain über
  `/api/nfc/clock/sync` als **Toggle** auf die Buchung angewendet
  (`ApplyScanAsync`): nicht eingestempelt bedeutet einstempeln, sonst
  ausstempeln; im Pausenfall entsprechend weiter.

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
