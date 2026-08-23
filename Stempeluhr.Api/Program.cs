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
// Throttles the unauthenticated kiosk sync endpoint (per-IP, fixed window).
builder.Services.AddSingleton(_ => new RequestRateLimiter(TimeSpan.FromSeconds(60), maxRequests: 20));
builder.Services.AddScoped<IClockService, ClockService>();
builder.Services.AddScoped<IAdminService, AdminService>();
builder.Services.AddHttpClient<IKimaiClient, KimaiClient>();

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

app.UseApiExceptionHandling();
app.UseCors("AngularDev");
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapApiEndpoints();
app.MapFallbackToFile("index.html");

app.Run();
