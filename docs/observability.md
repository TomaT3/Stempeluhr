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
| `kiosk.clock` | `stempeluhr.event_id`, `.action`, `.employee_id`, `.employee_name`, `.source` (card/pin/web/unknown), `.terminal_id`, `.outcome` (booked/continued/noop/bad_request/retry/failed), bei `retry` und `failed` auch `error.type` und `.error`, nur bei `failed` Fehlerstatus. Erst nach erfolgreicher Anmeldung, abgelehnte PINs und Sperren erzeugen hier keinen Span |
| `offline.sync` | `stempeluhr.batch_size` |
| `offline.apply` | wie `kiosk.clock` plus `.performed_at` und `.state` (Kimai-Zustand); `.outcome` ist `booked`, `noop`, `buffered`, `locked` oder `rejected` (nur dann Fehlerstatus). Abgelehnte Ereignisse tragen die angegebene `employee_id`, aber keinen Namen |
| `offline.skip` | Ereignis, das `offline.apply` nicht erreicht: Event-ID, Aktion, angegebene `employee_id`, `.performed_at`, ohne Namen und Fehlerstatus. `.outcome` ist `buffered`, `locked`, `skipped` (nach einer abgelehnten PIN im selben Batch, der Kiosk behält es), `duplicate` oder `rejected` (bereits abgelehnt, erneut gesendet) |
| `outbox.flush` | `stempeluhr.outbox_size`, `.drained`, `.interrupted`, nur wenn die Outbox nicht leer ist |
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

Trace-Kontext: Ein eingehendes `traceparent` (W3C) wird übernommen. Kiosk und
Agent setzen es noch nicht; bis dahin beginnt jede Anfrage eine eigene Trace.

## Suchen

In OpenObserve unter Logs bzw. Traces den Stream `stempeluhr` wählen, oder per
SQL über `POST /api/default/_search?type=traces` bzw. `?type=logs`:

```sql
SELECT * FROM "stempeluhr" WHERE trace_id = '4bf92f3577b34da6a3ce929d0e0e4736'
SELECT * FROM "stempeluhr" WHERE stempeluhr_event_id = '<event-id>'
SELECT * FROM "stempeluhr" WHERE stempeluhr_employee_name = 'Max Mustermann' ORDER BY _timestamp DESC
SELECT * FROM "stempeluhr" WHERE stempeluhr_outcome IN ('failed', 'rejected')
```

OpenObserve bietet unter `/api/default/mcp` einen MCP-Server (u. a.
`SearchSQL`, `GetLatestTraces`). Nur im LAN bzw. per VPN erreichbar machen,
nie über den Cloudflare-Tunnel.
