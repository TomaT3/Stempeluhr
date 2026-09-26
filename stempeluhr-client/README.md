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
