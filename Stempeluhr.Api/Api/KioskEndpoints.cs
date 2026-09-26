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
        app.MapPost("/api/kiosk/pin-login", async (
            KioskPinLoginRequest request,
            IClockService clockService,
            CancellationToken cancellationToken) =>
        {
            var session = await clockService.LoginWithPinAsync(request.Pin, cancellationToken);
            return session is null ? Results.Unauthorized() : Results.Ok(session);
        });

        app.MapPost("/api/kiosk/hours", async (
            KioskPinLoginRequest request,
            IClockService clockService,
            CancellationToken cancellationToken) =>
        {
            var hours = await clockService.GetHoursOverviewAsync(request.Pin, cancellationToken);
            return hours is null ? Results.Unauthorized() : Results.Ok(hours);
        });

        app.MapPost("/api/kiosk/clock", async (
            KioskClockRequest request,
            IClockService clockService,
            CancellationToken cancellationToken) =>
        {
            var status = await clockService.ClockAsync(request, cancellationToken);

            return status.Result switch
            {
                ClockActionResult.Unauthorized => Results.Unauthorized(),
                ClockActionResult.BadRequest => Results.BadRequest(new { message = "Unbekannte Stempelaktion." }),
                _ => Results.Ok(status.Status)
            };
        });

        app.MapPost("/api/kiosk/identify", async (
            HttpRequest httpRequest,
            KioskIdentifyRequest request,
            IClockService clockService,
            INfcClockEventStore eventStore,
            RequestRateLimiter kioskIdentifyRateLimiter,
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
            RequestRateLimiter kioskSyncRateLimiter,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            var ip = httpRequest.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            // The kiosk sync endpoint accepts arbitrary performedAt timestamps
            // and is only protected by the employee PIN, so it is an attractive
            // brute-force target. Throttle per client IP (the real client IP
            // requires trusted reverse proxies to be configured via
            // Stempeluhr:KnownProxies - otherwise every kiosk behind the proxy
            // shares one budget); real auth (terminal token) is tracked as a
            // follow-up.
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

            if (request.Events is { Count: > MaxSyncBatchSize })
            {
                return Results.BadRequest(new { error = $"Too many events in one batch (max {MaxSyncBatchSize})." });
            }

            if (request.Events is { Count: > 0 })
            {
                var result = await offlineClockService.SyncKioskAsync(request.Events, cancellationToken);
                return Results.Ok(result);
            }

            return Results.Ok(new OfflineSyncResultDto(0, 0, 0, Array.Empty<OfflineSyncEventResultDto>()));
        });

        return app;
    }
}
