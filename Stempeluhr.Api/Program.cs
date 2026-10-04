using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Stempeluhr.Api.Api;
using Stempeluhr.Api.Middleware;
using Stempeluhr.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IRuntimeSettingsStore, RuntimeSettingsStore>();
builder.Services.AddSingleton(sp => new RejectedOfflineEventStore(
    Path.Combine(DataPaths.Directory(builder.Configuration, builder.Environment), "rejected-offline-events.json"),
    sp.GetRequiredService<ILogger<RejectedOfflineEventStore>>()));
builder.Services.AddSingleton<IEmployeeService, EmployeeService>();
builder.Services.AddSingleton<IAdminAuthorizationService, AdminAuthorizationService>();
builder.Services.AddSingleton<INfcClockEventStore, NfcClockEventStore>();
builder.Services.AddSingleton<IOfflineEventIdStore>(sp => new FileOfflineEventIdStore(
    Path.Combine(DataPaths.Directory(builder.Configuration, builder.Environment), "offline-event-ids.json"),
    sp.GetRequiredService<ILogger<FileOfflineEventIdStore>>()));
// Singleton: the offline outbox (queues + sync lock) must outlive individual
// HTTP requests so events buffered during a Kimai outage survive and can be
// flushed by the background service below.
builder.Services.AddSingleton<IOfflineClockService, OfflineClockService>();
// Shared by the replay and the scoped live ClockService (issue #67).
builder.Services.AddSingleton<KioskEventCoordinator>();
builder.Services.AddHostedService<OfflineOutboxBackgroundService>();
// Separate throttles for the unauthenticated kiosk sync and identify endpoints.
builder.Services.AddKioskRateLimiters();
// Failed-PIN lock per employee (issue #8); shared by live and replay paths.
builder.Services.AddSingleton<PinAttemptGuard>();
builder.Services.AddScoped<IClockService, ClockService>();
builder.Services.AddScoped<IAdminService, AdminService>();
// Telegram-Notifier: Singleton + named HttpClient. Die Notify-Task läuft
// bewusst nach dem Request-Ende (fire-and-forget) - ein scope-gebundener
// Typed Client würde am Scope-Ende disposed und die Nachricht ginge in
// einem schmalen Race still verloren (ObjectDisposedException im catch).
builder.Services.AddHttpClient(TelegramNotifier.ClientName, client =>
{
    client.BaseAddress = new Uri("https://api.telegram.org");
    // Best effort: kurzer Timeout, damit ein Telegram-Ausfall nie den
    // Stempelvorgang blockiert (Notifier wirft ohnehin nie).
    client.Timeout = TimeSpan.FromSeconds(5);
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(2)
});
builder.Services.AddSingleton<ITelegramNotifier, TelegramNotifier>();
builder.Services.AddSingleton<OfflineRejectionNotifier>();
// Telegram warning after 6 h without a break or 10 h per shift.
builder.Services.AddSingleton(sp => new WorkTimeAlertStore(
    Path.Combine(DataPaths.Directory(builder.Configuration, builder.Environment), "work-time-alerts.json"),
    sp.GetRequiredService<ILogger<WorkTimeAlertStore>>()));
builder.Services.AddHostedService<WorkTimeAlertService>();
// Terminal monitoring: status page and Telegram alarm when a terminal stops reporting.
builder.Services.AddSingleton<TerminalHealthStore>();
builder.Services.AddHostedService<TerminalHealthWatcher>();
// Optional: terminal reports into InfluxDB for Grafana dashboards.
builder.Services.AddHttpClient(TerminalMetricsWriter.ClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(2)
});
builder.Services.AddSingleton<TerminalMetricsWriter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TerminalMetricsWriter>());
builder.Services.AddHttpClient<IKimaiClient, KimaiClient>(client =>
{
    // Every Kimai call runs under the global sync lock: one hung connection
    // (TCP black hole) must not freeze sync requests AND outbox flushes for
    // the HttpClient default timeout of 100 s per call.
    client.Timeout = TimeSpan.FromSeconds(15);
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    // The singleton OfflineClockService holds this typed client for the whole
    // process lifetime - without pool rotation a Kimai IP change (NAS/Docker
    // restart) would keep hitting the stale DNS entry until the API restarts.
    PooledConnectionLifetime = TimeSpan.FromMinutes(2)
});

// Behind a reverse proxy (Cloudflared/nginx on the NAS) every kiosk shares the
// proxy IP, which would make the kiosk sync rate limiter a single global
// budget. When trusted proxy IPs are configured, parse X-Forwarded-For so
// Connection.RemoteIpAddress becomes the real client IP again. Unset => direct
// exposure: no header trust, so XFF cannot be spoofed.
var knownProxies = builder.Configuration.GetSection("Stempeluhr:KnownProxies").Get<string[]>() ?? [];
if (knownProxies.Length > 0)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var proxy in knownProxies)
        {
            options.KnownProxies.Add(IPAddress.Parse(proxy));
        }
    });
}

builder.Services.AddCors(options =>
{
    options.AddPolicy("AngularDev", policy =>
    {
        policy
            .WithOrigins("http://localhost:4200", "https://localhost:4200")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

if (knownProxies.Length > 0)
{
    app.UseForwardedHeaders();
}

app.UseApiExceptionHandling();
app.UseCors("AngularDev");
// SPA-Cache-Strategie (StaticFileCachePolicy): nur gehashte Bundles sind
// immutable, alles andere (index.html, Service Worker, /pi/-Manifest) wird
// revalidiert. WICHTIG: dieselbe Logik gilt für UseStaticFiles UND den
// SPA-Fallback (MapFallbackToFile) — der Kiosk lädt die App über
// /terminal?terminalId=... und das ist ein Fallback-Pfad mit eigener
// StaticFile-Instanz.
// /pi/ liefert Installer und Agent-Bundle für die Terminals (siehe Dockerfile).
var contentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
contentTypes.Mappings[".sh"] = "text/x-shellscript";
contentTypes.Mappings[".gz"] = "application/gzip";

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypes,
    OnPrepareResponse = StaticFileCachePolicy.Apply,
});

app.MapApiEndpoints();
app.MapFallbackToFile("index.html", new StaticFileOptions { OnPrepareResponse = StaticFileCachePolicy.Apply });

app.Run();
