# Stempeluhr für Kimai

Touchfreundliche Web-Stempeluhr für eine gehostete Kimai-Instanz. Mitarbeiter
identifizieren sich per PIN oder NFC-Karte und wählen anschließend Kommen,
Gehen, Pausenbeginn oder Pausenende.

- Mitarbeiteransicht: `/clock`
- kompakte Kioskansicht: `/terminal?terminalId=<id>`
- Administration: `Admin` in der Web-App

## Architektur

- `Stempeluhr.Api`: .NET 10 Minimal API, Kimai-Proxy und Auslieferung des
  gebauten Frontends
- `stempeluhr-client`: Angular-Client für Mitarbeiter, Terminal und Admin
- `tools/pi-nfc-agent`: Python-Dienst für Raspberry Pi und ACR122U
- `Stempeluhr.Api.Tests`: xUnit-Tests der API

Kimai-Tokens und andere Secrets bleiben im Backend. Der Browser erhält nur die
für Bedienung und Anzeige benötigten Daten.

## Einrichtung und RuntimeSettings

Für die lokale Entwicklung:

```bash
cp Stempeluhr.Api/appsettings.Development.example.json \
  Stempeluhr.Api/appsettings.Development.json
```

Dort mindestens ein lokales Admin-Passwort und die Kimai-Basis-URL setzen.
Anschließend im Admin-Bereich konfigurieren:

- Kimai-URL und Admin-API-Token
- Mitarbeiter mit API-Token, optionaler PIN, Farbe und Bild
- optional eine NFC-Karten-ID pro Mitarbeiter
- Standard-Projekt, Standard-Aktivität und Pause-Aktivität

Die zur Laufzeit gespeicherten Einstellungen liegen standardmäßig in
`Stempeluhr.Api/data/settings.json`. `data/` ist nicht Teil des Repositories und
muss im Betrieb persistent eingebunden und gesichert werden. Secrets werden in
Admin-Antworten nicht zurückgegeben.

Der NFC-Agent benötigt zusätzlich einen Reader-Token. Die Agent-Einstellung
`reader_token` muss dem API-Konfigurationswert
`Stempeluhr__NfcReaderToken` entsprechen.

## Lokal entwickeln

Backend (`http://localhost:5100`):

```bash
dotnet run --project Stempeluhr.Api/Stempeluhr.Api.csproj
```

Frontend mit API-Proxy (`http://localhost:4500`):

```bash
cd stempeluhr-client
npm ci
npm start
```

`npm start` verwendet Port `4500`. Ein direktes `npx ng serve` verwendet Port
`4200`. Beide Varianten leiten `/api` über `proxy.conf.json` an die lokale API
weiter.

## NFC-Terminal

Die vollständige Raspberry-Pi-, Chromium-, ACR122U- und systemd-Einrichtung
steht in [`docs/raspberry-pi-kiosk-nfc.md`](docs/raspberry-pi-kiosk-nfc.md). Die
aktuelle Agent-Konfiguration zeigt
[`tools/pi-nfc-agent/config.example.json`](tools/pi-nfc-agent/config.example.json).

Für die Verbindung müssen folgende Werte zusammenpassen:

- Chromium öffnet `https://<host>/terminal?terminalId=<id>`.
- Der Agent verwendet als `api_base_url` nur `https://<host>`.
- `terminal_id` des Agenten entspricht `terminalId` in der URL.
- `reader_token` entspricht `Stempeluhr__NfcReaderToken` der API.

### Identifikationsablauf

Der Agent stellt einen Scan ausschließlich über seinen lokalen Loopback-Server
(`127.0.0.1:8737`) bereit. Die Terminal-Web-App liest und bestätigt den Scan und
identifiziert den Mitarbeiter. **Der Scan selbst bucht keine Zeit.** Erst die
anschließend gewählte Aktion verwendet den Clock-/Kiosk-Endpunkt.

Bekannte Karten kann der Browser aus seinem lokalen Cache auch ohne API-Verbindung
identifizieren. Unbekannte Karten werden online über die API aufgelöst und für
die spätere Offline-Nutzung gespeichert.

Ohne Bestätigung verwirft der Agent den Scan standardmäßig
(`fallback_mode: "none"`). Der Kompatibilitätsmodus `toggle` schreibt nach dem
Timeout stattdessen ein Toggle-Ereignis in die persistente Agent-Queue. Dieser
Fallback ist vom normalen Scan-und-Bestätigungsablauf getrennt.

## Online- und Offline-Verhalten

Live-Aktionen und Offline-Replay sind getrennte Pfade:

- Der Browser speichert fehlgeschlagene explizite Aktionen mit Zeitstempel und
  Identität lokal und überträgt sie nach Wiederherstellung der Verbindung.
- Die API puffert transiente Kimai-Fehler in einer serverseitigen Outbox.
- Replay bleibt geordnet und idempotent. Dauerhafte fachliche Ablehnungen werden
  am Kiosk sichtbar, statt still verloren zu gehen.
- Lokal bekannte Karten und früher online bestätigte PINs können offline
  identifizieren. Die Aktion wird beim Nachtrag weiterhin serverseitig geprüft.
- Lokale Zustände sind als „zuletzt gesehen“, „offline vorgemerkt“ oder
  „unbekannt“ gekennzeichnet. Bei unbekanntem Status bietet die UI beide
  zulässigen Richtungen an.
- Der Queue-Hinweis bleibt sichtbar, solange Aktionen warten. Eine erreichbare
  API bedeutet nicht automatisch, dass Kimai den Nachtrag angenommen hat.

## Build und Tests

```bash
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"

dotnet build Stempeluhr.Api/Stempeluhr.Api.csproj -v q
dotnet test Stempeluhr.Api.Tests/Stempeluhr.Api.Tests.csproj \
  -v minimal --logger "console;verbosity=normal"

cd stempeluhr-client
npm ci
npx ng build --configuration production
npx ng test --watch=false

cd ..
python3 tools/pi-nfc-agent/test_offline_queue.py
bash tools/testenv/run_e2e_test.sh
```

`Stempeluhr.slnx` enthält das Testprojekt nicht; API-Tests deshalb direkt über
das Test-`csproj` starten.

## Docker und Betrieb

Lokales Image mit persistentem Datenverzeichnis starten:

```bash
docker build -t stempeluhr:local .
docker run --rm -p 8080:8080 \
  -v stempeluhr-data:/app/data \
  -e Admin__Password=change-me \
  stempeluhr:local
```

Für ein Kunden-Deployment:

1. `data/` einschließlich `settings.json` sichern.
2. Einen festen GHCR-Versionstag verwenden, nicht nur `latest`.
3. Container-Image aktualisieren und den Container neu erstellen.
4. Falls nötig den Pi-Agent mit
   `tools/deploy/pi-deploy.sh agent vX.Y.Z` aktualisieren; seine vorhandene
   Konfiguration bleibt erhalten.
5. Bei einer veralteten Kiosk-App den Chromium-Cache mit
   `tools/deploy/pi-deploy.sh kiosk` zurücksetzen.
6. PIN/NFC, Kommen/Pause/Gehen, Stundenanzeige und gegebenenfalls Offline-Replay
   prüfen.

Die Voraussetzungen und Hostliste für Agent-Update und Cache-Recovery sind in
[`tools/deploy/pi-deploy.sh`](tools/deploy/pi-deploy.sh) und
[`tools/deploy/pis.conf.example`](tools/deploy/pis.conf.example) dokumentiert.
Bei einem NAS hinter Cloudflare/Cloudflared zeigt die Portfreigabe intern zum
Container, beispielsweise `8002:8080`; Browser und Agent verwenden weiterhin
die externe HTTPS-Adresse.

## Release

Maßgeblich ist der manuell auf `main` gestartete Workflow
[`.github/workflows/release.yml`](.github/workflows/release.yml) mit dem Namen
**Release**. Er bestimmt den SemVer-Bump aus Conventional Commits oder aus der
Eingabe, erstellt Tag, GitHub Release und Pi-Agent-ZIP und veröffentlicht das
GHCR-Image mit den Tags `X.Y.Z`, `X.Y` und `latest`.

Expliziter Patch-Release:

```bash
gh workflow run release.yml --ref main -f bump=patch
```

Für die automatische Ermittlung des Bumps `-f bump=...` weglassen. Nach dem
Lauf Release, Pi-Agent-Asset und alle erwarteten Image-Tags prüfen. Das
Kunden-Deployment erfolgt separat und verwendet einen festen Versionstag.

## Sicherheit

- `settings.json`, lokale `appsettings.*`-Dateien, Admin-Passwort, Kimai-Tokens
  und Reader-Token nicht committen; Produktionszugriffe nur über HTTPS führen.
- Das persistente `data/`-Verzeichnis schützen und sichern.
- Die Offline-Queue und Identifikations-Caches liegen im `localStorage` und
  können PIN- oder Kartendaten enthalten. Kiosk-Hardware und Browser-Profil
  deshalb physisch und administrativ schützen und nicht für andere Websites
  verwenden.
- Für Admin und NFC-Reader eigene, starke Secrets verwenden und den Reader-Token
  nur auf API und Agent bereitstellen.
