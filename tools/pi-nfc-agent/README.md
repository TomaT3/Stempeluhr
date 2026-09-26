# Stempeluhr NFC-Agent (Raspberry Pi)

Kleiner Python-Dienst für einen ACR122U-Kartenleser (PC/SC). Er liest die UID
einer aufgelegten Karte und stellt sie der Kiosk-Web-App über einen
Loopback-Server bereit. **Ein Scan identifiziert nur** – gebucht wird erst über
den Knopf am Kiosk. Offline-Queue und Nachtrag liegen vollständig in der Web-App
und in der API, nicht im Agenten.

## Schnittstelle (nur `127.0.0.1`)

| Methode | Pfad | Zweck |
| --- | --- | --- |
| `GET` | `/scan/latest` | letzter Scan: `cardId`, `scannedAt`, `consumed` |
| `POST` | `/scan/ack` | Kiosk bestätigt den Scan |
| `GET` | `/health` | `{"ok": true, "version": "x.y.z"}` |

Bestätigt die Kiosk-App einen Scan nicht innerhalb von
`selection_timeout_seconds`, wird er verworfen (Log + Fehler-Piep). CORS- und
Private-Network-Header erlauben den Zugriff aus der HTTPS-Kiosk-Seite.

## Dateien

- `stempeluhr_nfc_agent.py` – Leserauswahl, UID-Erkennung, Entprellung,
  Loopback-Server und Ack-Watchdog
- `stempeluhr-nfc-agent.service` – systemd-Unit des Agenten
- `stempeluhr-nfc-agent-update.service` / `.timer` – Auto-Update (Boot + alle
  15 Minuten)
- `update.sh` – holt `/pi/agent.json` vom Server, prüft SHA-256, installiert
  nach `/opt/stempeluhr-nfc-agent/releases/<version>`, setzt `current` um,
  prüft `/health` und rollt bei Fehler zurück
- `install.sh` – Ersteinrichtung bzw. Umstellung eines Pis
  (siehe [docs/raspberry-pi-kiosk-nfc.md](../../docs/raspberry-pi-kiosk-nfc.md))
- `build-bundle.sh` – baut im Docker-Image das Bundle für `/pi/`
- `config.example.json` – Beispielkonfiguration
- `test_scan_handling.py`, `test_local_scan_server.py` – Selbsttests ohne
  Kartenleser (`python3 <datei>`); Updater-Test:
  `bash tools/testenv/test_pi_update.sh`

## Konfiguration

`/etc/stempeluhr-nfc-agent/config.json`:

| Key | Default | Bedeutung |
| --- | --- | --- |
| `api_base_url` | – (Pflicht) | Basis-URL der Stempeluhr, z. B. `https://stempeluhr.example.com` |
| `terminal_id` | `default` | Terminal-Kennung, gleich dem `terminalId` der Kiosk-URL |
| `debounce_seconds` | `3` | Entprellung pro Karte |
| `reader_name_contains` | – | Filter auf den PC/SC-Reader-Namen (z. B. `ACR122`) |
| `local_port` | `8737` | Port des Loopback-Servers |
| `selection_timeout_seconds` | `10` | Wartezeit auf das Ack der Kiosk-App |

Schlüssel älterer Versionen (`reader_token`, `queue_path`, `fallback_mode`)
werden ignoriert.

## Bekannte Einschränkungen

- Während der Watchdog auf das Ack wartet, erkennt der Reader-Loop keine neue
  Karte.
- `beep()` schreibt nur ein Terminal-Bell auf stderr; für hörbares Feedback
  wäre ein Buzzer nötig.
