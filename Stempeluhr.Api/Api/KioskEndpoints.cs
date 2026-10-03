using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;

namespace Stempeluhr.Api.Api;

public static class KioskEndpoints
{
    /// <summary>
    /// Upper bound for events per sync batch. Every event costs at least one
    /// Kimai round trip under the global sync lock, so an unbounded batch
    /// could block all syncs and outbox flushes for a long time.
    /// </summary>
    private const int MaxSyncBatchSize = 100;

    public static IEndpointRouteBuilder MapKioskEndpoints(this IEndpointRouteBuilder app)
    {
        var diagnosticLimiter = new RequestRateLimiter(TimeSpan.FromMinutes(1), 2);
        app.MapPost("/api/kiosk/diagnostics", (HttpContext context, JsonElement report,
            IRuntimeSettingsStore store, ILogger<Program> logger) =>
        {
            var terminalId = TerminalAuthentication.Authenticate(context.Request, store.Load());
            if (terminalId is null) return Results.Unauthorized();
            if (!diagnosticLimiter.TryAcquire(terminalId)) return Results.StatusCode(429);
            if (report.ValueKind != JsonValueKind.Object) return Results.BadRequest();
            logger.LogInformation("Terminal {TerminalId} diagnostics: {Report}",
                terminalId, JsonSerializer.Serialize(TerminalDiagnostics.Filter(report)));
            return Results.Ok(new { ok = true });
        }).WithMetadata(new RequestSizeLimitAttribute(32768));

        app.MapGet("/api/kiosk/catalog", (HttpContext context, IRuntimeSettingsStore store, IEmployeeService employees) =>
        {
            var settings = store.Load();
            if (TerminalAuthentication.Authenticate(context.Request, settings) is null)
                return Results.Unauthorized();
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(TerminalAuthentication.Catalog(settings, employees));
        });

        app.MapPost("/api/kiosk/pin-login", async (
            KioskPinLoginRequest request,
            IClockService clockService,
            CancellationToken cancellationToken) =>
        {
            var session = await clockService.LoginWithPinAsync(request.Pin, cancellationToken);
            return session is null ? Results.Unauthorized() : Results.Ok(session);
        }).AddEndpointFilter(PinLockedException.Filter);

        app.MapPost("/api/kiosk/hours", async (
            KioskPinLoginRequest request,
            IClockService clockService,
            CancellationToken cancellationToken) =>
        {
            var hours = await clockService.GetHoursOverviewAsync(request.Pin, cancellationToken);
            return hours is null ? Results.Unauthorized() : Results.Ok(hours);
        }).AddEndpointFilter(PinLockedException.Filter);

        app.MapPost("/api/kiosk/clock", async (
            KioskClockRequest request,
            IClockService clockService,
            CancellationToken cancellationToken) =>
        {
            var status = await clockService.ClockAsync(request, cancellationToken);

            return status.Result switch
            {
                ClockActionResult.Unauthorized => Results.Unauthorized(),
                ClockActionResult.BadRequest => Results.BadRequest(new { message = "Unbekannte Stempelaktion oder Taetigkeit." }),
                _ => Results.Ok(status.Status)
            };
        }).AddEndpointFilter(PinLockedException.Filter);

        app.MapPost("/api/kiosk/identify", async (
            HttpRequest httpRequest,
            KioskIdentifyRequest request,
            IClockService clockService,
            INfcClockEventStore eventStore,
            [FromKeyedServices(KioskRateLimiters.IdentifyKey)] RequestRateLimiter kioskIdentifyRateLimiter,
            CancellationToken cancellationToken) =>
        {
            // The endpoint is unauthenticated (the kiosk has no token) and
            // every call hits Kimai (GetStatusAsync) - throttle per client IP
            // like the kiosk sync endpoint. The real client IP requires
            // Stempeluhr:KnownProxies to be configured (see Program.cs).
            var ip = httpRequest.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!kioskIdentifyRateLimiter.TryAcquire(ip))
            {
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            // Resolves a scanned card id to an employee WITHOUT stamping.
            // The kiosk uses this on its local-scan path when the card is not
            // in its local cache yet; the result is cached client-side so
            // later scans work offline too. The NfcClockRequest.Action is
            // irrelevant for identification and deliberately not set.
            var clockEvent = await clockService.IdentifyWithNfcCardAsync(
                new NfcClockRequest(request.CardId, null, request.TerminalId),
                cancellationToken);

            // The admin page shows the latest scan per terminal - also for
            // unknown cards, which is exactly how a new card gets assigned.
            eventStore.Publish(clockEvent);

            return clockEvent.Success ? Results.Ok(clockEvent) : Results.BadRequest(clockEvent);
        });

        app.MapPost("/api/kiosk/clock/sync", async (
            HttpRequest httpRequest,
            OfflineKioskSyncRequest request,
            IOfflineClockService offlineClockService,
            IRuntimeSettingsStore settingsStore,
            [FromKeyedServices(KioskRateLimiters.SyncKey)] RequestRateLimiter kioskSyncRateLimiter,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            var ip = httpRequest.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            // The legacy kiosk sync endpoint accepts arbitrary performedAt timestamps
            // and is only protected by the employee PIN, so it is an attractive
            // brute-force target. Throttle per client IP (the real client IP
            // requires trusted reverse proxies to be configured via
            // Stempeluhr:KnownProxies - otherwise every kiosk behind the proxy
            // shares one budget). Terminal-authenticated replay uses the same limit.
            if (!kioskSyncRateLimiter.TryAcquire(ip))
            {
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            // Makes the KnownProxies setup verifiable from the logs. Deliberately
            // AFTER the rate limiter (limited floods produce no log lines) and
            // with only the first, length-capped XFF segment - the raw header is
            // client-controlled and must not be able to flood or poison logs.
            // Once KnownProxies is verified in production this line can drop to
            // Debug level or be removed entirely.
            var forwarded = httpRequest.Headers["X-Forwarded-For"].ToString();
            var forwardedFirst = forwarded.Split(',')[0].Trim();
            if (forwardedFirst.Length > 64)
            {
                forwardedFirst = forwardedFirst[..64];
            }

            logger.LogInformation(
                "Kiosk clock sync from client {ClientIp} (X-Forwarded-For: {ForwardedFor}), {EventCount} event(s)",
                ip,
                forwardedFirst.Length == 0 ? "-" : forwardedFirst,
                request.Events?.Count ?? 0);

            var hasTerminalAuth = TerminalAuthentication.IsTerminalRequest(httpRequest);
            var terminalId = hasTerminalAuth
                ? TerminalAuthentication.Authenticate(httpRequest, settingsStore.Load()) : null;
            if (hasTerminalAuth && terminalId is null) return Results.Unauthorized();

            if (request.Events is { Count: > MaxSyncBatchSize })
            {
                return Results.BadRequest(new { error = $"Too many events in one batch (max {MaxSyncBatchSize})." });
            }

            if (request.Events is { Count: > 0 })
            {
                var events = terminalId is null ? request.Events : request.Events.Select(e => e with
                { AuthenticatedTerminalId = terminalId, Pin = null, NfcCardId = null }).ToArray();
                var result = await offlineClockService.SyncKioskAsync(events, cancellationToken);
                return Results.Ok(result);
            }

            return Results.Ok(new OfflineSyncResultDto(0, 0, 0, Array.Empty<OfflineSyncEventResultDto>()));
        });

        return app;
    }
}
