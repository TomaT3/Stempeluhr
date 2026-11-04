using System.Diagnostics;
using System.Text.RegularExpressions;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Stempeluhr.Api.Services;

/// <summary>
/// Eigene Spans der Stempeluhr. Ein eingehendes <c>traceparent</c> (Kiosk,
/// Agent) wird von der ASP.NET-Core-Instrumentierung übernommen, die Spans
/// hier hängen sich darunter, die Kimai-Aufrufe wiederum darunter. Attribute
/// nennen nie PIN, Karten-ID, Namen oder Tokens.
/// </summary>
public static class StempeluhrTelemetry
{
    public const string SourceName = "Stempeluhr";
    public static readonly ActivitySource Source = new(SourceName);

    public static Activity? StartStamp(string name, string? eventId, string? action, string? employeeId)
    {
        var activity = Source.StartActivity(name);
        if (activity is null) return null;
        activity.SetTag("stempeluhr.event_id", eventId);
        activity.SetTag("stempeluhr.action", action);
        activity.SetTag("stempeluhr.employee_id", employeeId);
        return activity;
    }

    public static void TagOrigin(Activity? activity, StampOrigin? origin)
    {
        if (activity is null || origin is null) return;
        activity.SetTag("stempeluhr.source", origin.Web ? "web" : origin.Method switch
        {
            StampMethod.Card => "card",
            StampMethod.Pin => "pin",
            _ => "unknown",
        });
        activity.SetTag("stempeluhr.terminal_id", origin.TerminalId);
    }

    public static void TagOutcome(Activity? activity, string outcome)
        => activity?.SetTag("stempeluhr.outcome", outcome);
}

/// <summary>
/// OpenTelemetry nur, wenn <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> gesetzt ist
/// (z. B. <c>http://nas:5080/api/default</c> für OpenObserve). Ohne Endpunkt
/// bleibt alles wie bisher: Konsole, keine Exporter. Header (Basic-Auth,
/// <c>stream-name</c>) kommen aus <c>OTEL_EXPORTER_OTLP_HEADERS</c>, der
/// Dienstname aus <c>OTEL_SERVICE_NAME</c> (Standard stempeluhr-api).
/// </summary>
public static class TelemetrySetup
{
    public const string EndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    public static bool IsEnabled(IConfiguration configuration)
        => !string.IsNullOrWhiteSpace(configuration[EndpointKey]);

    public static WebApplicationBuilder AddStempeluhrTelemetry(this WebApplicationBuilder builder)
    {
        if (!IsEnabled(builder.Configuration)) return builder;

        var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] is { Length: > 0 } name ? name : "stempeluhr-api";
        var version = typeof(TelemetrySetup).Assembly.GetName().Version?.ToString();

        builder.Logging.AddOpenTelemetry(options =>
        {
            options.IncludeScopes = true;
            options.IncludeFormattedMessage = true;
            options.ParseStateValues = true;
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName, serviceVersion: version))
            .WithTracing(tracing => tracing
                .AddSource(StempeluhrTelemetry.SourceName)
                .AddAspNetCoreInstrumentation(options => options.Filter = IsTracedRequest)
                .AddHttpClientInstrumentation()
                .AddProcessor(new TelemetryRedactionProcessor())
                .AddOtlpExporter(options => options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf))
            .WithLogging(logging => logging
                .AddOtlpExporter(options => options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf));

        return builder;
    }

    /// <summary>
    /// Nur API-Aufrufe; Health-Abfragen und statische Dateien (App, /pi/)
    /// würden die Stempel übertönen.
    /// </summary>
    public static bool IsTracedRequest(HttpContext context)
    {
        var path = context.Request.Path;
        return path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/health");
    }
}

/// <summary>
/// Läuft vor dem Exporter. Entfernt Query-Strings aus <c>url.full</c>,
/// schwärzt den Telegram-Bot-Token im Pfad (<c>/bot&lt;TOKEN&gt;/…</c>) und
/// verwirft Spans ohne Aussage: das Long-Polling von Telegram (alle 50 s) und
/// die Schreibaufrufe an InfluxDB.
/// </summary>
public sealed partial class TelemetryRedactionProcessor : BaseProcessor<Activity>
{
    [GeneratedRegex(@"/bot[^/]+", RegexOptions.CultureInvariant)]
    private static partial Regex BotTokenPattern();

    public override void OnEnd(Activity data)
    {
        if (data.GetTagItem("url.full") is not string url) return;

        if (url.Contains("/getUpdates", StringComparison.Ordinal) || url.Contains("/api/v2/write", StringComparison.Ordinal))
        {
            // Nicht exportieren: der Exporter überspringt nicht aufgezeichnete Spans.
            data.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        }

        var telegram = IsTelegram(url);
        data.SetTag("url.full", Redact(url));
        if (telegram && data.GetTagItem("url.path") is string path) data.SetTag("url.path", BotTokenPattern().Replace(path, "/bot***"));
        data.SetTag("url.query", null);
    }

    public static string Redact(string url)
    {
        var query = url.IndexOf('?');
        if (query >= 0) url = url[..query];
        return IsTelegram(url) ? BotTokenPattern().Replace(url, "/bot***") : url;
    }

    private static bool IsTelegram(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Host.Equals("api.telegram.org", StringComparison.OrdinalIgnoreCase);
}
