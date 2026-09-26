# Stempeluhr für Kimai

Touchfreundliche Web-Stempeluhr für eine gehostete Kimai-Instanz. Mitarbeiter
melden sich per PIN oder NFC-Karte an und wählen Kommen, Gehen, Pausenbeginn
oder Pausenende. Die Zeiten landen direkt in Kimai – auch wenn das Terminal
zwischendurch offline war.

- Mitarbeiteransicht: `/clock`
- Terminal (Raspberry Pi mit Kartenleser): `/terminal?terminalId=<id>`
- Administration: `Admin` in der Web-App

## Architektur

```text
 Raspberry Pi (Terminal)                        NAS / Docker                  Cloud
┌──────────────────────────────────┐        ┌──────────────────────┐     ┌─────────┐
│ Chromium-Kiosk  /terminal        │ HTTPS  │ Stempeluhr-Container │     │         │
│  Angular-App + Service Worker    ├───────►│  .NET 10 API         ├────►│  Kimai  │
│  Offline-Queue (localStorage)    │        │  + gebauter Client   │     │         │
│        ▲ 127.0.0.1:8737          │        │  + /pi/ Agent-Bundle │     └─────────┘
│ NFC-Agent (Python) ◄── ACR122U   │◄───────┤  (Auto-Update)       │
└──────────────────────────────────┘        └──────────────────────┘
```

| Verzeichnis | Inhalt |
| --- | --- |
| `Stempeluhr.Api` | .NET 10 Minimal API: Kimai-Proxy, Offline-Nachtrag, Auslieferung von Client und Pi-Bundle |
| `stempeluhr-client` | Angular-Client für `/clock`, `/terminal` und Admin |
| `tools/pi-nfc-agent` | NFC-Agent, Installer und Updater für die Pis |
| `tools/deploy` | Wartung der Pis per SSH (Umstellung, Status, Cache-Reset) |
| `tools/testenv` | Fake-Kimai, E2E-Test, Agent-Simulation, Updater-Test |
| `Stempeluhr.Api.Tests` | xUnit-Tests der API |

Kimai-Tokens und andere Secrets bleiben im Backend (`data/settings.json`); der
Browser erhält nur, was er zur Bedienung braucht.

## Ablauf eines Stempels

1. **Identifizieren:** PIN-Eingabe (`/api/kiosk/pin-login`) oder Karte. Der
   Agent liest die UID und stellt sie nur lokal bereit (`GET /scan/latest`);
   die Kiosk-App bestätigt den Scan (`POST /scan/ack`) und löst die Karte über
   ihren lokalen Cache bzw. online über `/api/kiosk/identify` auf.
   **Ein Scan bucht nie.**
2. **Stempeln:** Der gewählte Knopf ruft `/api/kiosk/clock` auf; die API
   bucht in Kimai (Pause = Wechsel auf die Pause-Aktivität).
3. **Offline:** Scheitert der Aufruf (kein Netz, Timeout nach 8 s, Server-
   oder Kimai-Fehler) oder ist der Ausfall schon bekannt, landet die Aktion mit
   echtem Zeitstempel in der Offline-Queue des Browsers.

## Offline-Verhalten

Drei Ebenen sorgen dafür, dass Stempel bei Ausfällen nicht verloren gehen:

| Ebene | Was passiert |
| --- | --- |
| **Service Worker** | Die App liegt lokal im Browser. Ein Terminal, das ohne Netz neu startet (nächtlicher Reboot, Stromausfall), lädt sie trotzdem. `/api` wird nie gecacht. |
| **Browser-Queue** (Kiosk → API) | Aktionen werden in `localStorage` gespeichert und nach Wiederkehr der Verbindung über `/api/kiosk/clock/sync` nachgetragen – geordnet, in Paketen zu 100, idempotent über Event-IDs. |
| **Server-Outbox** (API → Kimai) | Ist nur Kimai weg, puffert die API den Nachtrag und spielt ihn per Hintergrunddienst nach. |

Weitere Regeln:

- Beim Nachtrag prüft die API jede Aktion gegen den aktuellen Kimai-Status
  (rückdatiert auf den Zeitstempel). Schon erledigte Aktionen werden zu No-ops
  („Lief bereits“, „Pause lief bereits“, …) – ein Stempel, der live übernommen
  wurde und trotzdem in der Queue landete, bucht also nicht doppelt.
- Offline anmelden können sich nur Mitarbeiter, die **an diesem Terminal**
  schon einmal online per PIN angemeldet waren (gesalzener Prüfwert) bzw.
  deren Karte hier schon einmal online erkannt wurde. Ein neues Terminal kennt
  offline also zunächst niemanden.
- Den Status zeigt die UI offline als „zuletzt gesehen“, „offline vorgemerkt“
  oder „unbekannt“ an; bei unbekanntem Status sind beide Richtungen wählbar.
- Der Hinweis „n Stempel warten auf Übertragung“ bleibt stehen, solange die
  Queue nicht leer ist – auch wenn die API wieder antwortet, Kimai aber nicht.
- Stempel, die der Server beim Nachtrag endgültig ablehnt (z. B. PIN
  inzwischen geändert), zeigt das Terminal im Ruhezustand an, bis jemand sie in
  Kimai nachgetragen und „Alle erledigt“ gedrückt hat.

## Einrichtung

### Server (Docker)

Beispiel für ein NAS, intern auf Port `8002`, extern per Cloudflare-Tunnel
unter `https://stempeluhr.example.com`:

```yaml
services:
  stempeluhr:
    image: ghcr.io/tomat3/stempeluhr:0.13.0   # festen Versionstag verwenden
    container_name: stempeluhr
    restart: unless-stopped
    volumes:
      - /volume1/docker/stempeluhr/data:/app/data
    ports:
      - 8002:8080
    environment:
      Admin__Password: "change-me"
      Kimai__BaseUrl: "https://kimai.example.com"
      # Nur nötig, wenn die App echte, unterscheidbare Client-IPs hinter einem
      # eigenen Reverse-Proxy sieht (Rate-Limit pro IP):
      # Stempeluhr__KnownProxies__0: "172.18.0.1"
```

`data/` enthält `settings.json` mit allen Secrets und muss persistent
eingebunden und gesichert werden.

### Admin-Bereich

- Kimai-URL und Admin-API-Token
- Mitarbeiter mit Kimai-API-Token, PIN, Farbe, Bild und optional NFC-Karte.
  Eine neue Karte am Terminal auflegen; im Admin-Bereich erscheint sie unter
  „Letzte Karten-ID“ und lässt sich per „Letzte NFC-Karte zuweisen“ übernehmen.
- Standard-Projekt, Standard-Aktivität und Pause-Aktivität

Secrets werden in Admin-Antworten nie zurückgegeben.

### Telegram-Benachrichtigung (optional)

Bei jedem echten Live-Stempel (nicht bei No-ops und nicht beim Offline-
Nachtrag) schickt die API eine Nachricht wie
`🟢 Anna Mustermann · eingestempelt um 08:12` in eine Telegram-Gruppe.

1. Bei @BotFather `/newbot` ausführen, Token kopieren.
2. Private Gruppe anlegen, Bot hinzufügen und zum Admin machen.
3. Eine Nachricht in die Gruppe schreiben, dann
   `https://api.telegram.org/bot<TOKEN>/getUpdates` öffnen:
   `result[0].message.chat.id` ist die Chat-ID (negativ).
4. In `data/settings.json` ergänzen (wirkt ohne Neustart):

   ```json
   { "telegramBotToken": "<TOKEN>", "telegramChatId": "-1001234567890" }
   ```

Sendefehler beeinflussen das Stempeln nie.

### Terminal (Raspberry Pi)

Ein neues Terminal wird mit einem Befehl eingerichtet:

```bash
curl -fsSL https://stempeluhr.example.com/pi/install.sh | sudo bash -s -- \
  --server https://stempeluhr.example.com --terminal-id stempeluhr-pi-02 --kiosk-user kiosk
```

Der Installer richtet Kartenleser-Zugriff, NFC-Agent, Auto-Update und den
Chromium-Kiosk ein. OS-Installation, Autologin und WLAN-Einstellungen stehen in
[`docs/raspberry-pi-kiosk-nfc.md`](docs/raspberry-pi-kiosk-nfc.md), Details zum
Agenten in [`tools/pi-nfc-agent/README.md`](tools/pi-nfc-agent/README.md).

## Update

1. `data/` sichern.
2. Image-Tag im Compose-File auf die neue Version setzen, Container neu
   erstellen.
3. Fertig – der Rest folgt automatisch:
   - **Kiosk-App:** Das Terminal erkennt die neue Server-Version, lädt sie im
     Ruhezustand in den Service Worker und startet neu.
   - **NFC-Agent:** Jeder Pi prüft alle 15 Minuten (und nach dem Boot)
     `/pi/agent.json`, installiert die passende Version, prüft sie über
     `127.0.0.1:8737/health` und rollt bei Fehlern zurück.
4. Kontrolle: `tools/deploy/pi-deploy.sh status` zeigt die Agent-Version aller
   Pis; am Terminal PIN/Karte, Kommen/Pause/Gehen und Stundenanzeige prüfen.

Pis, deren Agent noch von Hand kopiert wurde, einmalig mit
`tools/deploy/pi-deploy.sh bootstrap` umstellen (Hostliste in
`tools/deploy/pis.conf`, siehe `pis.conf.example`). Hängt ein Kiosk trotz allem
auf einer alten App, hilft `tools/deploy/pi-deploy.sh kiosk` (Cache-Reset und
Reboot).

## Entwicklung

```bash
cp Stempeluhr.Api/appsettings.Development.example.json \
   Stempeluhr.Api/appsettings.Development.json   # Admin-Passwort, Kimai-URL

dotnet run --project Stempeluhr.Api/Stempeluhr.Api.csproj   # http://localhost:5100

cd stempeluhr-client
npm ci
npm start                                                    # http://localhost:4500, /api -> 5100
```

Service Worker und Auto-Reload sind im Entwicklungs-Build (`0.0.0-local`)
abgeschaltet.

### Build und Tests

```bash
dotnet build Stempeluhr.Api/Stempeluhr.Api.csproj -v q
dotnet test Stempeluhr.Api.Tests/Stempeluhr.Api.Tests.csproj -v minimal

cd stempeluhr-client
npx ng build --configuration production
npx ng test --watch=false
cd ..

python3 tools/pi-nfc-agent/test_scan_handling.py
python3 tools/pi-nfc-agent/test_local_scan_server.py
bash tools/testenv/test_pi_update.sh     # Linux: Bundle + Updater
bash tools/testenv/run_e2e_test.sh       # Linux: Fake-Kimai + echte API
```

`Stempeluhr.slnx` enthält das Testprojekt nicht; API-Tests daher über das
Test-`csproj` starten. Alle Tests laufen auch in der CI
([`.github/workflows/ci.yml`](.github/workflows/ci.yml)).

## Release

Der Workflow **Release** ([`.github/workflows/release.yml`](.github/workflows/release.yml))
wird manuell auf `main` gestartet:

```bash
gh workflow run release.yml --ref main              # Bump aus Conventional Commits
gh workflow run release.yml --ref main -f bump=patch
```

Er erstellt Tag und GitHub Release und veröffentlicht das Image
`ghcr.io/tomat3/stempeluhr` mit den Tags `X.Y.Z`, `X.Y` und `latest`. Die
Version landet in API, Client und Pi-Bundle. Der Kunden-Deploy ist ein
separater Schritt (siehe [Update](#update)).

## Sicherheit

- `data/settings.json`, lokale `appsettings.*`-Dateien und alle Tokens nie
  committen; Produktion nur über HTTPS.
- Offline-Queue und Identifikations-Caches liegen im `localStorage` des Kiosks
  und können PINs bzw. Karten-IDs enthalten. Kiosk-Hardware und Browser-Profil
  physisch schützen und für nichts anderes verwenden.
- Der Kiosk authentifiziert sich nicht selbst; Nachträge sind nur durch PIN
  bzw. Karte des Mitarbeiters und ein Rate-Limit pro IP geschützt.
- `/pi/` liefert öffentlichen Repo-Code aus; die Integrität sichern HTTPS und
  die SHA-256 in `agent.json`.

## Bekannte Grenzen und offene Punkte

- Offline-Identifikation nur für Mitarbeiter, die an diesem Terminal schon
  online gesehen wurden. Abhilfe wäre ein vom Server gelieferter
  Mitarbeiter-Katalog für authentifizierte Terminals – zusammen mit einer
  Terminal-Authentifizierung statt PIN in der Queue (#7).
- Nutzt ein Mitarbeiter während eines Ausfalls mehrere Terminals, kann die
  Reihenfolge beim Nachtrag nach Eingang statt nach Zeit gemischt werden.
- Weitere offene Issues: PIN-Fehlversuch-Backoff (#8), Randfälle abgelehnter
  Stempel (#37), Telegram-Hinweis bei abgelehnten Stempeln (#36).
