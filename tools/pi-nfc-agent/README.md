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
| `GET` | `/terminal/catalog` | authentifizierter Mitarbeiter-Katalog vom Server |
| `POST` | `/terminal/sync` | PIN-freier Queue-Nachtrag mit Agent-Token |
| `GET` | `/health` | `{"ok": true, "version": "x.y.z"}` |
| `POST` | `/diagnostics/heartbeat` | technisches Browser-Lebenszeichen; ausschließlich konfigurierte Origin |
| `GET` | `/diagnostics` | letzte Pi-/Browser-Diagnose für lokale Wartung |

Bestätigt die Kiosk-App einen Scan nicht innerhalb von
`selection_timeout_seconds`, wird er verworfen (Log + Fehler-Piep). CORS- und
Private-Network-Header erlauben den Zugriff nur aus der Kiosk-Seite: Deren
Origin ist die von `api_base_url` (oder `kiosk_origin`). Eine andere Seite
im Kiosk-Browser bekommt für `/scan/*` ein 403 und kann Scans weder lesen
noch bestätigen. Anfragen ohne `Origin` (curl, Updater) bleiben erlaubt,
`/health` ist offen. Für `/terminal/*` ist die konfigurierte Origin (normalisiert wie bei Scans)
auch bei curl Pflicht. Der Agent setzt `Authorization: Bearer …` und
`X-Terminal-Id` selbst, folgt keinen Redirects und liefert das Token nie aus.
Der Agent setzt die eigene Terminal-ID; eine abweichende Kiosk-URL blockiert
den Nachtrag nicht.
Fehlendes Token ergibt 503 (Browser behält bis zum ersten erfolgreichen
Katalogabruf den Legacy-Nachtrag); 401 bei Entzug/Rotation wird an den Browser
weitergereicht. Die Queue bleibt dabei erhalten.

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
- `pcsc_maintenance.py`, `probe_reader.py`, `pcsc/` – explizite Paketmigration
  mit Originalsicherung und Rollback; Leserprüfung ohne Scan. Pakete entstehen
  beim Docker-Build aus `tools/pcsc`; der normale Updater prüft nur den Fixstand.
- `config.example.json` – Beispielkonfiguration
- `test_scan_handling.py`, `test_local_scan_server.py` – Selbsttests ohne
  Kartenleser (`python3 <datei>`); Updater-Test:
  `bash tools/testenv/test_pi_update.sh`

Der Agent hält einen Leser und dessen PC/SC-Kontext zwischen Abfragen offen.
Jeder gelesene Kartenhandle wird geschlossen; ohne Karte entsteht kein neuer
Kontext pro Abfrage. Bei USB-/Dienstfehlern wird die Sitzung freigegeben und
neu aufgebaut, ebenso werden Ressourcen bei SIGTERM freigegeben. Das ersetzt
den Polkit-Fix ab pcsc-lite 2.5.0 nicht: Berechtigungsprüfungen finden weiterhin
statt. [Bestandsmigration und Rollback](../../docs/raspberry-pi-kiosk-nfc.md#pcsc-paketmigration-bestehender-terminals).

## Konfiguration

`/etc/stempeluhr-nfc-agent/config.json`:

| Key | Default | Bedeutung |
| --- | --- | --- |
| `api_base_url` | – (Pflicht) | Basis-URL der Stempeluhr, z. B. `https://stempeluhr.example.com` |
| `terminal_token` | – | geheimes, auf dem Server unter `terminalTokens.<id>` registriertes Token; Einrichtung/Rotation über `install.sh --terminal-token-file PFAD` |
| `terminal_id` | `default` | Terminal-Kennung, gleich dem `terminalId` der Kiosk-URL |
| `debounce_seconds` | `3` | Entprellung pro Karte |
| `reader_name_contains` | – | Filter auf den PC/SC-Reader-Namen (z. B. `ACR122`) |
| `local_port` | `8737` | Port des Loopback-Servers |
| `selection_timeout_seconds` | `10` | Wartezeit auf das Ack der Kiosk-App |
| `kiosk_origin` | Origin von `api_base_url` | Nur nötig, wenn der Kiosk die Seite von einer anderen Adresse lädt als `api_base_url`. Das Journal meldet abgelehnte Origins. |

Schlüssel älterer Versionen (`reader_token`, `queue_path`, `fallback_mode`)
werden ignoriert.

## Bekannte Einschränkungen

Messwerte und eine begrenzte neustartfeste Historie sind in
[Terminal-Diagnose](../../docs/terminal-diagnostics.md) beschrieben.

- Während der Watchdog auf das Ack wartet, erkennt der Reader-Loop keine neue
  Karte.
- `beep()` schreibt nur ein Terminal-Bell auf stderr; für hörbares Feedback
  wäre ein Buzzer nötig.
