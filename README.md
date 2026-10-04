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
   bucht in Kimai (Pause = Wechsel auf die Pause-Aktivität, Tätigkeitswechsel
   = Wechsel auf Projekt/Aktivität einer weiteren Tätigkeit, siehe unten).
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
- Authentifizierte Terminals laden beim Öffnen und jede Minute einen vollständigen
  Mitarbeiter-Katalog über den lokalen Agenten. Nach dem ersten erfolgreichen
  Abruf können sich alle aktiven, mit Kimai-Token eingerichteten Mitarbeiter
  offline per PIN und Karte anmelden – auch ohne frühere Anmeldung an diesem Pi.
  Der Katalog überlebt einen Browser-Neustart; ein vollständig gelöschtes
  Browser-Profil muss App und Katalog zunächst wieder online laden.
- Nach dem ersten erfolgreichen authentifizierten Katalogabruf speichert das
  Terminal keine PINs mehr in der Queue. Nachträge laufen dann über den lokalen
  Agenten mit Terminal-Token. Bis dahin bleiben alte Pis beim bisherigen
  PIN-/Karten-Nachtrag und behalten dessen gespeicherte Zugangsdaten.
  Auf `/clock` ohne Terminal-Agent bleibt eine PIN
  nur im Arbeitsspeicher: Nach einem Browser-Neustart warten PIN-basierte
  Nachträge auf eine erneute Online-Anmeldung des jeweiligen Mitarbeiters.
  Das hält die Nachträge anderer Mitarbeiter nicht auf; die Reihenfolge pro
  Mitarbeiter bleibt erhalten. Live-Stempel warten auf dessen Nachträge und
  anschließend auf einen frisch geladenen Status.
- Den Status zeigt die UI offline als „zuletzt gesehen“, „offline vorgemerkt“
  oder „unbekannt“ an; bei unbekanntem Status sind beide Richtungen wählbar.
- Der Hinweis „n Stempel warten auf Übertragung“ bleibt stehen, solange die
  Queue nicht leer ist – auch wenn die API wieder antwortet, Kimai aber nicht.
- Stempel, die der Server beim Nachtrag endgültig ablehnt (z. B. Mitarbeiter
  inzwischen deaktiviert), zeigt das Terminal im Ruhezustand an, bis jemand sie in
  Kimai nachgetragen und „Alle erledigt“ gedrückt hat.
  Abgelehnte Nachträge eingerichteter Mitarbeiter werden zusätzlich ohne PIN
  und Karten-ID in `data/rejected-offline-events.json` erfasst; Nachträge ohne
  oder mit unbekannter Mitarbeiter-ID lehnt die API nur ab. Im Adminbereich unter
  „Abgelehnte Offline-Stempel“ sind sie auch von einem anderen Rechner aus
  einsehbar und können nach dem manuellen Nachtrag markiert werden. „Alle
  erledigt“ am Kiosk quittiert nur den lokalen Hinweis und setzt die
  Admin-Markierung nicht. Offene Einträge bleiben erhalten; von den
  nachgetragenen Einträgen werden die letzten 1000 aufbewahrt. Ist die
  Journaldatei beschädigt, legt die API sie als `.corrupt` zur Prüfung beiseite
  und beginnt eine neue Historie.
- Verbietet Kimais Erfassungsmodus oder die Berechtigung des Mitarbeiter-Tokens
  nachgetragene Zeitfelder, wird der Offline-Stempel abgelehnt. Bei einem
  abgelehnten Stop, Pausenbeginn oder Tätigkeitswechsel bleibt der laufende
  Kimai-Eintrag unverändert und der Mitarbeiter eingestempelt. Den tatsächlichen
  Zeitpunkt in Kimai manuell korrigieren und den Fall im Admin markieren;
  es wird keine Ersatzbuchung zur aktuellen Uhrzeit angelegt. Details stehen
  in der [Terminal-Diagnose](docs/terminal-diagnostics.md).

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
eingebunden und gesichert werden. `Stempeluhr__DataPath` verlegt den Ordner
(Standard: `data/` im ContentRoot, im Container `/app/data`).

### Admin-Bereich

- Kimai-URL und Admin-API-Token
- Mitarbeiter mit Kimai-API-Token, PIN, Farbe, Bild und optional NFC-Karte.
  Eine neue Karte am Terminal auflegen; im Admin-Bereich erscheint sie unter
  „Letzte Karten-ID“ und lässt sich per „Letzte NFC-Karte zuweisen“ übernehmen.
- Standard-Projekt, Standard-Aktivität und Pause-Aktivität
- Pro Mitarbeiter optional **weitere Tätigkeiten** (Bezeichnung, Projekt,
  Aktivität, abrechenbar), z. B. Arbeit für andere Kunden, und eine
  Bezeichnung der Haupttätigkeit für den Kiosk (z. B. „Büro“; leer =
  „Standard-Tätigkeit“)

Secrets werden in Admin-Antworten nie zurückgegeben.

### Weitere Tätigkeiten (Tätigkeitswechsel)

Wer weitere Tätigkeiten hat, wählt sie schon beim Einstempeln: Statt eines
einzelnen Knopfes zeigt der Kiosk „Einstempeln auf“ mit Haupttätigkeit und
weiteren Tätigkeiten (ein Tipp bucht; bei unbekanntem Offline-Status öffnet
„Einstempeln“ diese Auswahl). Eingestempelt gibt es zusätzlich „Tätigkeit
wechseln“. Der Wechsel stempelt nicht aus: Das laufende Timesheet
endet und ein neues beginnt im selben Moment auf Projekt/Aktivität der
gewählten Tätigkeit (wie bei der Pause). Die Arbeitszeit bleibt lückenlos, in
Kimai lässt sich die Zeit pro Kunde auswerten, und die Stundenübersicht zählt
alles außer Pause. Nach einer Pause läuft die Tätigkeit von vor der Pause
weiter. Offline gewählte Tätigkeiten werden wie alle Stempel nachgetragen;
ist die Tätigkeit eines offline gestempelten Einstempelns inzwischen
gelöscht, bucht der Nachtrag auf die Haupttätigkeit, damit keine Arbeitszeit
verloren geht, und vermerkt das in der Beschreibung des Timesheets. Läuft
beim Nachtrag schon eine andere Tätigkeit, bleibt das Einstempeln wie immer
ein No-op; die verworfene Wahl steht dann im API-Log. Lässt
sich ein Wechsel oder ein Pausenende nicht eindeutig nachtragen (z. B. weil
kurz danach an einem anderen Terminal ausgestempelt wurde), meldet der Kiosk
ihn als abgelehnt, statt eine Buchung zu raten. Kam ein Pausenende oder
Wechsel live nur halb in Kimai an (Stopp gebucht, Start gescheitert oder
Kiosk-Timeout), sieht das in Kimai genauso aus. Der Kiosk schickt deshalb
schon mit dem Live-Stempel die Event-ID mit, unter der er ihn im Fehlerfall
einreiht; die API merkt sich den gestoppten Eintrag unter dieser ID, und der
Nachtrag setzt genau dieses Ereignis fort. Läuft der Live-Stempel noch (der
Kiosk gibt nach 8 s auf, ein Kimai-Aufruf darf 15 s dauern), wartet der
Nachtrag dieses Ereignisses in der Outbox auf dessen Ergebnis, statt einen
halben Stand zu lesen. Nach einem Neustart der API
dazwischen (der Merker liegt nur im Speicher) wird der Stempel wie bisher
abgelehnt.

Die Dauer im Statusfeld zählt nur den laufenden Abschnitt (z. B. „Kunde X
seit 10:15“) und beginnt nach Wechsel oder Pausenende neu; die Tagessumme
steht in der Stundenkarte.

Voraussetzungen in Kimai: Kunde und Projekt (ggf. eigene Aktivität) anlegen
und dem Kimai-Benutzer des Mitarbeiters Zugriff darauf geben (Team). Ohne
Zugriff (ebenso bei archiviertem Projekt) lehnt Kimai den Start auf der neuen
Tätigkeit ab – dann ist das bisherige Timesheet schon beendet. Live startet
die Stempeluhr deshalb gleich wieder auf der bisherigen Tätigkeit (bei einer
fremden Buchung auf der Haupttätigkeit) und meldet am Kiosk mit zwei Pieptönen
z. B. „Kunde X nicht moeglich - weiter auf Büro“; erst wenn auch das
scheitert, bleibt es bei „Kimai konnte nicht speichern“. Einen offline
gestempelten Wechsel, den Kimai so ablehnt, meldet der Kiosk dagegen als
abgelehnt; die Zeit ab dem Wechsel muss dann in Kimai nachgetragen werden.
Eine Ausgleichsbuchung beim Nachtrag sähe niemand, die Zeit stünde unbemerkt
auf der falschen Tätigkeit, und spätere Stempel aus der Queue liefen gegen
einen geratenen Stand. Jede
Tätigkeit braucht ein eigenes Paar aus Projekt und Aktivität, das sich von der
Standard-Tätigkeit und der Pause unterscheidet – daran erkennt die Stempeluhr,
welche Tätigkeit gerade läuft. Im Admin stehen dafür nur Aktivitäten zur Wahl,
die Kimai für das gewählte Projekt annimmt (globale und die des Projekts).
Läuft eine Buchung, die zu keiner Tätigkeit passt (z. B. nach dem Löschen
einer Tätigkeit), bleibt der Wechsel zu jeder Tätigkeit möglich.

### Telegram-Benachrichtigung (optional)

Bei jedem echten Live-Stempel (nicht bei No-ops oder erfolgreich übernommenen
Offline-Nachträgen) schickt die API eine Nachricht wie
`🟢 Anna Mustermann · eingestempelt um 08:12` in eine Telegram-Gruppe
(Tätigkeitswechsel: `🔄 Anna Mustermann · wechselt zu Kunde X um 10:15`;
mit weiteren Tätigkeiten nennt auch das Einstempeln die Tätigkeit:
`🟢 Anna Mustermann · eingestempelt auf Kunde X um 08:12`).
Endgültig abgelehnte Offline-Nachträge, deren Mitarbeiter erfolgreich
authentifiziert wurde, melden sich zusätzlich als Warnung mit Aktion,
Zeitpunkt in der Kimai-Zeitzone des Mitarbeiters (falls nicht abrufbar: UTC),
Grund und Bitte zum
manuellen Nachtrag in Kimai. Mehrere Ablehnungen einer Verarbeitungsrunde
werden zusammengefasst. Höchstens eine Warnung pro Minute und 20 pro UTC-Tag
gehen an Telegram; weitere Fälle werden im nächsten erlaubten Zeitfenster
gebündelt. Ausstehende Meldungen bleiben bei einem Neustart im Journal und
werden danach erneut versucht. Fehlgeschlagene Authentifizierung, unbekannte
oder deaktivierte Mitarbeiter und wiederholte Event-IDs erzeugen keinen Push,
ebenso Ablehnungen, die bei ausgeschaltetem Telegram entstanden sind. Im
Journal als erledigt markierte Fälle werden nicht mehr gemeldet. Alle
Ablehnungen bleiben unabhängig davon im Admin-Journal sichtbar.

Außerdem warnt die API, wenn jemand **mehr als 6 Stunden am Stück ohne
Pause** oder **mehr als 10 Stunden in einer Schicht** arbeitet, z. B.
`⚠️ Anna Mustermann · über 6 Std. ohne Pause (ab 07:58, 6:05 Std.)` oder
`⚠️ Anna Mustermann · über 10 Std. in der Schicht seit 29.09. 22:00 (10:02 Std.)`.
Ein Hintergrunddienst prüft dazu alle 5 Minuten die Kimai-Timesheets der
letzten 48 Stunden, die Warnung kommt also schon, während noch gestempelt ist.
Offline-Nachträge und Stempel anderer Terminals zählen genauso.
Gezählt wird nur Arbeit, keine Pause-Aktivität. Eine Pause oder Ausstempel-Lücke
unterbricht „am Stück“ erst ab 15 Minuten, ein Tätigkeitswechsel nie. Eine
Schicht endet erst nach 8 Stunden ohne Arbeit, unabhängig von Mitternacht:
Nachtschichten und geteilte Dienste zählen als Ganzes. Jede Überschreitung
meldet sich einmal pro Block bzw. Schicht, auch wenn davor später noch Zeit
nachgetragen oder der Beginn in Kimai korrigiert wird. Teilt eine nachträglich
eingetragene Pause einen schon gemeldeten Block, meldet sich der neue Block
eigenständig, sobald er selbst die Grenze erreicht;
`data/work-time-alerts.json` merkt sich gesendete Warnungen auch über einen
Neustart. Scheitert der Versand,
folgt der nächste Versuch bei der nächsten Prüfung. Fälle, die schon länger als
24 Stunden vorbei sind, werden nicht mehr gemeldet. Ohne Telegram-Konfiguration
fragt der Dienst Kimai gar nicht erst ab.

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

**Terminal-Überwachung:** Meldet sich ein Terminal nicht mehr, hängt die
Kiosk-Seite oder sind Werte wie Temperatur oder Speicher kritisch, kommt eine
Nachricht, z. B. `🔴 Meldet sich nicht (letzter Bericht 12:34)`, und nach dem
Ende der Störung eine Entwarnung. Regeln und Grenzwerte:
[Terminal-Diagnose](docs/terminal-diagnostics.md#überwachung). Technische
Warnungen können in einen eigenen Chat gehen; ohne `telegramAlertChatId`
landen sie in `telegramChatId`:

```json
{ "telegramAlertChatId": "-1009876543210" }
```

### Terminal-Metriken in Grafana (optional)

Die API schreibt jeden Terminal-Bericht (etwa einmal pro Minute: Temperatur,
freier RAM, CPU, Load, Chromium-Speicher, Speicherplatz, Queue, Drosselung,
Laufzeit, Kiosk-Lebenszeichen) in eine InfluxDB. Unterstützt wird InfluxDB 2.x
und InfluxDB 3 über deren `/api/v2/write`. Die Pis brauchen dafür nichts.

1. In InfluxDB einen Bucket anlegen (z. B. `stempeluhr`, Aufbewahrung etwa
   90 Tage) und einen API-Token mit Schreibrecht auf diesen Bucket.
2. In `data/settings.json` ergänzen. `influxOrg` darf bei InfluxDB 3 leer
   bleiben. Aus dem Container ist Influx über die NAS-IP oder ein gemeinsames
   Docker-Netz erreichbar, nicht über `localhost`.

   ```json
   {
     "influxUrl": "http://192.168.1.10:8086",
     "influxOrg": "home",
     "influxBucket": "stempeluhr",
     "influxToken": "<TOKEN>"
   }
   ```

3. In Grafana eine InfluxDB-Datenquelle mit Abfragesprache **Flux** anlegen
   (Token mit Leserecht) und
   [`tools/grafana/terminal-dashboard.json`](tools/grafana/terminal-dashboard.json)
   importieren (Dashboards → New → Import). Dabei Datenquelle und Bucket wählen.

Die Einstellungen werden ohne Neustart wirksam. Ist Influx nicht erreichbar,
gehen die Werte dieser Zeit verloren. Das Log meldet den Ausfall einmal und die
Wiederkehr einmal. Stempeln und Überwachung beeinflusst das nie.

### Terminal (Raspberry Pi)

Zuerst ein eigenes Terminal-Token in `data/settings.json` registrieren und
als geschützte Datei auf den Pi übertragen (siehe Pi-Anleitung). Danach:

```bash
curl -fsSL https://stempeluhr.example.com/pi/install.sh | sudo bash -s -- \
  --server https://stempeluhr.example.com --terminal-id stempeluhr-pi-02 --kiosk-user kiosk \
  --terminal-token-file /root/stempeluhr-terminal.token
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
- Authentifizierte Terminals speichern keine PINs in der Offline-Queue. Erst
  ein erfolgreicher Katalogabruf bestätigt die Unterstützung des Agenten und
  migriert bestehende Terminal-Queues ohne Änderung von Event-IDs/Zeitstempeln.
  Bis dahin bleibt für alte Pis der Legacy-Modus mit gespeicherten PINs aktiv.
  Nach bestätigter Umstellung gibt es bei Token-Entzug keinen Legacy-Fallback.
- Terminal-Tokens stehen ausschließlich in `settings.json` auf dem Server und
  `config.json` beim Agenten (`root:stempeluhr`, Modus `640`). Der Browser erhält
  sie nie. `/api/kiosk/catalog` und Terminal-Nachträge benötigen Bearer-Token
  und passende `X-Terminal-Id`; ein ungültiges Token wird nicht durch PIN-Auth
  ersetzt. Entfernen/Ersetzen unter `terminalTokens` wirkt ohne API-Neustart.
- Der Agent erlaubt seine privilegierten `/terminal/*`-Routen nur der
  konfigurierten Kiosk-Origin, auch Anfragen ohne Origin werden abgewiesen.
  Er leitet ausschließlich Katalog und Nachträge weiter, keine beliebigen URLs
  und keine HTTP-Redirects. Live-Stempeln per PIN/Karte bleibt unverändert.
- Karten-IDs und gesalzene PIN-Prüfwerte liegen im Browser. Kurze PINs bleiben
  trotz SHA-256 offline durchprobierbar. Hardware und Browser-Profil schützen.
  Ein kompromittiertes Terminal oder XSS auf der erlaubten Kiosk-Origin kann
  Nachträge für aktive Mitarbeiter auslösen; das Terminal ist eine Vertrauensgrenze.
- Ein Terminal bestätigt beim Nachtrag die Identität. Geänderte PINs/Karten
  werden mit dem nächsten Katalog ersetzt; während eines Ausfalls kann ein
  alter Katalog weiterhin identifizieren. Die API prüft beim Nachtrag erneut,
  ob der Mitarbeiter aktiv ist. Ein Token-Entzug blockiert neue Sync-Anfragen;
  schon angenommene Server-Outbox-Einträge werden weiterhin abgearbeitet.
- `/pi/` liefert öffentlichen Repo-Code aus; die Integrität sichern HTTPS und
  die SHA-256 in `agent.json`.
- Fehlversuchs-Sperre für PINs (im Speicher der API): Nach 5 falschen PINs oder
  Karten in Folge für einen Mitarbeiter sperrt die API ihn für 1, dann 5, dann
  15 Minuten. Das gilt für Live-Stempeln, `/api/clock/*` und den PIN-Nachtrag.
  Eine erfolgreiche Anmeldung setzt diesen Zähler zurück, nach einer Stunde
  ohne Fehlversuch beginnt er neu. PIN-Login und Stundenübersicht ohne
  Mitarbeiter-ID teilen ein globales Budget von 10 Fehlversuchen in
  15 Minuten. Ein erfolgreicher Login setzt es nicht zurück, sonst könnte sich
  jeder mit eigener PIN neue Versuche verschaffen. Ist das Budget aufgebraucht,
  bleibt der PIN-Login nur so lange gesperrt, bis der älteste Fehlversuch aus
  dem Fenster fällt. Während einer Sperre prüft die API die PIN gar nicht erst
  und antwortet mit `429` und `Retry-After`. Der Kiosk reiht Live-Stempel dann
  offline ein, und Nachträge des gesperrten Mitarbeiters bleiben `buffered`,
  bis die Sperre abläuft. Nachträge anderer Mitarbeiter laufen weiter.
  Nachträge über Terminal-Token sind nicht betroffen. Ein API-Neustart hebt
  alle Sperren auf.

## Bekannte Grenzen und offene Punkte

Terminal-Hänger und Kimai-Ablehnungen: Vorgehen, technische Messwerte und
Logexport stehen in [Terminal-Diagnose](docs/terminal-diagnostics.md).

- Für PIN-freie Terminal-Nachträge müssen bestehende Pis einmalig ein
  registriertes Terminal-Token erhalten. Alte Agent-Konfigurationen und alte
  Agent-Versionen bleiben bis zum ersten erfolgreichen Katalogabruf beim
  Legacy-Nachtrag; dessen PINs liegen weiterhin im Browserprofil. Nach der
  Umstellung bleiben bei Token-Entzug wartende Stempel bis zur Behebung erhalten.
- Ohne Terminal-Agent kennt `/clock` offline nur zuvor lokal angemeldete
  Mitarbeiter. PIN-basierte Queues benötigen nach einem Neustart eine erneute
  Online-Anmeldung; sie bleiben bis dahin erhalten.
- Nutzt ein Mitarbeiter während eines Ausfalls mehrere Terminals, kann die
  Reihenfolge beim Nachtrag nach Eingang statt nach Zeit gemischt werden.
- Wer eine Mitarbeiter-ID kennt, kann diesen Mitarbeiter mit falschen PINs
  gezielt sperren. Seine Stempel gehen dabei nicht verloren, werden aber erst
  nach Ablauf der Sperre gebucht.
- Das globale PIN-Login-Budget lässt sich von jedem Gerät im Netz mit 10 falschen
  PINs pro 15 Minuten dauerhaft ausschöpfen. Dann sind PIN-Login und
  Stundenübersicht für alle gesperrt. Der Kiosk meldet Mitarbeiter mit
  gemerkter PIN offline an und reiht ihre Stempel ein. Wer keine gemerkte PIN
  hat (neuer Mitarbeiter, geänderte PIN, anderer Browser), kommt nur per Karte
  herein.
