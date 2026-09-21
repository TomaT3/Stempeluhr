# Stempeluhr Angular-Client

Angular-Frontend für die Mitarbeiteransicht (`/clock`), das kompakte
NFC-Terminal (`/terminal?terminalId=<id>`) und den Admin-Bereich. Im
Produktionscontainer liefert `Stempeluhr.Api` den gebauten Client aus.

## Voraussetzungen

- Node.js 22 wie in `.github/workflows/ci.yml`
- npm in der unter `packageManager` in `package.json` angegebenen Version
- lokal laufende API auf `http://localhost:5100`

```bash
npm ci
```

## Entwicklungsserver

Das Projekt-Startskript verwendet Port `4500` und den lokalen API-Proxy:

```bash
npm start
```

Ein direkter Angular-CLI-Start verwendet den Standardport `4200`:

```bash
npx ng serve
```

Beide Varianten laden `proxy.conf.json` und leiten `/api` an die lokale
.NET-API weiter.

## Build und Tests

```bash
npx ng build --configuration production
npx ng test --watch=false
```

Der Produktionsbuild liegt unter `dist/stempeluhr-client/browser`. Der
Repository-`Dockerfile` kopiert ihn in das `wwwroot` der API.

## Weitere Dokumentation

Produkt, RuntimeSettings, Offline-Verhalten, Docker-Betrieb und Releases sind
im [`../README.md`](../README.md) beschrieben. Die Raspberry-Pi- und
NFC-Einrichtung steht in
[`../docs/raspberry-pi-kiosk-nfc.md`](../docs/raspberry-pi-kiosk-nfc.md).
