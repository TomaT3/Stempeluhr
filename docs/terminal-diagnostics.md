# Terminal-Störungen untersuchen

## Offline-Stempel mit „extra fields“

Der Adminbereich unter **Abgelehnte Offline-Stempel** enthält den vollständigen
Kimai-Fehler, Event-ID und Ereigniszeit. Telegram kürzt den Fehler. Im
`errors.children`-Objekt stehen die Felder, die das Formular des API-Benutzers
anbietet. Fehlt ein tatsächlich gesendetes Feld bei einem Formularfehler,
lässt Kimai es in diesem Modus bzw. für diesen Benutzer nicht zu. Die
Erkennung ist unabhängig von der Sprache der Fehlermeldung.

- `begin` oder `end` fehlen: Unter **System → Einstellungen → Zeiterfassung →
  Erfassungsmodus** prüfen. Kimais Modus **Stempeluhr / Time-clock** verhindert
  bei gewöhnlichen Benutzern das Setzen nachgetragener Start- und Endzeiten
  auch über die API. Offline-Nachträge brauchen einen Modus, in dem der
  verwendete Mitarbeiter-Token diese Zeitpunkte setzen darf, etwa **Default**.
  Änderungen müssen mit den gewünschten Mitarbeiterrechten abgestimmt werden.
- `billable` fehlt: Berechtigung `edit_billable_own_timesheet` des
  API-Benutzers prüfen. Die Stempeluhr setzt die Abrechenbarkeit ausdrücklich.
- `full=true` erweitert ausschließlich die API-Antwort; es gibt keine
  zusätzlichen Schreibrechte.

Referenzen: [Kimai-Einstellungen](https://www.kimai.org/documentation/configurations.html),
[API-Formular](https://github.com/kimai/kimai/blob/main/src/API/TimesheetController.php),
[Stempeluhr-Modus](https://github.com/kimai/kimai/blob/main/src/Timesheet/TrackingMode/PunchInOutMode.php).

Offline-Starts werden nur mit dem tatsächlichen Startzeitpunkt angelegt.
Ein HTTP 400 löst keine Ersatzbuchung mit der aktuellen Uhrzeit mehr aus.
Offline-Stops setzen das erfasste Ende direkt per PATCH, ohne vorher auf
„jetzt“ zu stoppen. Wird das Zeitfeld abgelehnt, bleibt der laufende Eintrag
unverändert; auch Pause und Tätigkeitswechsel stoppen dann keinen Eintrag.
Stop und anschließender Start eines anderen Eintrags bleiben zwei getrennte
Kimai-Anfragen; Kimai bietet dafür keine gemeinsame Transaktion.
Der frühere Ersatzversuch konnte einen laufenden Eintrag mit falschem Beginn
hinterlassen, wenn auch der anschließende PATCH abgewiesen wurde. Bei alten
Ablehnungen deshalb vorhandene Kimai-Zeiten prüfen und korrigieren, bevor eine
zusätzliche Buchung angelegt wird. Bereits endgültig abgelehnte Events werden
nicht automatisch erneut gebucht; manuell nachtragen und im Admin markieren.
Der frühere Versions-Fallback entfällt auch für alte Kimai-Versionen, die
keinen Beginn beim Anlegen unterstützen.

## Erfassung auf dem Terminal

Nach einem regulären Release und Kunden-Deploy (separater Schritt) kommen
Client, Diagnosemodul und geänderte Unit über das vorhandene Agent-Update.
Bestehende `config.json`-Dateien bleiben gültig.

- Die Produktions-App sendet mit `terminalId` alle 15 Sekunden ein
  Lebenszeichen an `127.0.0.1:<local_port>/diagnostics/heartbeat`. Es enthält
  App-Version, Ruhe-/Mitarbeiterbildschirm, Busy-Zustand, Sperrgrund
  (`request`, `backlog`, `status`, `none`), Offline-Zustand, Queue-Zähler,
  Zeit seit der letzten Eingabe, Sichtbarkeit und Verzögerung des Timers.
  Außerdem werden begrenzt Anfragezeiten/-status und generische
  JavaScript-/Promise-/Speicherfehler erfasst. Fehlermeldungen und Stacks
  werden bewusst nicht übernommen, da sie Eingabewerte enthalten können.
- Der Agent speichert diese Daten in
  `/var/lib/stempeluhr-nfc-agent/diagnostics.jsonl`, mit zwei Rotationen zu
  je maximal etwa 2 MiB. Die Größe, nicht eine feste Aufbewahrungsdauer, ist
  garantiert begrenzt. `StateDirectory` überlebt Service- und Pi-Neustarts.
- Ein unabhängiger Agent-Thread misst jede Minute CPU-Auslastung, freien RAM,
  Chromium-Prozesszahl und summierten RSS, pcscd-RSS, Temperatur,
  Unterspannungs-/Drosselungsflags, freien Speicherplatz und Laufzeit.
  Summierter RSS zählt gemeinsam genutzten Speicher mehrfach; er dient als
  Verlauf, nicht als exakte physische Speichernutzung. Nicht verfügbare
  Messwerte fehlen. Ohne Browser-Lebenszeichen seit mehr als 60 Sekunden
  lautet der Zustand `missing`; vor dem ersten Empfang `not-seen`.
- Mit Terminal-Token sendet der Agent den jüngsten Zustand jede Minute an
  `/api/kiosk/diagnostics`. Die API prüft Token und Terminal-ID, begrenzt
  Anfragen und protokolliert nur erlaubte technische Felder. Die vollständige
  Ereignishistorie liegt auf dem Pi; der Server bekommt Stichproben.
  Zwischen den Sendeläufen sammelt der Agent bis zu 80 Ereignisse und
  bestätigt sie erst nach erfolgreicher Übertragung. Ältere Ereignisse
  können bei längerem Ausfall aus diesem begrenzten Puffer fallen.
  Wiederholte identische Health-Fehler werden nur bei Statusänderung erfasst.
  Ohne Token bleibt die lokale Diagnose aktiv. Ausfälle beim Versand
  beeinflussen weder Kartenlesen noch Stempeln und werden nicht unbegrenzt gepuffert.
- Das Update installiert `/etc/systemd/journald.conf.d/stempeluhr.conf`:
  persistentes Systemjournal mit 64 MiB Zielgrenze, 128 MiB freizuhaltendem
  Platz und maximal sieben Tagen Aufbewahrung. Diese Limits gelten für das
  gesamte Systemjournal; vorhandene strengere lokale Einstellungen beachten.
  Führt noch das alte Updater-Skript das erste Update aus, richtet der nächste
  Timer-Lauf das Journal auch bei gleicher Version und ohne Serververbindung
  ein. Ein fehlgeschlagener journald-Neustart wird gemeldet und unterbricht
  das Agent-Update nicht; die Einstellung greift dann beim nächsten Neustart.
  Ein abruptes Abschalten kann die allerletzten noch nicht geschriebenen
  Journaleinträge verlieren.

Die Diagnose erfasst keine Namen, Mitarbeiter-IDs, PINs, Karten-IDs,
Eingabeinhalte, Request-/Response-Bodies oder Tokens. Nur die Event-ID einer
Live-Buchung erlaubt die Zuordnung zum vorhandenen Ablehnungsjournal.
Das normale NFC-Journal älterer Agenten enthält weiterhin Karten-IDs;
Logexporte daher vertraulich behandeln und nicht in Git committen.
Die Diagnose startet keinen Browser neu und führt keine Buchung aus.

## Beim nächsten Hänger

Vor einem Neustart festhalten: Läuft die Sekundenanzeige weiter? Reagieren
X/Abmelden, Maus oder eine NFC-Karte? Wenn die Uhr und Browser-Lebenszeichen
weiterlaufen, aber keine Touch-Ereignisse ankommen, die Eingabe bzw. den
Touch-Treiber prüfen. Eine JavaScript-Tastensperre hat einen protokollierten
Sperrgrund. Fehlende Lebenszeichen bei weiterhin arbeitendem Agenten sprechen
für eine Browser-/Seitenstörung, beweisen aber allein keinen Renderer-Hänger
(z. B. auch eine geschlossene Seite, Hintergrundtab oder blockiertes Loopback).
Für die Abgrenzung Systemmetriken, Kernel- und NFC-Journal heranziehen.

Auf dem Admin-Rechner im Repository (bash):

```bash
ssh root@100.106.209.70 'bash -s -- "2026-10-03 11:45:00"' \
  < tools/deploy/collect-diagnostics.sh > pi-diagnostics.txt
```

Zeitangaben im Parameter gelten in der Zeitzone des Pis. Ohne Parameter
werden die letzten 24 Stunden der Journale abgefragt. Das Skript liest nur,
beendet keine Prozesse und exportiert weder Agent-Konfiguration noch
Browserprofil. `/health` und `/diagnostics` werden dabei auf dem Standardport
8737 abgefragt; bei abweichendem Port die zwei Abfragen anpassen.
Die vollständige begrenzte JSONL-Historie wird unabhängig vom Zeitfilter exportiert.

Im API-/Containerlog nach derselben Event-ID und den neuen `Kimai POST/PATCH`
Diagnosen suchen. Diese nennen die Operation, Laufzeit, HTTP-Status und
unerlaubte Felder. Erfolgreiche Kimai-Aufrufe stehen auf Debug-Level;
Ablehnungen auf Warning-Level. Die vollständigen Fehler bleiben im Adminjournal.
Für Serverlogs Docker-Logrotation konfigurieren (z. B. `max-size: 10m`,
`max-file: "3"`); die Aufbewahrung der API-Logs übernimmt der Betreiber.
