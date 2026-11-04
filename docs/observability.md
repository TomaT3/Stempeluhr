# Logs und Traces (OpenTelemetry)

Die API kann ihre Logs und Traces per OTLP an ein selbst gehostetes Backend
schicken, z. B. OpenObserve auf dem NAS. Ein Stempel ist dann über eine
Trace-ID auffindbar: eingehender Aufruf, eigener Span (`kiosk.clock`,
`offline.sync`, `offline.apply`, `outbox.flush`) und jeder Kimai-Aufruf darunter.
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
| `kiosk.clock` | `stempeluhr.event_id`, `.action`, `.employee_id`, `.source` (card/pin/web/unknown), `.terminal_id`, `.outcome` (booked/noop/bad_request) |
| `offline.sync` | `stempeluhr.batch_size` |
| `offline.apply` | wie `kiosk.clock` plus `.performed_at`; `.outcome` ist `booked` oder der No-op-Zustand |
| `outbox.flush` | `stempeluhr.outbox_size`, nur wenn die Outbox nicht leer ist |
| HTTP-Client | Kimai- und Telegram-Aufrufe mit Status und Dauer |

Nie im Export: PIN, Karten-ID, Namen, Tokens, Request- oder Response-Bodies.
Query-Strings werden aus `url.full` entfernt, der Telegram-Bot-Token im Pfad
wird zu `/bot***`. Das Telegram-Long-Polling und die Influx-Schreibaufrufe
werden gar nicht exportiert. `employee_id` ist die interne ID aus den
Einstellungen.

Trace-Kontext: Ein eingehendes `traceparent` (W3C) wird übernommen. Kiosk und
Agent setzen es noch nicht; bis dahin beginnt jede Anfrage eine eigene Trace.

## Suchen

In OpenObserve unter Logs bzw. Traces den Stream `stempeluhr` wählen, oder per
SQL über `POST /api/default/_search?type=traces` bzw. `?type=logs`:

```sql
SELECT * FROM "stempeluhr" WHERE trace_id = '4bf92f3577b34da6a3ce929d0e0e4736'
SELECT * FROM "stempeluhr" WHERE stempeluhr_event_id = '<event-id>'
```

OpenObserve bietet unter `/api/default/mcp` einen MCP-Server (u. a.
`SearchSQL`, `GetLatestTraces`). Nur im LAN bzw. per VPN erreichbar machen,
nie über den Cloudflare-Tunnel.
