# Stempeluhr fuer Kimai

Touch-freundliche Stempeluhr fuer eine gehostete Kimai-Instanz.

## Aufbau

- `Stempeluhr.Api`: .NET Minimal API als sicherer Kimai-Proxy
- `stempeluhr-client`: Angular-App fuer die Mitarbeiteroberflaeche

Die Kimai-API-Tokens bleiben im Backend. Der Browser bekommt nur Namen, Farben und Statusdaten.

## Kimai konfigurieren

Die Kimai-Adresse, Admin-Tokens, Mitarbeiter-Tokens und Bilder werden nicht in Git gespeichert.
Die App schreibt diese Werte lokal in `Stempeluhr.Api/data/settings.json`; der Ordner `data/` ist ignoriert.

Lokal kannst du `Stempeluhr.Api/appsettings.Development.json` verwenden. Diese Datei ist ebenfalls ignoriert.
Als Vorlage gibt es `Stempeluhr.Api/appsettings.Development.example.json`.

Minimaler lokaler Start:

```json
{
  "Admin": {
    "Password": "admin"
  },
  "Kimai": {
    "BaseUrl": "https://kimai.example.invalid"
  }
}
```

Danach in der App oben `Admin` oeffnen:

- Kimai-URL und Admin-API-Token setzen
- Kimai-Mitarbeiter laden
- pro Mitarbeiter API-Token, PIN, Farbe und optional Bild pflegen
- optional pro Mitarbeiter NFC-Karten-ID fuer ein Raspberry-Pi-Terminal pflegen
- Standard-Projekt-ID, Standard-Aktivitaet-ID und Pause-Aktivitaet-ID setzen

`Pin` ist optional. Ohne PIN kann ein Mitarbeiter direkt ein- und ausstempeln.
Die Pause-Aktivitaet-ID verweist auf eine normale Kimai-Taetigkeit, die als Pause genutzt wird.
Aktive Timesheets mit dieser Taetigkeit werden in der Stempeluhr als `In Pause` angezeigt.

### Offline-Nachtrag von Pausenenden: Voraussetzungen und Toleranz

Die automatische Recovery eines unterbrochenen Offline-`pauseEnd` (das
Pausen-Timesheet wurde bereits gestoppt, der Wiedereinstieg in die Arbeit
schlug transient fehl) setzt eine konfigurierte **Pause-Aktivitaet-ID
voraus**: Nur mit ihr kann die API ein gestopptes Timesheet ueberhaupt als
Pause erkennen. Ohne diese Einstellung bleibt ein solcher Fall bewusst ein
No-op mit lauter Warnung im Log - die Arbeit muss dann manuell nachgetragen
werden.

Die Erkennung nutzt eine Toleranz (`PauseEndRecoveryToleranceSeconds` in
der `settings.json`, Standard 30 s): Das Ende des letzten gestoppten
Pausen-Timesheets darf nur um diesen Betrag vom Zeitstempel des
nachgetragenen Events abweichen. Trade-off dabei:

- Kleiner Wert = kleines Phantom-Fenster (ein echter Live-Stopp innerhalb
  des Fensters wuerde sonst faelschlich als unterbrochene Transaktion
  mitgebucht), braucht aber synchronisierte Uhren.
- Groesserer Wert = robust gegen Uhrenabweichung zwischen Client
  (Raspberry Pi, Kiosk-Browser) und Kimai-Server, oeffnet aber eben dieses
  Fenster.

Clients sollten daher per NTP synchronisiert sein (der Raspberry Pi tut das
standardmaessig ueber systemd-timesyncd); laeuft ein Client ohne
Zeitsynchronisation, den Wert in der Admin-Umgebung entsprechend erhoehen.

### Bekannte Grenze: Reihenfolge beim Live-Apply nach einer Stoerung

Die "eine Timeline"-Garantie gilt fuer die **Outbox**: Sobald Events in der
Server-Outbox warten, werden alle Backlogs (NFC- und Kiosk-Queue zusammen)
strikt in Event-Zeitordnung abgespielt. Im **Live-Pfad** dagegen (Outbox leer,
Kimai erreichbar - der Normalfall direkt nach einer Störungserholung, weil
beide Clients ihre Events clientseitig queuen und danach selbst synchronisieren)
werden zwei unabhaengige Sync-Requests in **Ankunftsreihenfolge** angewendet,
nicht in Event-Zeitordnung. Innerhalb eines Requests bleibt die Ordnung stets
erhalten; es kann aber passieren, dass z. B. ein NFC-Toggle@09:00 vor einem
Kiosk-pauseStart@08:00 ankommt und dann gegen einen anderen Zustand abgeleitet
wird. Praktische Gegenmassnahme: Waehrend einer Stoerung pro Mitarbeiter bei
einem Terminal bleiben, bis der Nachtrag durch ist. Ein kurzes serverseitiges
Merge-Fenster ueberlappender Requests ist als moeglicher Ausbau notiert.

## Raspberry Pi NFC-Terminal

Fuer ein Terminal mit Raspberry Pi 5, Touchdisplay und ACR122U gibt es einen
separaten Agenten unter `tools/pi-nfc-agent`. Die Einrichtung ist in
`docs/raspberry-pi-kiosk-nfc.md` beschrieben.
Das Touchdisplay verwendet die kompakte Route `/terminal?terminalId=<id>`;
die normale Mitarbeiteroberflaeche bleibt unter `/clock`.

## Starten

Backend:

```powershell
dotnet run --project .\Stempeluhr.Api\Stempeluhr.Api.csproj
```

Frontend:

```powershell
cd .\stempeluhr-client
npm install
npm start
```

Danach ist die App unter `http://localhost:4200` erreichbar. Das Backend laeuft auf `http://localhost:5100`.
Der Angular-Dev-Server leitet `/api` ueber `stempeluhr-client/proxy.conf.json` lokal an das Backend weiter. Das gilt auch fuer direktes `ng serve`, weil der Proxy in `angular.json` eingetragen ist.

## Docker

Image lokal bauen:

```powershell
docker build -t stempeluhr:local .
```

Container starten:

```powershell
docker run --rm -p 8080:8080 -v stempeluhr-data:/app/data -e Admin__Password=admin stempeluhr:local
```

Die App ist dann unter `http://localhost:8080` erreichbar.
Im Container werden Frontend und Backend vom selben .NET-Prozess ausgeliefert. Dadurch funktionieren API-Aufrufe relativ ueber `/api`, auch wenn der Container spaeter ueber Cloudflared unter einer externen Domain erreichbar ist.

### Docker Compose auf NAS / Docker Desktop

Beispiel fuer ein NAS, das intern auf Host-Port `8002` laeuft und extern per
Cloudflare/Cloudflared unter `https://stempeluhr.example.local` erreichbar ist:

```yaml
services:
  stempeluhr:
    image: ghcr.io/tomat3/stempeluhr:0.4.0
    container_name: stempeluhr
    restart: unless-stopped
    volumes:
      - /volume1/docker/stempeluhr/data:/app/data
    ports:
      - 8002:8080
    environment:
      Admin__Password: "change-me"
      Kimai__BaseUrl: "https://kimai.example.local"
      Stempeluhr__NfcReaderToken: "change-me-reader-token"
      # Optional und nur in besonderen Topologien noetig (siehe
      # Sicherheitshinweis unten): IP-Adresse(n) vertrauter Reverse-Proxys,
      # damit X-Forwarded-For ausgewertet wird. Im Standard-Setup (alle Geräte
      # ueber den Cloudflare-Tunnel) ist KEINE Einstellung noetig.
      # Stempeluhr__KnownProxies__0: "172.18.0.1"
```

Die App ist intern unter `http://<nas-ip>:8002/` erreichbar. Fuer Raspberry Pi,
Tablet und normale Benutzer sollte die externe HTTPS-Adresse verwendet werden,
also z.B. `https://stempeluhr.example.local/`.

Fuer das Raspberry-Pi-Terminal:

- Chromium-URL:
  `https://stempeluhr.example.local/terminal?terminalId=stempeluhr-pi-01`
- NFC-Agent `api_base_url`:
  `https://stempeluhr.example.local`
- NFC-Agent `reader_token`:
  derselbe Wert wie `Stempeluhr__NfcReaderToken`
- `terminal_id` im Agenten und `terminalId` in der Chromium-URL muessen identisch sein.

Wichtig: Der Docker-Port `8002:8080` ist nur die interne NAS-Veroeffentlichung.
Wenn Cloudflared davor liegt, bekommen Chromium und der NFC-Agent die externe
HTTPS-Adresse. Der Agent bekommt trotzdem nur die Basis-Adresse ohne `/terminal`.

### Sicherheitshinweis: Offline-Queue und PINs

Der Kiosk/Client speichert gequeute Offline-Stempel (inklusive PIN) im
`localStorage` des Browsers, damit ein Offline-Stempel auch einen Neustart des
Kiosk-Browsers ueberlebt. Das ist eine bewusste Abwaegung:

- `localStorage` ist auf dem Geraet im Klartext lesbar (lokaler Zugang oder
  erfolgreicher XSS). Die Stempeluhr geht davon aus, dass Kiosk-Hardware
  (Tablet am Eingang, Raspberry-Pi-Terminal) vertrauenswuerdig ist.
- Wer das Risiko senken will: Kiosk-Geraete physisch sichern, Browser im
  Kiosk-Modus ohne DevTools betreiben, keine weiteren Websites im selben
  Browser-Profil oeffnen.
- Sauberste Loesung waere eine Terminal-/Reader-Token-Authentifizierung statt
  der PIN fuer gequeute Events; das ist als Follow-up eingeplant.

Zusaetzlich gilt: Der Sync-Endpoint `/api/kiosk/clock/sync` ist unauthentifiziert
(4-stellige PIN als einziger Schutz), wird aber per Client-IP gedrosselt
(20 Request-Einheiten/60 s) und nimmt maximal 100 Events pro Batch an. Das
Budget wird dabei nach **Event-Anzahl** bepreist (10 Events = 1
Request-Einheit), und eine Batch-Verarbeitung bricht beim ersten
PIN-Fehlschlag ab - die uebrigen Events des Batches bleiben in der
Client-Queue und werden einzeln in Folgerunden erneut versucht. Pro Request
entsteht so hoechstens **ein** PIN-Vergleichsergebnis; massenhaftes
Durchprobieren von PINs ueber grosse Batches ist damit ausgeschlossen (ein
Angreifer mit eigenen Requests bleibt auf die 20 Requests/60 s begrenzt).

Der Replay akzeptiert daneben die NFC-Karten-ID, mit der die Session am
Terminal entsperrt wurde (Paritaet zum Live-Pfad, der Karten-Touch ohne PIN
authentifiziert). Die Karte wird nur akzeptiert, wenn sie demselben
Mitarbeiter zugeordnet ist wie die Event-Angabe.

Wichtig zur Einordnung: Wenn alle Geraete wie ueblich ueber den
Cloudflare-Tunnel zugreifen, teilen sie sich die oeffentliche IP des Standorts -
das Budget ist dann zwangslaeufig ein gemeinsamer Topf, und
`Stempeluhr__KnownProxies__0` aendert daran nichts. Die Einstellung lohnt nur,
wenn die App Anfragen mit echten, unterscheidbaren Client-IPs sieht (z. B.
mehrere Standorte mit eigenen Internetanschluessen, VPN-Zugriffe oder direkte
LAN-Nutzung). Gegen gezieltes PIN-Raten an einem einzelnen Konto ist ein
Fehlversuch-Backoff vorgesehen (Issue #8); die sauberste Loesung bleibt die
Terminal-Token-Auth (Issue #7).

## Telegram-Benachrichtigung bei Stempelungen

Optional kann die API bei jedem **echten** Stempel-Übergang eine
Telegram-Nachricht an einen Chat (z. B. eine private Gruppe des Kunden)
senden. Gemeldet werden Kommen, Gehen, Pause-Start und Pause-Ende.
Bewusst **nicht** gemeldet werden:

- No-op-Stempel („schon eingestempelt", „nicht eingestempelt" usw.) —
  Doppel-Taps bleiben stumm.
- Nachgeholte Offline-Stempel (Sync-Replay) — nur Live-Stempelungen lösen
  eine Nachricht aus.

Nachrichtenformat (erzeugt von `TelegramMessageFactory`):

```text
🟢 Anna Mustermann · eingestempelt um 08:12
🔴 Anna Mustermann · ausgestempelt um 17:03
🟡 Anna Mustermann · Pause um 12:31
```

Die Uhrzeit wird in der Kimai-User-Zeitzone des Mitarbeiters formatiert.
Fehler beim Senden (Telegram nicht erreichbar) beeinflussen den Stempelvorgang
nie — die Benachrichtigung ist Best effort (kein Retry, kein Doppelversand).

### Einrichten

1. **Bot anlegen:** Bei @BotFather im Telegram `/newbot` ausführen und den
   Bot-Token kopieren (Secret — wird weder im Client noch über die Admin-API
   ausgeliefert, dort gibt es nur „hinterlegt: ja/nein". Eine Anzeige/
   Bearbeitung im Admin-UI ist als Ausblick geplant — aktuell wird die
   Telegram-Konfiguration ausschließlich in der `settings.json` gesetzt).
2. **Gruppe:** Eine private Telegram-Gruppe anlegen, den Bot hinzufügen und
   als Administrator einstellen. Weitere Empfänger lassen sich später einfach
   in die Gruppe aufnehmen.
3. **Chat-ID ermitteln:** Eine beliebige Nachricht in die Gruppe schreiben,
   dann im Browser `https://api.telegram.org/bot<TOKEN>/getUpdates` öffnen —
   `result[0].message.chat.id` ist die Gruppen-ID (negative Zahl).
4. **Eintragen:** In `data/settings.json` (dort, wo auch die Kimai-Tokens
   liegen) ergänzen:

   ```json
   {
     "telegramBotToken": "<TOKEN>",
     "telegramChatId": "-1001234567890"
   }
   ```

   Die Einstellungen werden pro Request aus der Datei geladen — die Änderung
   wirkt sofort, kein Neustart nötig. Fehlen beide Felder (oder ist eines
   leer), ist die Benachrichtigung deaktiviert. Über Umgebungsvariablen ist
   die Telegram-Konfiguration bewusst nicht einstellbar.

5. **Testen:** Ein Mitarbeiter stempelt — die Nachricht muss in der Gruppe
   erscheinen. Alle vier Aktionen einmal durchspielen.

## Semantische Versionierung

Versionen folgen SemVer: `MAJOR.MINOR.PATCH`. Die Release-Version ist der Git-Tag, zum Beispiel `v0.1.3`.

Es gibt keine Versionsdatei, die pro Release angepasst werden muss:

- Lokale Builds verwenden `0.0.0-local`.
- Release-Builds bekommen die Version aus dem Git-Tag.
- Die GitHub Action uebergibt die Tag-Version als Docker-Build-Arg an `.NET`.
- `stempeluhr-client/package.json` bleibt bei `0.0.0`, weil die Angular-App nicht als npm-Paket released wird.

Automatisch versionieren:

1. In GitHub `Actions` oeffnen.
2. Workflow `Create version tag` starten.
3. `patch`, `minor` oder `major` waehlen.
4. Der Workflow erzeugt den naechsten Tag, zum Beispiel `v0.1.3`.
5. Der Workflow `Release container` baut und pusht danach automatisch das Docker-Image.

Manuell geht es weiterhin ueber einen Git-Tag:

```powershell
git tag v0.1.3
git push origin v0.1.3
```

Der Workflow `.github/workflows/release-container.yml` baut bei Tags wie `v0.1.3` oder bei einem veroeffentlichten GitHub Release ein Docker-Image und pusht es nach GitHub Container Registry:

```text
ghcr.io/<owner>/<repo>:0.1.3
ghcr.io/<owner>/<repo>:0.1
ghcr.io/<owner>/<repo>:latest
```

## Kimai-Endpunkte

Die App verwendet serverseitig diese Kimai-Endpunkte:

- `GET /api/timesheets/active`
- `POST /api/timesheets`
- `PATCH /api/timesheets/{id}/stop`
