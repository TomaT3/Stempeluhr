# Logs und Traces (OpenTelemetry)

Die API kann ihre Logs und Traces per OTLP an ein selbst gehostetes Backend
schicken, z. B. OpenObserve auf dem NAS. Ein Stempel ist dann über eine
Trace-ID auffindbar: eingehender Aufruf, eigener Span (`kiosk.clock`,
`offline.sync`, `offline.apply`, `offline.skip`, `outbox.flush`) und jeder Kimai-Aufruf darunter.
Logs, die während eines Aufrufs entstehen, tragen dieselbe `trace_id`.

Ohne Konfiguration ist nichts davon aktiv; die Konsole bleibt wie bisher.

## Anbindung

Nur Umgebungsvariablen am Container, nichts in `data/settings.json`:

```yaml
    environment:
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://192.168.1.10:5080/api/default"
      OTEL_EXPORTER_OTLP_HEADERS: "Authorization=Basic <base64(email:passwort)>,stream-name=stempeluhr"
      OTEL_SERVICE_NAME: "stempeluhr-api"   # optional, Standard
```

- Der Endpunkt endet ohne `/`; die API hängt `/v1/logs` und `/v1/traces` an
  (OTLP über HTTP/Protobuf).
- `stream-name` legt bei OpenObserve den Stream für Logs **und** Traces fest.
- Der Basic-Auth-Wert ist ein Secret: nur im Portainer-Stack setzen, nicht in
  Git. Aus dem Container ist OpenObserve über die NAS-IP oder ein gemeinsames
  Docker-Netz erreichbar, nicht über `localhost`.
- Ist das Backend nicht erreichbar, verwirft der Exporter die Daten dieser
  Zeit. Stempeln beeinflusst das nie.

## Was aufgezeichnet wird

| Span | Attribute |
| --- | --- |
| `POST /api/...` (ASP.NET Core) | Route, Status, Dauer. Nur `/api`, ohne `/api/health` |
| `kiosk.clock` | `stempeluhr.event_id`, `.action`, `.employee_id`, `.employee_name`, `.source` (card/pin/web/unknown), `.terminal_id`, `.outcome` (booked/continued/noop/bad_request/retry/failed/unauthorized/locked), bei `retry`, `failed` und `locked` auch `error.type` und `.error`, nur bei `failed` Fehlerstatus. Bei `unauthorized` und `locked` steht die angegebene `employee_id`, aber kein Name |
| `offline.sync` | `stempeluhr.batch_size`; `.outcome` `failed` und Fehlerstatus, wenn der Lauf abbricht |
| `offline.apply` | wie `kiosk.clock` plus `.performed_at` und `.state` (Kimai-Zustand); `.outcome` ist `booked`, `noop`, `buffered`, `locked` oder `rejected` (nur dann Fehlerstatus). Abgelehnte Ereignisse tragen die angegebene `employee_id`, aber keinen Namen |
| `offline.skip` | Ereignis, das `offline.apply` nicht erreicht: Event-ID, Aktion, angegebene `employee_id`, `.performed_at`, ohne Namen und Fehlerstatus. `.outcome` ist `buffered`, `locked`, `skipped` (nach einer abgelehnten PIN im selben Batch, der Kiosk behält es), `duplicate` oder `rejected` (bereits abgelehnt, erneut gesendet) |
| `outbox.flush` | `stempeluhr.outbox_size`, `.drained`, `.interrupted`, nur wenn die Outbox nicht leer ist; `.outcome` `failed` und Fehlerstatus, wenn der Lauf abbricht |
| HTTP-Client | Kimai- und Telegram-Aufrufe mit Status und Dauer |

`continued` heißt: Kimai hat den Wechsel abgelehnt, das laufende Blatt wurde
gestoppt und die Arbeit auf der vorherigen Tätigkeit fortgesetzt. `retry` heißt:
Kimai war nicht erreichbar oder der Kiosk hat aufgegeben. Er reiht den Stempel
ein, der Nachtrag erscheint unter derselben Event-ID als `offline.apply`. Bleibt
er aus, ist das der Stempel, den man sucht. Scheitert nach einer Buchung nur das
Statuslesen, bleibt `booked` stehen und `.error` nennt den Grund.

**Personenbezug:** Der Export enthält bewusst Namen von Mitarbeitern, damit sich
ein Stempel ohne Nachschlagen zuordnen lässt. Logs und Fehlertexte werden nicht
gefiltert und können auch Antworten von Kimai und Telegram enthalten. Das Backend
deshalb nur selbst hosten und nur im LAN bzw. per VPN erreichbar machen.

Nie im Export: PIN, Karten-ID, Tokens. Query-Strings werden aus `url.full`
entfernt, der Telegram-Bot-Token im Pfad wird zu `/bot***`. Die HttpClient-Logs
zu Telegram nennen die volle URL und gehen deshalb bei keinem Log-Level in den
Export.

Texte werden im Export auf 8 KiB gekürzt (`OTEL_ATTRIBUTE_VALUE_LENGTH_LIMIT`
überschreibt das für Attribute). Event-ID, Aktion und Mitarbeiter-ID kommen
ungeprüft vom Client und könnten den Export-Batch sonst sprengen.

Nicht exportiert, weil sie die Stempel übertönen:

- das Telegram-Long-Polling und die Influx-Schreibaufrufe,
- erfolgreiche HTTP-Aufrufe ohne Eltern-Span, etwa die Arbeitszeit-Prüfung alle
  5 Minuten oder Telegram-Benachrichtigungen. Fehlgeschlagene bleiben sichtbar,
- die HttpClient-Logzeilen zu Kimai und Influx unterhalb von Warning. Die Konsole
  zeigt sie weiterhin nach den allgemeinen Log-Leveln.

Benachrichtigungen laufen in einer eigenen Trace, nicht unter dem Stempel.

## Ein Stempel, eine Trace

Die Trace-ID eines Stempels ist seine Event-ID. Der Kiosk schickt sie als
`traceparent` (W3C) mit. Event-IDs sind immer 32 Hex-Zeichen, auch über http://.
Nur kürzere IDs älterer Kiosk-Versionen, die noch in einer Queue liegen, werden
links mit Nullen auf 32 Zeichen aufgefüllt. In derselben Trace liegen:

- der Live-Versuch: `POST /api/kiosk/clock`, `kiosk.clock` und die Kimai-Aufrufe,
  direkt oder über den Agenten,
- der Nachtrag aus der Queue: `offline.apply` samt Kimai-Aufrufen, auch wenn
  er erst später aus der Outbox der API kommt (`outbox.flush`). Der Nachtrag
  verweist per Span-Link auf `offline.sync` bzw. `outbox.flush`. Der Batch
  selbst bleibt eine eigene Trace, weil er Stempel mehrerer Traces enthält.

`offline.skip` (gepuffert, Duplikat, gesperrt, übersprungen) bleibt unter dem
Batch und verweist nur per Span-Link auf die Trace des Stempels: Während eines
Kimai-Ausfalls sendet der Kiosk gepufferte Ereignisse jede Minute neu. Gefunden
wird es über `stempeluhr_event_id`.

In der Queue steht das `traceparent` pro Ereignis im Body, nicht als Header.
Auch Ereignisse, die eine ältere Kiosk-Version eingereiht hat, bekommen es so
beim Senden. Ein ungültiger Wert wird ignoriert; der Span hängt dann wie bisher
unter dem Batch. Ein Wert ohne Sampled-Flag (`-00`) wird trotzdem aufgezeichnet.
Der Agent reicht beim Sync keinen `traceparent`-Header weiter, nur beim
Live-Stempel.

Über den Agenten geht der Header nur, wenn dessen `/health`
`features: ["traceparent"]` meldet. Ein älterer Agent lässt den Header im
CORS-Preflight nicht zu; der Stempel liefe dann ohne Terminal direkt. Der Kiosk
fragt `/health` im Hintergrund ab. Der erste Stempel nach dem Laden geht deshalb
ohne Header über den Agenten und beginnt dort eine eigene Trace, der Nachtrag
landet trotzdem in der Trace der Event-ID. Scheitert ein Stempel mit Header an
Status 0, fragt der Kiosk `/health` neu ab. Nennt ein erreichbarer Agent die
Fähigkeit nicht mehr (zurückgerollt), geht der Stempel einmal ohne Header über
den Agenten, sonst direkt. 404 und 503 lassen die gemerkte Antwort stehen.

Der Kiosk zeichnet selbst keine Spans auf. Der oberste Span einer Trace zeigt
deshalb auf einen Eltern-Span, den es im Backend nicht gibt.

## Ereignisse der Terminals

`/api/kiosk/diagnostics` schreibt jedes Ereignis des Kiosks als eigenen
Log-Eintrag. Die Liste der Ereignisse steht in den
[Terminal-Störungen](terminal-diagnostics.md#erfassung-auf-dem-terminal).

| Attribut | Inhalt |
| --- | --- |
| `stempeluhr_kiosk_event` | `session.open`, `stamp.route`, `http.clock`, `error.storage`, `state`, … |
| `stempeluhr_device_time`, `stempeluhr_seq` | Zeit und Reihenfolge auf dem Gerät |
| `stempeluhr_terminal_id`, `stempeluhr_event_id`, `stempeluhr_employee_id`, `stempeluhr_employee_name` | wie an den Spans |
| `stempeluhr_route`, `_fallback`, `_method`, `_reason`, `_via`, `_what`, `_action` | je nach Ereignis |

Der Agent schickt die Ereignisse einmal pro Minute. `_timestamp` ist deshalb die
Zeit des Berichts: Für die Reihenfolge nach `stempeluhr_device_time` und
`stempeluhr_seq` sortieren. Die Geräteuhr kann von der Serverzeit abweichen.

## Suchen

In OpenObserve unter Logs bzw. Traces den Stream `stempeluhr` wählen, oder per
SQL über `POST /api/default/_search?type=traces` bzw. `?type=logs`:

```sql
-- Event-ID = Trace-ID: Live-Versuch, Nachtrag und Kimai-Aufrufe eines Stempels
SELECT * FROM "stempeluhr" WHERE trace_id = '4bf92f3577b34da6a3ce929d0e0e4736'
-- dazu die Log-Einträge des Kiosks (eigene Trace pro Bericht)
SELECT * FROM "stempeluhr" WHERE stempeluhr_event_id = '<event-id>'
SELECT * FROM "stempeluhr" WHERE stempeluhr_employee_name = 'Max Mustermann' ORDER BY _timestamp DESC
SELECT * FROM "stempeluhr" WHERE stempeluhr_kiosk_event = 'stamp.route' AND stempeluhr_fallback IS NOT NULL
SELECT * FROM "stempeluhr" WHERE stempeluhr_terminal_id = 'stempeluhr-01' ORDER BY stempeluhr_device_time, stempeluhr_seq
SELECT * FROM "stempeluhr" WHERE stempeluhr_outcome IN ('failed', 'rejected')
```

OpenObserve bietet unter `/api/default/mcp` einen MCP-Server (u. a.
`SearchSQL`, `GetLatestTraces`). Nur im LAN bzw. per VPN erreichbar machen,
nie über den Cloudflare-Tunnel.

## Fehlersuche ohne SSH

Die meisten Meldungen lassen sich ohne Zugang zum Pi klären: mit OpenObserve,
**Admin → Terminalstatus** und **Admin → Abgelehnte Offline-Stempel**.

### „Mein Stempel fehlt“ oder „steht falsch in Kimai“

1. Den Stempel finden, nach Name und ungefährer Zeit:

   ```sql
   SELECT _timestamp, operation_name, stempeluhr_action, stempeluhr_outcome, stempeluhr_source,
          stempeluhr_terminal_id, stempeluhr_event_id
   FROM "stempeluhr" WHERE stempeluhr_employee_name = 'Max Mustermann'
   ORDER BY _timestamp DESC
   ```

   Im Zeitraum unter Traces suchen; die Kiosk-Ereignisse liegen unter Logs.
   Gibt es keinen Span, aber `session.open` und `stamp.route`, kam der Stempel
   nie bei der API an: `stamp.route` mit `route = queue` heißt, er liegt in der
   Queue des Kiosks (weiter mit Schritt 3).
2. Die Event-ID ist die Trace-ID. `WHERE trace_id = '<event-id>'` zeigt
   Live-Versuch, Nachtrag und jeden Kimai-Aufruf mit Status. Das `outcome`
   sagt, wie es weitergeht:

   | `outcome` | Bedeutung | Weiter |
   | --- | --- | --- |
   | `booked`, `noop` | Kimai hat gebucht bzw. war schon im Zielzustand | Eintrag in Kimai prüfen, ggf. danach manuell geändert |
   | `continued` | Wechsel abgelehnt, auf der vorherigen Tätigkeit weiter | Kimai-Aufrufe in der Trace zeigen den Grund |
   | `retry` | Kiosk reiht ein | Nachtrag `offline.apply` in derselben Trace suchen |
   | `buffered` | Kimai nicht erreichbar, die API hält den Stempel in ihrer Outbox | weiteres `offline.apply` in derselben Trace nach `outbox.flush` |
   | `rejected` | Kimai lehnt endgültig ab | **Abgelehnte Offline-Stempel**, siehe [Terminal-Störungen](terminal-diagnostics.md#offline-stempel-mit-extra-fields) |
   | `failed`, `unauthorized`, `locked` | Fehler, falsche PIN bzw. Sperre | `.error` und `error.type` am Span |

   Abgelehnte Kimai-Aufrufe stehen außerdem als Warning
   `Kimai <Operation>: HTTP <Status> … unsupported fields: …` im Log derselben
   Trace.
3. Fehlt der Nachtrag, liegt der Stempel noch auf dem Terminal. Die
   Zustandszeile zeigt die Queue:

   ```sql
   SELECT _timestamp, body FROM "stempeluhr"
   WHERE str_match(body, 'Terminal stempeluhr-01 diagnostics') ORDER BY _timestamp DESC
   ```

   `pending` > 0 über längere Zeit meldet auch der Alarm „Offline-Stempel stauen
   sich“. `rejected` zählt die Ablehnungen, die der Kiosk kennt.

### Terminal hängt oder meldet sich nicht

1. **Admin → Terminalstatus** und die Telegram-Alarme zeigen, welche Bedingung
   greift ([Überwachung](terminal-diagnostics.md#überwachung)).
2. Die Zustandszeilen (Abfrage oben) zeigen den Verlauf pro Minute: `uiStatus`
   und `heartbeatAgeSeconds` (Seite lebt), `screen`, `busy`, `blocked` und
   `lastInputAgeMs` (Sitzung hängt), `lagMs` (Timer verzögert), dazu CPU, RAM,
   Temperatur, `throttledFlags` und die pcscd-Werte.
3. Die Ereignisse vor dem Hänger in Gerätereihenfolge:

   ```sql
   SELECT stempeluhr_device_time, stempeluhr_seq, stempeluhr_kiosk_event, stempeluhr_employee_name,
          stempeluhr_reason, stempeluhr_route, stempeluhr_http_status
   FROM "stempeluhr" WHERE stempeluhr_terminal_id = 'stempeluhr-01'
   ORDER BY stempeluhr_device_time, stempeluhr_seq
   ```

   Ein `error.javascript`, `error.promise` oder `error.storage` kurz vorher
   spricht für die Seite, ein letzter `http.*` ohne Antwort für Netz oder API.
4. Kommt gar nichts mehr an, ist das Terminal vom Server aus nicht von einem
   Netzausfall zu unterscheiden. Dann bleibt der Pi selbst.

### Wann doch SSH

Nur auf dem Pi liegen:

- das Journal von NFC-Agent, Agent-Update, WLAN-Wiederherstellung und Kernel
  (Kartenleser, USB, Unterspannung, Neustarts),
- die vollständige Ereignishistorie `diagnostics.jsonl`. Die API bekommt pro
  Minute höchstens 80 Ereignisse; bei längerem Netzausfall fallen ältere aus
  dem Puffer,
- alles aus der Zeit, in der der Pi die API nicht erreicht hat.

Dafür einmal `tools/deploy/collect-diagnostics.sh` laufen lassen, siehe
[Beim nächsten Hänger](terminal-diagnostics.md#beim-nächsten-hänger).
