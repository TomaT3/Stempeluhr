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
Der Mitarbeiter bleibt in Kimai eingestempelt, bis jemand den tatsächlichen
Stop bzw. Pausen- oder Tätigkeitswechsel manuell korrigiert. Die Ablehnung
verhindert damit eine Buchung zur falschen Uhrzeit und muss bearbeitet werden.
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
  Chromium-Prozesszahl und summierten RSS, pcscd-RSS, anonymen pcscd-RAM
  (`RssAnon`), Prozess-Swap (`VmSwap`) und deren Summe, Temperatur,
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
  ein. Fehler beim Anlegen oder Installieren der journald-Konfiguration
  werden gemeldet und unterbrechen das Agent-Update nicht; der nächste
  Update-Lauf versucht die Installation erneut. Ein fehlgeschlagener
  journald-Neustart wird ebenfalls gemeldet; die installierte Einstellung
  greift dann beim nächsten Neustart.
  Ein abruptes Abschalten kann die allerletzten noch nicht geschriebenen
  Journaleinträge verlieren.

Die Diagnose erfasst keine Namen, Mitarbeiter-IDs, PINs, Karten-IDs,
Eingabeinhalte, Request-/Response-Bodies oder Tokens. Nur die Event-ID einer
Live-Buchung erlaubt die Zuordnung zum vorhandenen Ablehnungsjournal.
Das normale NFC-Journal älterer Agenten enthält weiterhin Karten-IDs;
Logexporte daher vertraulich behandeln und nicht in Git committen.
Die Diagnose startet keinen Browser neu und führt keine Buchung aus.

## PC/SC-Speicherwachstum prüfen

Die Messungen vom 04.10.2026 in
[Issue #86](https://github.com/TomaT3/Stempeluhr/issues/86) zeigen Wachstum
des anonymen `pcscd`-Speichers einschließlich Swap auf einem Trixie/arm64-Pi
mit pcsc-lite 2.3.3 und Polkit. Ein plötzlich fallender RSS oder steigender
`MemAvailable` ist keine Entwarnung: Seiten können nur ausgelagert worden
sein. pcscd 2.3.3 verliert bei jeder Polkit-Prüfung (neuer PC/SC-Kontext,
jede Kartenverbindung) Speicher; der Upstream-Fix ist ab 2.5.0 enthalten.
Der Agent wartet deshalb ereignisgesteuert auf Karten und verbindet sich nur
noch einmal pro aufgelegter Karte; die Paketmigration liefert den Fix selbst.

Die Agent-Historie, API-Diagnose und Influx enthalten `pcscdRssKb`,
`pcscdAnonymousKb`, `pcscdSwapKb` und `pcscdAnonymousAndSwapKb`. Die Summe
fehlt bei nicht lesbaren Komponenten; sie wird nicht als Null dargestellt.
Bei keinem oder mehreren lesbaren `pcscd`-Prozessen wird keine scheinbar
eindeutige Prozessmessung geliefert. `pcscdPid` und `pcscdStartTicks`
(Startzeit in Kernel-Clock-Ticks seit Boot) unterscheiden einen Prozess von
seinem Nachfolger, auch bei PID-Wiederverwendung. `pcscdVersion` ist die
installierte Debian-Paketversion, wird spätestens alle zehn Minuten neu
abgefragt und beweist allein nicht den Stand eines laufenden alten Binaries.
Messwerte in KiB werden im Adminbereich in MiB angezeigt.

Nach dem [Paketwechsel](raspberry-pi-kiosk-nfc.md#pcsc-paketmigration-bestehender-terminals)
den Verlauf von `pcscdAnonymousAndSwapKb` mindestens 24 Stunden beobachten.
Erwartet wird ein begrenzter Verlauf nach der Aufwärmphase, ohne
fortlaufendes Wachstum von anonymem RAM plus Swap. Ein neuer `pcscdStartTicks`
kennzeichnet einen Neustart; er setzt den Verlauf zurück und ist kein
Fixnachweis.
Bei weiterem Wachstum Heap-Profil/Allokationsdiagnose des NFC-Stacks
erstellen und Leck, Retention oder Fragmentierung unterscheiden.

Ein gezielter `pcscd`-Neustart kann Speicher vorübergehend freigeben,
unterbricht aber NFC und gilt nur als Übergangslösung. Echte Kartenscans,
Entfernen/Wiederauflegen sowie Wiederverbindung nach USB-Wechsel und
Dienstneustart müssen auf Hardware geprüft werden. Die Container- und
Regressionstests ersetzen diese Abnahme nicht.

## Überwachung

Die API merkt sich den letzten Bericht jedes Terminals im Speicher. Die Seite
**Admin → Terminalstatus** (`/admin/terminals`) zeigt für jedes Terminal mit
Eintrag in `terminalTokens` den Zustand, die Zeit seit dem letzten Bericht,
aktive Probleme und die letzten Werte. Sie lädt sich alle 30 Sekunden neu.
Den Verlauf über die Zeit zeigt ein optionales Grafana-Dashboard (siehe
[README](../README.md#terminal-metriken-in-grafana-optional)).

Ein Hintergrunddienst prüft jede Minute. Beginnt eine Störung, schickt er eine
Telegram-Nachricht an `telegramAlertChatId` (leer: `telegramChatId`), und eine
zweite, wenn sie endet. Dazwischen gibt es keine Erinnerungen. Mehrere
Änderungen eines Terminals gehen als eine Nachricht raus. Nimmt Telegram eine
Nachricht nicht an, versucht es die nächste Prüfung erneut.

| Bedingung | Alarm | ab Dauer | Entwarnung |
|---|---|---|---|
| Terminal meldet sich nicht | kein Bericht seit > 5 min | – | nächster Bericht |
| Kiosk-Seite hängt | `uiStatus` `missing`/`not-seen` | 3 min seit letztem Lebenszeichen | `alive` |
| Offline-Stempel stauen sich | `pending` > 0 | 15 min | `pending` = 0 |
| Stromversorgung | aktuelle Drosselungsbits (0–3) | 2 min | Bits frei |
| Temperatur | ≥ 80 °C | 5 min | < 75 °C |
| Arbeitsspeicher | < 100 MB frei | 5 min | > 150 MB |
| CPU | > 90 % | 10 min | < 70 % |
| Speicherplatz | < 500 MB frei | – | > 750 MB |

Zwischen Alarm- und Entwarnungsgrenze bleibt der bisherige Zustand bestehen.
Fehlt ein Messwert, ändert sich der Zustand dieser Bedingung ebenfalls nicht.
Solange ein Terminal nicht erreichbar ist, entstehen aus seinen alten Werten
keine neuen Alarme und keine Entwarnungen. Hat sich ein Terminal seit dem
API-Start noch nie gemeldet, gilt der Start als letzter Bericht. Die
Entwarnung „wieder erreichbar“ unterscheidet über die Laufzeit des Pis
zwischen Neustart und durchgelaufenem Pi (dann eher Netz oder Tailscale).

Grenzen:
- Vom Server aus sieht ein Netzausfall genauso aus wie ein eingefrorener Pi.
- Reagiert nur der Touchscreen nicht, während Seite und Lebenszeichen
  weiterlaufen, ist das von außen nicht erkennbar.
- Der Zustand liegt nur im Speicher. Nach einem API-Neustart kann eine
  laufende Störung erneut gemeldet werden, und eine Entwarnung über den
  Neustart hinweg entfällt.
- Ohne Telegram-Konfiguration zeigt nur die Statusseite die Störungen.

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
