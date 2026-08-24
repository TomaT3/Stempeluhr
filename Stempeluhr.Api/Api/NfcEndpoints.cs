using System.Net;
using System.Security.Cryptography;
using System.Text;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;

namespace Stempeluhr.Api.Api;

public static class NfcEndpoints
{
    private const string ReaderTokenHeader = "X-Nfc-Reader-Token";

    /// <summary>
    /// Upper bound for events per sync batch. Every event costs at least one
    /// Kimai round trip under the global sync lock, so an unbounded batch
    /// could block all syncs and outbox flushes for a long time.
    /// </summary>
    private const int MaxSyncBatchSize = 100;

    public static IEndpointRouteBuilder MapNfcEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/nfc/clock", async (
            HttpRequest httpRequest,
            NfcClockRequest request,
            IConfiguration configuration,
            IClockService clockService,
            INfcClockEventStore eventStore,
            IOfflineEventIdStore eventIdStore,
            CancellationToken cancellationToken) =>
        {
            if (!IsReaderAuthorized(httpRequest, configuration))
            {
                return Results.Unauthorized();
            }

            // Idempotency for the live path: when the agent sends its eventId,
            // register it before applying. If the stamp is applied but the
            // response times out, the agent retries via the sync endpoint and
            // that replay now resolves as duplicate instead of toggling again.
            var registeredEventId = !string.IsNullOrWhiteSpace(request.EventId);
            if (registeredEventId && !eventIdStore.TryRegister(request.EventId!))
            {
                return Results.Conflict(new { message = "Event wurde bereits verarbeitet.", duplicate = true });
            }

            NfcClockEventDto clockEvent;
            try
            {
                clockEvent = await clockService.IdentifyWithNfcCardAsync(request, cancellationToken);
            }
            catch
            {
                // The stamp was not applied - free the ID so the agent's queued
                // event can be applied later by the sync replay.
                if (registeredEventId)
                {
                    eventIdStore.Remove(request.EventId!);
                }

                throw;
            }

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

            // Makes the KnownProxies setup verifiable from the logs: if the
            // logged client is still the tunnel/gateway IP instead of the real
            // device address, the trusted proxy list does not match yet.
            logger.LogInformation(
                "Kiosk clock sync from client {ClientIp} (X-Forwarded-For: {ForwardedFor}), {EventCount} event(s)",
                ip,
                httpRequest.Headers["X-Forwarded-For"].ToString() is { } forwarded && forwarded.Length > 0 ? forwarded : "-",
                request.Events?.Count ?? 0);

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

        app.MapPost("/api/nfc/clock/sync", async (
            HttpRequest httpRequest,
            OfflineSyncRequest request,
            IConfiguration configuration,
            IOfflineClockService offlineClockService,
            CancellationToken cancellationToken) =>
        {
            if (!IsReaderAuthorized(httpRequest, configuration))
            {
                return Results.Unauthorized();
            }

            if (request.Events is { Count: > MaxSyncBatchSize })
            {
                return Results.BadRequest(new { error = $"Too many events in one batch (max {MaxSyncBatchSize})." });
            }

            if (request.Events is { Count: > 0 })
            {
                var result = await offlineClockService.SyncAsync(request.Events, cancellationToken);
                return Results.Ok(result);
            }

            return Results.Ok(new OfflineSyncResultDto(0, 0, 0, Array.Empty<OfflineSyncEventResultDto>()));
        });

        app.MapGet("/api/nfc/events/latest", (
            string? terminalId,
            bool? fallbackToAny,
            INfcClockEventStore eventStore) =>
        {
            return Results.Ok(new NfcLatestEventDto(eventStore.GetLatest(terminalId, fallbackToAny == true)));
        });

        return app;
    }

    private static bool IsReaderAuthorized(HttpRequest request, IConfiguration configuration)
    {
        var configuredToken = configuration["Stempeluhr:NfcReaderToken"];
        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            return IsLoopback(request);
        }

        var providedToken = request.Headers[ReaderTokenHeader].ToString();
        return FixedTimeEquals(configuredToken.Trim(), providedToken.Trim());
    }

    private static bool IsLoopback(HttpRequest request)
    {
        var remoteIpAddress = request.HttpContext.Connection.RemoteIpAddress;
        return remoteIpAddress is not null && IPAddress.IsLoopback(remoteIpAddress);
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);

        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
