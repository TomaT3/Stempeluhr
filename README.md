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

### Korrekturanträge

Mitarbeiter vergessen, die Pause oder das Ausstempeln zu stempeln. Kimai-Zugang
bekommen sie bewusst nicht; stattdessen beantragen sie die Korrektur, und erst
nach der Freigabe durch Chef oder Admin schreibt die API sie in Kimai. Entschieden
wird auf der Admin-Seite `/admin/corrections` oder per Knopf in einem eigenen
Telegram-Chat ([Einrichtung](#korrekturanträge-per-telegram-freigeben));
beantragt wird am Kiosk und auf `/clock`.

**Ablauf:** Der Mitarbeiter wählt in der Liste der letzten 31 Tage einen Eintrag
(nach Schichten gruppiert, eine Nachtschicht über Mitternacht bleibt eine
Schicht) und sendet den Antrag ab. Er bleibt `pending`, bis der Admin ihn
genehmigt oder ablehnt (oder der Mitarbeiter ihn zurückzieht). Beim Genehmigen
wird das Timesheet neu gelesen, die Regeln werden erneut geprüft, dann folgen
die Schritte in Kimai. Anträge liegen in `data/time-corrections.json`: alle
offenen (`pending`, `failed`) bleiben, von den abgeschlossenen die letzten 1000.

**Arten:**

| Art | Eingabe | In Kimai |
| --- | --- | --- |
| `addPause` | Arbeits-Timesheet (auch ein **laufendes**), Pause von/bis darin | Ende auf Pausenbeginn kürzen, Pause anlegen, Rest-Arbeit bis zum alten Ende anlegen (mit Projekt, Aktivität, Beschreibung und `billable` des Originals; entfällt, wenn die Pause am alten Ende endet). Bei einem laufenden Eintrag läuft die Rest-Arbeit ab dem Pausenende weiter, siehe „Pause im laufenden Eintrag“ unten |
| `setEnd` | Timesheet, tatsächliches Ende (vor dem aktuellen) | Ende ändern |
| `addShift` | Beginn, Ende, Tätigkeit, optional Pause | Arbeit (bei Pause in zwei Teilen) und Pause anlegen, Beschreibung „Nachgetragen (Korrekturantrag)“ |
| `changeTimes` | gestopptes Timesheet, neuer Beginn und/oder neues Ende | Beginn/Ende ändern |

**Regeln** (beim Absenden und beim Genehmigen): nur eigene Timesheets der
letzten 31 Tage (beim Genehmigen gezählt ab dem Absenden, eine späte
Entscheidung lässt den Antrag nicht verfallen); keine Zeit in der Zukunft
(2 Minuten Toleranz); Ende nach Beginn, höchstens 16 h pro Schicht und 4 h pro
Pause; keine Überlappung mit anderen Timesheets (angrenzend ist erlaubt); pro
Timesheet höchstens ein offener Antrag. Der Kiosk sendet Zeiten auf die Minute:
liegt ein Pausenende oder neues Ende in der Minute des bisherigen Endes, gilt
das bisherige Ende samt Sekunden (keine Rest-Arbeit, kein Kürzen um Sekunden).
„Eigen“ prüft die API über die Kimai-Benutzer-ID des Tokens
(`/api/users/me`) gegen den Besitzer des Timesheets, denn Kimai liefert fremde
Einträge auch an Tokens mit `view_other_timesheet`. Fehlt eine der IDs, wird
abgelehnt statt geraten. Gebucht wird mit dem **Mitarbeiter-Token**, das
Admin-Token bleibt rein lesend. Es gibt keine Offline-Queue: ist Kimai nicht
erreichbar, antwortet das Absenden mit 503.

**Pause im laufenden Eintrag:** Wer in die Pause gegangen ist, ohne zu stempeln,
beantragt sie, ohne auszustempeln (`addPause` auf das laufende Timesheet, das
Ende ist dann leer). Beim Absenden gilt: die Pause liegt nach dem Beginn des
Eintrags, ihr Ende höchstens jetzt (2 Minuten Toleranz); höchstens 4 h, keine
Überlappung und ein offener Antrag pro Timesheet gelten wie sonst. `setEnd` und
`changeTimes` verlangen weiter einen gestoppten Eintrag. Bis zur Freigabe
stempelt der Mitarbeiter normal weiter. Beim Genehmigen hält die API **vor dem
ersten Schritt** den Stand des Eintrags im Antrag fest (`observedAtApply`,
`observedEndAtApply`; leer heißt „lief noch“), damit „Erneut versuchen“ auch nach
dem Kürzen richtig fortsetzt:

| Stand beim Genehmigen | Schritte in Kimai |
| --- | --- |
| Eintrag läuft noch | 1. Ende auf Pausenbeginn (`shorten`), 2. Pause anlegen (`pause`), 3. Rest-Arbeit ab Pausenende **laufend** starten (`startWork`; Projekt, Aktivität, Beschreibung und `billable` des Originals) |
| inzwischen gestoppt (Ende ≥ Pausenende) | wie bei einem gestoppten Eintrag, Rest-Arbeit bis zu diesem Ende |
| inzwischen vor dem Pausenende gestoppt oder sonst geändert | `failed` mit „Eintrag wurde inzwischen geändert“ |

Die Prüfung auf Überschneidungen umfasst die ganze Rest-Arbeit: bei einem
laufenden Eintrag bis jetzt, bei einem inzwischen gestoppten bis zu dessen
Ende; eine schon gestartete Rest-Arbeit zählt bis zu ihrem tatsächlichen Ende.
Hat der Mitarbeiter etwa nach einem gescheiterten Neustart eine andere
Tätigkeit gestempelt, scheitert „Erneut versuchen“, statt rückwirkend darüber
zu buchen. Vor Schritt 3 prüft die API außerdem, dass nichts anderes läuft; sonst wird der Antrag
`failed` („Eintrag wurde inzwischen geändert“) und der Admin trägt von Hand
nach oder versucht es erneut. Eine schon laufend gestartete Rest-Arbeit
erkennt die API an Beginn = Pausenende und gleicher Aktivität, ihr Ende spielt
keine Rolle (der Mitarbeiter kann inzwischen ausgestempelt haben). Solange noch
kein Schritt etwas geändert hat (etwa weil Kimai beim ersten Versuch nicht
erreichbar war), liest „Erneut versuchen“ den Stand neu: wer inzwischen
ausgestempelt hat, bekommt die Rest-Arbeit bis zu diesem Ende statt einer
laufenden. Ist der Eintrag schon gekürzt, gilt der festgehaltene Stand. Wie bei
jedem Antrag müssen Aktivität, Projekt, `billable` und Beschreibung dem
Snapshot entsprechen. In Telegram steht bei
einem laufenden Eintrag zusätzlich eine Zeile „Danach“, z. B. `06:00–12:00 ·
Pause 12:00–12:30 · ab 12:30 (läuft)`.

*Bekannte Grenze:* Stempelt der Mitarbeiter genau zwischen dem Kürzen (Schritt 1)
und dem Neustart (Schritt 3), also innerhalb von Millisekunden, aus, kann die
neu gestartete Rest-Arbeit danach trotzdem laufen; die API erkennt das nicht
und der Mitarbeiter stempelt dann einfach erneut aus.

**Idempotenz und `Failed`:** Jede Entscheidung läuft unter einer Sperre pro
Antrag. Wer einen schon entschiedenen Antrag erneut genehmigt oder ablehnt (zwei
Admins, Doppelklick), bekommt den Stand zurück und löst keine zweite Buchung
aus. Jeder erledigte Schritt wird gespeichert (`appliedSteps`); vor dem Anlegen
sucht die API in Kimai nach einem Eintrag mit genau diesem Beginn, Ende und
dieser Aktivität und überspringt den Schritt, wenn er schon existiert. Lehnt
Kimai ab (Erfassungsmodus, Sperrzeitraum, fehlende Berechtigung) oder hat sich
der Eintrag seit dem Antrag geändert („Eintrag wurde inzwischen geändert“), wird
der Antrag `failed` und behält den Grund; der Genehmigen-Aufruf antwortet
trotzdem mit 200. Der Admin kann dann **erneut versuchen** (setzt beim offenen
Schritt fort, bucht nichts doppelt) oder von Hand in Kimai nachtragen und den
Antrag **als erledigt markieren**.

**Endpunkte:**

- Kiosk (Auth wie `/api/kiosk/clock`: `employeeId` plus `pin` **oder**
  `nfcCardId`; falsche PINs zählen im PIN-Schutz, 5 pro Mitarbeiter sperren; zusätzlich
  30 Aufrufe pro Minute und Client-IP):
  `POST /api/kiosk/corrections/timesheets` (Auswahlliste),
  `POST /api/kiosk/corrections` (absenden; Zeiten als lokale Zeit
  `yyyy-MM-ddTHH:mm` in der Kimai-Zeitzone des Mitarbeiters),
  `POST /api/kiosk/corrections/mine` (eigene Anträge der letzten 31 Tage),
  `POST /api/kiosk/corrections/{id}/withdraw` (nur eigene `pending`-Anträge,
  sonst 404 bzw. 409). Fehler kommen als 400 `{ "message": … }` auf Deutsch.
- Arbeitszeit-Hinweise (rein lesend): `POST /api/kiosk/work-time-hints`, Body
  wie die übrigen Kiosk-Aufrufe (`employeeId` plus `pin` **oder** `nfcCardId`,
  PIN-Schutz wie oben). Eigenes Limit von 60 Aufrufen pro Minute und Client-IP,
  damit die Abfrage bei jeder Anmeldung das Korrektur-Budget (30/min) nicht
  aufbraucht. Ist Kimai nicht erreichbar, antwortet er mit 503. Die API liest
  die Timesheets der letzten 48 h und wertet sie mit denselben festen Regeln
  wie die Telegram-Warnung aus (über 6 h am Stück ohne Pause, über 10 h in der
  Schicht; nur Fälle, die noch laufen oder höchstens 24 h vorbei sind;
  Nachtschichten über Mitternacht zählen als eine Schicht). Gemeldet werden nur
  Fälle der jüngsten Schicht (Arbeit nach mindestens 8 h ohne Arbeit): Ein Fall
  einer früheren Schicht ist nicht die „letzte Schicht“, auch wenn er keine 24 h
  zurückliegt. Hat ein Eintrag im
  Bereich des Falls schon einen offenen Antrag (`pending`, `failed`), entfällt
  der Hinweis. Die Antwort `{ "timeZone": …, "hints": [ … ] }` enthält pro Fall
  `kind` (`continuous` oder `shift`), `begin` und `end` (lokale Zeit
  `yyyy-MM-ddTHH:mm` in der Kimai-Zeitzone des Mitarbeiters; `end: null` =
  Arbeit läuft noch), `workedSeconds` und `timesheetId`. Bei `continuous` ist
  das das laufende Arbeits-Timesheet, sonst der längste gestoppte Arbeits-Eintrag
  des Blocks; bei `shift` ist sie `null`. Der Aufruf bucht nichts, schickt kein
  Telegram und speichert nichts.
- Admin (Header `X-Admin-Password`, sonst 401):
  `GET /api/admin/corrections?status=open|all`,
  `POST /api/admin/corrections/{id}/approve`,
  `POST /api/admin/corrections/{id}/reject` (`{ "note": … }` optional),
  `POST /api/admin/corrections/{id}/retry` und
  `PUT /api/admin/corrections/{id}/resolved` (nur `failed`, sonst 409).

**Admin-Seite** `/admin/corrections` (Quick-Link „Korrekturanträge“ auf
`/admin`, mit der Zahl der offenen Anträge): Anmeldung mit dem Admin-Passwort
wie auf den anderen Admin-Seiten, Filter „Offen“ (`pending` und `failed`,
Standard) oder „Alle“. Jeder Antrag zeigt Mitarbeiter, Art, die Schicht,
**Vorher → Nachher** (bei einer nachgetragenen Pause die drei Teile Arbeit,
Pause, Rest-Arbeit), Kommentar, Quelle (Terminal oder `/clock`), Zeitpunkt des
Antrags und die Entscheidung mit Grund bzw. Kimai-Meldung. Die Zeiten stehen in
der Kimai-Zeitzone des Mitarbeiters; liegt das Gerät in einer anderen Zone,
steht sie dabei. Offene Anträge lassen sich genehmigen oder ablehnen (der
optionale Grund erscheint nach „Ablehnen“), fehlgeschlagene erneut versuchen
oder als „Manuell in Kimai erledigt“ markieren. Ist ein Antrag nur zum Teil
gebucht (z. B. Eintrag schon gekürzt, Pause fehlt), nennt die Seite die schon
erledigten Schritte, denn die Nacharbeit muss vom aktuellen Stand in Kimai
ausgehen. Bei einer Pause im laufenden Eintrag steht das Original als „– läuft“
und die Rest-Arbeit als „ab 12:30 (läuft)“; die Zeile „Laufender Eintrag“ sagt,
was beim Genehmigen passiert bzw. ob der Eintrag dabei noch lief oder schon
gestoppt war (dann Rest-Arbeit bis zu diesem Ende). Dafür liefert das DTO
`observedAtApply` und `observedEndAtApply` (lokale Zeit). Korrigierte Pausen-Einträge (`setEnd`, `changeTimes`) stehen als
„Pause“ da (`original.kind`/`original.label` im DTO). Nach jeder Aktion lädt die
Liste neu und zeigt das Ergebnis; während eine Aktion läuft, sind die Knöpfe
gesperrt. Die Seite ist für das Handy ausgelegt, damit der Chef unterwegs
freigeben kann.

**Kiosk und `/clock`:** Nach der Anmeldung (PIN **oder** Karte) steht in der
Sitzung der Knopf „Korrektur“. Er ist offline gesperrt („Korrektur nur online“),
ebenso solange Stempel des Mitarbeiters auf die Übertragung warten. Der Ablauf
(`features/clock/correction-flow`) ersetzt am Terminal die Stempelknöpfe und die
Stundenkarte in ihrer Spalte, auf `/clock` Status und Stempelknöpfe; Name und
Abmelden bleiben stehen. Er läuft in Schritten: Art („Pause nachtragen“,
„Ausstempeln nachtragen“, „Schicht nachtragen“, „Zeiten ändern“, „Meine Anträge“),
Eintrag (die Schichten der API, neueste zuerst, in einer intern scrollenden Liste;
bei Pause jede Arbeit, auch die laufende, sonst jeder gestoppte Eintrag, nie einer
mit offenem Antrag), Zeiten mit dem Stepper `shared/components/time-stepper` (Tag ±,
Stunde ±, Minute ±5 und ±1, vorbelegt, außerhalb der Regeln gesperrt),
Zusammenfassung Vorher → Nachher und Absenden. Nur `/clock` hat ein
Kommentarfeld (höchstens 300 Zeichen); das Terminal hat keine Tastatur. Bei einer
400-Antwort bleibt der Mitarbeiter in der Zusammenfassung und sieht die
Servermeldung. „Meine Anträge“ zeigt Status (bei Ablehnung mit Grund) und
Vorher → Nachher und bietet für offene Anträge „Zurückziehen“. Bei einer Pause im
laufenden Eintrag endet die Pause höchstens jetzt; vorbelegt sind die letzten
30 min bis jetzt (auf 5 min abgerundet, nie vor dem Beginn des Eintrags). Die
Zusammenfassung zeigt danach drei Zeilen, die letzte „ab 12:30 (läuft)“, und den
Hinweis, bis zur Freigabe ganz normal weiter zu stempeln. Mit `CorrectionStart`
(`addPause` oder `setEnd` plus Timesheet) öffnet die Seite den Ablauf direkt in
den Zeiten eines Eintrags. Läuft ein Abschnitt
länger als 12 h, erscheint „Vergessen auszustempeln?“: Der Knopf
stempelt über den normalen Stop-Pfad (`/api/kiosk/clock`, mit Offline-Queue) aus
und öffnet nur nach einem online gelungenen Stop „Ausstempeln nachtragen“ für genau
dieses Timesheet.

Nach jeder bestätigten Anmeldung (PIN-Login ohne wartende Stempel, Karte nach
dem `identify` des Servers, Status nach einem Nachtrag) und nach dem Schließen
des Ablaufs lädt die Sitzung die Arbeitszeit-Hinweise (`/api/kiosk/work-time-hints`)
und zeigt höchstens einen davon. Läuft die Arbeit seit über
6 h ohne Pause, steht dort „Pause vergessen?“ mit „seit 07:58 ohne Pause“. Der
Knopf stempelt **nicht**, sondern öffnet „Pause nachtragen“ für den laufenden
Eintrag; „Vergessen auszustempeln?“ geht vor. Ausgestempelt nennt der Hinweis
die letzte Schicht („Letzte Schicht: 7:10 Std. ohne Pause“ bzw. „Letzte Schicht:
10:40 Std.“, darunter „Prüfen“) und öffnet „Pause nachtragen“ für den Eintrag
des Hinweises bzw. bei über 10 h die Art-Auswahl. Treffen beide Fälle zu, steht
nur der Pausen-Hinweis da. In der Pause, offline und solange Stempel des
Mitarbeiters warten, gibt es keinen Hinweis; Fehler bleiben still. Die Hinweise
werden bei jedem Identitätswechsel und `back()` geleert, eine verspätete
Antwort nach einem Identitätswechsel oder einer Aktion wird verworfen.

Alle drei Hinweise stehen auf `/clock` unter dem Status, am Terminal in der
Uhr-Spalte unter Datum und Uhrzeit, darunter der Knopf „Korrektur“: In der
Mitarbeiter-Spalte schöbe jeder weitere Knopf die Stundenkarte bei 800×480
unter den Rand, offline neben dem Banner erst recht. Solange der
Korrekturablauf offen ist, blendet das Terminal sie aus.

Der Korrekturablauf lebt so lange wie die Sitzung: Neue Anmeldung,
X und Abbruch zerstören sie samt laufender Anfragen, eine verspätete Antwort
erreicht keinen anderen Mitarbeiter. Nach 2 min ohne Tipp geht der Kiosk in den
Ruhezustand; der Auto-Reload bei neuer Version wartet, bis niemand mehr im Ablauf
ist.

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

#### Korrekturanträge per Telegram freigeben

Neue [Korrekturanträge](#korrekturanträge) erscheinen in einem **eigenen
Telegram-Chat** mit den Knöpfen **Genehmigen** und **Ablehnen**. Der Chef
entscheidet dort direkt, ohne Admin-Seite; die Admin-Seite bleibt daneben
nutzbar. Ohne `telegramCorrectionChatId` gibt es keine Telegram-Freigabe: keine
Nachricht, kein Empfang, nur die Admin-Seite.

Die Nachricht nennt Mitarbeiter, Art, die betroffene Schicht, Vorher → Nachher
und den Kommentar, z. B.:

```text
📝 Korrekturantrag · Anna Mustermann
Ausstempeln nachtragen
Schicht: Mo 06.10. 22:00 – Di 07.10. 06:10
Ende: Di 07.10. 06:10 → Di 07.10. 05:40
Kommentar: Hab vergessen auszustempeln
```

Alle Zeiten stehen mit Wochentag und Datum in der Kimai-Zeitzone des
Mitarbeiters, damit Nachtschichten über Mitternacht eindeutig sind. „Schicht“
ist der Zeitraum des betroffenen Eintrags (bei einem Pausen-Eintrag „Pause“).

**Entscheiden:** Ein Tipp auf „Genehmigen“ oder „Ablehnen“ entscheidet noch
nichts; die Knöpfe wechseln zu „Ja, genehmigen“ bzw. „Ja, ablehnen“ und
„Zurück“. Erst die Bestätigung ruft den Service auf (als Entscheider steht der
Vorname des Telegram-Kontos, sonst der Benutzername, am Antrag). Ablehnen
geschieht dort ohne Grund. Danach ersetzt die API die Nachricht durch das
Ergebnis, ohne Knöpfe, und zwar nach **jeder** Entscheidung, auch wenn sie auf
der Admin-Seite fiel oder der Mitarbeiter zurückgezogen hat:

- `✅ Genehmigt von Max · 07.10. 09:12 – in Kimai eingetragen`
- `❌ Abgelehnt von Max · 07.10. 09:12`
- `⚠️ Nicht in Kimai eingetragen: … – bitte in Kimai nachtragen`
  (danach auf der Admin-Seite erneut versuchen oder als erledigt markieren;
  das Ergebnis ersetzt die Nachricht dann noch einmal)
- `↩️ Zurückgezogen`
- `☑️ Von Max manuell in Kimai nachgetragen`

**Wer darf:** Ein Tipp zählt nur, wenn die Nachricht im Korrektur-Chat steht
(`telegramCorrectionChatId`) und, falls `telegramApproverUserIds` gesetzt ist,
der Tippende in dieser Liste steht. Sonst antwortet der Bot mit „Keine
Berechtigung“ und es passiert nichts. Ist die Liste leer, darf jedes Mitglied
des Chats entscheiden. Jeder Tipp wird beantwortet, auch bei Fehlern. Ein
schon entschiedener Antrag antwortet „Bereits entschieden: …“ und die Nachricht
wird auf den Stand gebracht; doppelte Tipps, wiederholte Updates nach einem
Neustart und Telegram gleichzeitig mit der Admin-Seite lösen dank der Sperre
pro Antrag keine zweite Buchung aus.

**Einrichtung:**

1. Eine **eigene Gruppe** für die Freigabe anlegen, den Bot (siehe oben)
   hinzufügen und zum Admin machen.
2. **Chat-ID ermitteln**, und zwar **bevor** sie eingetragen wird: eine
   Nachricht in die neue Gruppe schreiben und
   `https://api.telegram.org/bot<TOKEN>/getUpdates` öffnen
   (`result[n].message.chat.id`, negativ). Danach fragt der Empfang der API
   selbst per `getUpdates` ab und verbraucht die Updates; ein manueller Aufruf
   liefert dann nichts mehr bzw. Fehler 409.
3. **User-IDs ermitteln:** In derselben `getUpdates`-Antwort steht pro
   Nachricht `result[n].message.from.id`. Wer entscheiden darf, schreibt vorher
   eine Nachricht in die Gruppe.
4. In `data/settings.json` ergänzen (wirkt ohne Neustart, spätestens nach dem
   laufenden Abruf von bis zu 50 s) oder im Admin unter „Telegram-Freigabe“
   eintragen (leer lassen bzw. leeren schaltet ab):

   ```json
   {
     "telegramBotToken": "<TOKEN>",
     "telegramChatId": "-1001234567890",
     "telegramCorrectionChatId": "-1009998887776",
     "telegramApproverUserIds": [123456789, 987654321]
   }
   ```

**Technik:** Der `TelegramUpdatePoller` fragt per Long-Polling ab
(`getUpdates`, `timeout=50`, nur `callback_query`; kein Webhook, nur
Outbound-HTTPS) und läuft nur mit Bot-Token **und** Korrektur-Chat. Der Offset
liegt im Speicher. Ein **409** heißt, dass für den Bot ein Webhook gesetzt ist
oder eine zweite Instanz mit demselben Token abfragt (z. B. eine
Testumgebung): das steht einmal im Log, danach wartet der Poller mit Backoff
und versucht es weiter. Auch bei Netzfehlern steigt die Wartezeit von 5 s auf
höchstens 60 s, ohne das Log zu fluten. `callback_data` hat die Form
`c:<aktion>:<id>` (höchstens 64 Bytes). Telegram-Fehler werden nur geloggt und
beeinflussen weder Entscheidung noch Buchung. Die Nachricht beim Absenden und
beim Zurückziehen geht im Hintergrund hinaus, damit ein langsames Telegram den
Kiosk nicht in sein Zeitlimit laufen lässt; die Anfragen an die Bot API
loggt .NET nur ab Warning, weil der Token im URL-Pfad steht.

### Terminal-Metriken in Grafana (optional)

Die API schreibt jeden Terminal-Bericht (etwa einmal pro Minute: Temperatur,
freier RAM, CPU, Load, Chromium-Speicher, Speicherplatz, Queue, Drosselung,
Laufzeit, Kiosk-Lebenszeichen) in eine InfluxDB. Schreiben klappt mit
InfluxDB 2.x und InfluxDB 3 über deren `/api/v2/write`. Das mitgelieferte
Dashboard nutzt aber Flux-Abfragen und läuft daher nur mit InfluxDB 2.x;
InfluxDB 3 kann kein Flux, dort Panels selbst mit SQL oder InfluxQL bauen.
Die Pis brauchen dafür nichts.

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

3. Nur InfluxDB 2.x: In Grafana eine InfluxDB-Datenquelle mit Abfragesprache
   **Flux** anlegen (Token mit Leserecht) und
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
([`.github/workflows/ci.yml`](.github/workflows/ci.yml)); den PC/SC-Pakettest
startet [`pcsc-packages.yml`](.github/workflows/pcsc-packages.yml) nur bei
PC/SC-Änderungen (siehe [tools/pcsc](tools/pcsc/README.md)).

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

Änderungen am Workflow oder Image-Build vorab ohne Release prüfen; der
Probelauf baut das Image, erstellt aber weder Tag noch Release und pusht
nichts:

```bash
gh workflow run release.yml --ref <branch> -f dry_run=true
```

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
