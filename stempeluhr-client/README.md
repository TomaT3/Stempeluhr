# Stempeluhr Angular-Client

Angular-Frontend für `/clock`, `/terminal?terminalId=<id>` und den
Admin-Bereich. Im Container liefert `Stempeluhr.Api` den gebauten Client aus.
Produkt, Offline-Verhalten, Betrieb und Release: siehe [`../README.md`](../README.md).

```bash
npm ci
npm start                                  # http://localhost:4500, /api -> localhost:5100
npx ng build --configuration production    # dist/stempeluhr-client/browser
npx ng test --watch=false
```

Node.js-Version wie in `.github/workflows/ci.yml`. Der Service Worker
(`ngsw-config.json`) ist nur im Produktions-Build aktiv.

## App-Icon

Favicon, PWA-Icons, `apple-touch-icon.png` und `manifest.webmanifest` liegen in
`public/`. Die Bilder werden aus den SVG-Quellen in `icon-source/` erzeugt und
eingecheckt:

- `stempeluhr.svg`: Kachel mit runden Ecken (Favicon, `icons/icon-*.png`,
  Apple-Icon auf deckendem Blau)
- `stempeluhr-maskable.svg`: vollflächig, Motiv auf 80 % für Android
  (`icons/icon-maskable-512.png`). Das Motiv muss in beiden Dateien gleich
  bleiben.

Nach einer Änderung am Motiv neu erzeugen (braucht ein lokales Chrome oder
Edge, Pfad notfalls über `CHROME=...`):

```bash
node icon-source/generate-icons.mjs
```
