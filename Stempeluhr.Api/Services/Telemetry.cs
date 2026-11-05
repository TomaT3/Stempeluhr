using System.Diagnostics;
using System.Reflection;
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
/// nennen den Namen, damit ein Stempel ohne Nachschlagen zuzuordnen ist, aber
/// nie PIN, Karten-ID oder Tokens.
/// </summary>
public static class StempeluhrTelemetry
{
    public const string SourceName = "Stempeluhr";
    public static readonly ActivitySource Source = new(SourceName);

    public static Activity? StartStamp(string name, string? eventId, string? action, string? employeeId, string? employeeName)
    {
        var activity = Source.StartActivity(name);
        if (activity is null) return null;
        activity.SetTag("stempeluhr.event_id", eventId);
        activity.SetTag("stempeluhr.action", action);
        activity.SetTag("stempeluhr.employee_id", employeeId);
        activity.SetTag("stempeluhr.employee_name", employeeName);
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

    /// <summary>Ergebnis am laufenden Stempel-Span, nicht an einem fremden Span.</summary>
    public static void TagCurrentOutcome(string outcome)
    {
        if (Activity.Current is { } current && current.Source == Source) TagOutcome(current, outcome);
    }

    /// <summary>
    /// Der Stempel kam nicht durch. <paramref name="isError"/> ist false, wenn
    /// das Ereignis nicht verloren ist (gepuffert, gesperrt): Dann bleibt der
    /// Span ohne Fehlerstatus, nennt den Grund aber trotzdem.
    /// </summary>
    public static void TagFailure(Activity? activity, string outcome, Exception exception, bool isError = true)
    {
        if (activity is null) return;
        TagOutcome(activity, outcome);
        activity.SetTag("error.type", exception.GetType().Name);
        activity.SetTag("stempeluhr.error", exception.Message);
        if (isError) activity.SetStatus(ActivityStatusCode.Error, exception.Message);
    }

    /// <summary>
    /// Startet Fire-and-forget-Arbeit ohne den laufenden Span: Eine
    /// Benachrichtigung gehört nicht zur Buchung und kann den Span überleben.
    /// Ein Telegram-Timeout sähe sonst wie ein langsamer Stempel aus.
    /// </summary>
    public static void RunDetached(Action start)
    {
        var current = Activity.Current;
        Activity.Current = null;
        try
        {
            start();
        }
        finally
        {
            Activity.Current = current;
        }
    }
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

    /// <summary>Log-Kategorie des typisierten Kimai-Clients.</summary>
    public const string KimaiHttpLogCategory = "System.Net.Http.HttpClient.IKimaiClient";

    /// <summary>
    /// Der Release-Tag wie in <c>/api/health</c>. <c>AssemblyVersion</c> steht
    /// fest auf 0.0.0.0 (Directory.Build.props).
    /// </summary>
    public static string? ServiceVersion => typeof(TelemetrySetup).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion;

    /// <summary>Ausgehende HTTP-Aufrufe: gefiltert, und geschwärzt, bevor der Exporter sie sieht.</summary>
    public static TracerProviderBuilder AddStempeluhrHttpTracing(this TracerProviderBuilder tracing) => tracing
        .AddHttpClientInstrumentation(options => options.FilterHttpRequestMessage = IsTracedHttpCall)
        .AddProcessor(new TelemetryRedactionProcessor());

    public static WebApplicationBuilder AddStempeluhrTelemetry(this WebApplicationBuilder builder)
    {
        if (!IsEnabled(builder.Configuration)) return builder;

        var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] is { Length: > 0 } name ? name : "stempeluhr-api";

        builder.Logging.AddOpenTelemetry(options =>
        {
            options.IncludeScopes = true;
            options.IncludeFormattedMessage = true;
            options.ParseStateValues = true;
        });
        // Jeder Kimai-Aufruf ist schon ein Span. Die Konsole behält diese Zeilen.
        builder.Logging.AddFilter<OpenTelemetryLoggerProvider>(KimaiHttpLogCategory, LogLevel.Warning);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName, serviceVersion: ServiceVersion))
            .WithTracing(tracing => tracing
                .AddSource(StempeluhrTelemetry.SourceName)
                .AddAspNetCoreInstrumentation(options => options.Filter = IsTracedRequest)
                .AddStempeluhrHttpTracing()
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

    /// <summary>
    /// Ohne Aussage und gar nicht erst aufgezeichnet: das Long-Polling von
    /// Telegram (alle 50 s) und die Schreibaufrufe an InfluxDB.
    /// </summary>
    public static bool IsTracedHttpCall(HttpRequestMessage request)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        return !path.EndsWith("/getUpdates", StringComparison.Ordinal)
            && !path.EndsWith("/api/v2/write", StringComparison.Ordinal);
    }
}

/// <summary>
/// Läuft vor dem Exporter. Entfernt Query-Strings aus <c>url.full</c>,
/// schwärzt den Telegram-Bot-Token im Pfad (<c>/bot&lt;TOKEN&gt;/…</c>) und
/// verwirft erfolgreiche HTTP-Aufrufe ohne Eltern-Span: Das sind Hintergrund-
/// Abfragen (Arbeitszeit-Prüfung alle 5 Minuten pro Mitarbeiter) und
/// Benachrichtigungen, die die Stempel sonst übertönen. Fehlgeschlagene bleiben.
/// </summary>
public sealed partial class TelemetryRedactionProcessor : BaseProcessor<Activity>
{
    [GeneratedRegex(@"/bot[^/]+", RegexOptions.CultureInvariant)]
    private static partial Regex BotTokenPattern();

    public override void OnEnd(Activity data)
    {
        if (data.GetTagItem("url.full") is not string url) return;

        if (data.Kind == ActivityKind.Client && data.ParentSpanId == default && data.Status != ActivityStatusCode.Error)
        {
            // Nicht exportieren: der Exporter überspringt nicht aufgezeichnete
            // Spans. Der Status steht erst hier fest, ein Filter vorab geht nicht.
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
