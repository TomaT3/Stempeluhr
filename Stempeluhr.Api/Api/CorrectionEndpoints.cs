using Microsoft.AspNetCore.Mvc;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;

namespace Stempeluhr.Api.Api;

/// <summary>
/// Korrekturanträge am Kiosk und auf /clock. Auth wie <c>/api/kiosk/clock</c>:
/// <c>employeeId</c> plus PIN <b>oder</b> Karten-ID, mit Sperre nach falschen
/// PINs (<see cref="PinLockedException.Filter"/>). Zusätzlich ein IP-Limit,
/// weil jeder Aufruf Kimai anfragt.
/// </summary>
public static class CorrectionEndpoints
{
    public static IEndpointRouteBuilder MapCorrectionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/kiosk/corrections/timesheets", async (
            HttpRequest httpRequest,
            CorrectionAuthRequest request,
            ITimeCorrectionService corrections,
            [FromKeyedServices(KioskRateLimiters.CorrectionKey)] RequestRateLimiter limiter,
            CancellationToken cancellationToken) =>
            TooManyRequests(httpRequest, limiter)
                ?? ToResult(await corrections.GetSelectableShiftsAsync(request, cancellationToken)))
            .AddEndpointFilter(PinLockedException.Filter);

        app.MapPost("/api/kiosk/corrections", async (
            HttpRequest httpRequest,
            SubmitCorrectionRequest request,
            ITimeCorrectionService corrections,
            [FromKeyedServices(KioskRateLimiters.CorrectionKey)] RequestRateLimiter limiter,
            CancellationToken cancellationToken) =>
            TooManyRequests(httpRequest, limiter)
                ?? ToResult(await corrections.SubmitAsync(request, cancellationToken)))
            .AddEndpointFilter(PinLockedException.Filter);

        app.MapPost("/api/kiosk/corrections/mine", async (
            HttpRequest httpRequest,
            CorrectionAuthRequest request,
            ITimeCorrectionService corrections,
            [FromKeyedServices(KioskRateLimiters.CorrectionKey)] RequestRateLimiter limiter,
            CancellationToken cancellationToken) =>
            TooManyRequests(httpRequest, limiter)
                ?? ToResult(await corrections.ListOwnAsync(request, cancellationToken)))
            .AddEndpointFilter(PinLockedException.Filter);

        app.MapPost("/api/kiosk/corrections/{id}/withdraw", async (
            string id,
            HttpRequest httpRequest,
            CorrectionAuthRequest request,
            ITimeCorrectionService corrections,
            [FromKeyedServices(KioskRateLimiters.CorrectionKey)] RequestRateLimiter limiter,
            CancellationToken cancellationToken) =>
            TooManyRequests(httpRequest, limiter)
                ?? ToResult(await corrections.WithdrawAsync(request, id, cancellationToken)))
            .AddEndpointFilter(PinLockedException.Filter);

        return app;
    }

    /// <summary>Gemeinsame Abbildung der Service-Ergebnisse auf HTTP (auch für die Admin-Endpunkte).</summary>
    internal static IResult ToResult<T>(CorrectionResult<T> result) => result.Outcome switch
    {
        CorrectionOutcome.Ok => Results.Ok(result.Value),
        CorrectionOutcome.Unauthorized => Results.Unauthorized(),
        CorrectionOutcome.NotFound => Results.NotFound(new { message = result.Message }),
        CorrectionOutcome.Conflict => Results.Conflict(new { message = result.Message }),
        CorrectionOutcome.Unavailable => Results.Json(new { message = result.Message }, statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.BadRequest(new { message = result.Message }),
    };

    // The endpoints are unauthenticated for the kiosk (employee ID + PIN/card
    // are the credentials) and every call hits Kimai: throttle per client IP
    // like the other kiosk endpoints. The real client IP requires
    // Stempeluhr:KnownProxies (see Program.cs).
    private static IResult? TooManyRequests(HttpRequest httpRequest, RequestRateLimiter limiter)
    {
        var ip = httpRequest.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return limiter.TryAcquire(ip) ? null : Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }
}
