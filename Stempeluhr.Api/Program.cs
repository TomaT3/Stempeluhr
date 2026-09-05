using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Stempeluhr.Api.Api;
using Stempeluhr.Api.Middleware;
using Stempeluhr.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IRuntimeSettingsStore, RuntimeSettingsStore>();
builder.Services.AddSingleton<IEmployeeService, EmployeeService>();
builder.Services.AddSingleton<IAdminAuthorizationService, AdminAuthorizationService>();
builder.Services.AddSingleton<INfcClockEventStore, NfcClockEventStore>();
builder.Services.AddSingleton<IOfflineEventIdStore>(sp => new FileOfflineEventIdStore(
    Path.Combine(builder.Environment.ContentRootPath, "data", "offline-event-ids.json"),
    sp.GetRequiredService<ILogger<FileOfflineEventIdStore>>()));
// Singleton: the offline outbox (queues + sync lock) must outlive individual
// HTTP requests so events buffered during a Kimai outage survive and can be
// flushed by the background service below.
builder.Services.AddSingleton<IOfflineClockService, OfflineClockService>();
builder.Services.AddHostedService<OfflineOutboxBackgroundService>();
// Throttles the unauthenticated kiosk sync endpoint (per client IP, fixed
// window; real per-client IPs require Stempeluhr:KnownProxies - see above).
builder.Services.AddSingleton(_ => new RequestRateLimiter(TimeSpan.FromSeconds(60), maxRequests: 20));
// Separate limiter for the unauthenticated kiosk identify endpoint. More
// generous than the sync limiter: a shift change can scan many cards in a
// minute, but 60/min still caps brute-forcing card ids (4-byte UIDs) and
// protects Kimai from a request flood (each identify hits GetStatusAsync).
builder.Services.AddSingleton(_ => new RequestRateLimiter(TimeSpan.FromSeconds(60), maxRequests: 60));
builder.Services.AddScoped<IClockService, ClockService>();
builder.Services.AddScoped<IAdminService, AdminService>();
builder.Services.AddHttpClient<ITelegramNotifier, TelegramNotifier>(client =>
{
    client.BaseAddress = new Uri("https://api.telegram.org");
    // Best effort: kurzer Timeout, damit ein Telegram-Ausfall nie den
    // Stempelvorgang blockiert (Notifier wirft ohnehin nie).
    client.Timeout = TimeSpan.FromSeconds(5);
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(2)
});
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
// SPA-Cache-Strategie: index.html IMMER frisch validieren (no-cache) — der
// Kiosk bekommt nach jedem Deploy die neue App statt der alten Cache-Kopie.
// Die gehashten Bundles (main-*.js, styles-*.css) ändern ihren Namen bei
// jedem Build und sind immutable cachebar.
// WICHTIG: dieselbe Logik gilt für UseStaticFiles UND den SPA-Fallback
// (MapFallbackToFile) — der Kiosk lädt die App über /terminal?terminalId=...
// und das ist ein Fallback-Pfad mit eigener StaticFile-Instanz.
void ApplyCacheHeaders(Microsoft.AspNetCore.StaticFiles.StaticFileResponseContext context)
{
    var headers = context.Context.Response.Headers;
    if (string.Equals(context.File.Name, "index.html", StringComparison.OrdinalIgnoreCase))
    {
        headers.CacheControl = "no-cache";
    }
    else
    {
        headers.CacheControl = "public, max-age=31536000, immutable";
    }
}

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = ApplyCacheHeaders });

app.MapApiEndpoints();
app.MapFallbackToFile("index.html", new StaticFileOptions { OnPrepareResponse = ApplyCacheHeaders });

app.Run();
