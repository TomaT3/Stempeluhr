using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;

namespace Stempeluhr.Api.Api;

public static class KioskEndpoints
{
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

            return clockEvent.Success ? Results.Ok(clockEvent) : Results.BadRequest(clockEvent);
        });

        return app;
    }
}
