using Microsoft.AspNetCore.Mvc;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;

namespace Stempeluhr.Api.Api;

/// <summary>
/// Abwesenheiten am Kiosk und auf /clock. Auth wie die Korrekturanträge:
/// <c>employeeId</c> plus PIN <b>oder</b> Karten-ID, Sperre nach falschen PINs
/// (<see cref="PinLockedException.Filter"/>) und ein eigenes IP-Limit. Keine
/// Offline-Queue: ist Kimai nicht erreichbar, antworten Absenden und Genehmigen
/// mit 503. Abwesenheiten sind keine Stempel - gebucht wird weiter nur über
/// <c>/api/kiosk/clock</c>.
/// </summary>
public static class AbsenceEndpoints
{
    public static IEndpointRouteBuilder MapAbsenceEndpoints(this IEndpointRouteBuilder app)
    {
        // Urlaubskonten, Zeitfenster je Art und belegte Tage für den Kalender.
        app.MapPost("/api/kiosk/absences/balance", async (
            HttpRequest httpRequest,
            CorrectionAuthRequest request,
            IAbsenceService absences,
            [FromKeyedServices(KioskRateLimiters.AbsenceKey)] RequestRateLimiter limiter,
            CancellationToken cancellationToken) =>
            TooManyRequests(httpRequest, limiter)
                ?? CorrectionEndpoints.ToResult(await absences.GetOverviewAsync(request, cancellationToken)))
            .AddEndpointFilter(PinLockedException.Filter);

        app.MapPost("/api/kiosk/absences", async (
            HttpRequest httpRequest,
            SubmitAbsenceRequest request,
            IAbsenceService absences,
            [FromKeyedServices(KioskRateLimiters.AbsenceKey)] RequestRateLimiter limiter,
            CancellationToken cancellationToken) =>
            TooManyRequests(httpRequest, limiter)
                ?? CorrectionEndpoints.ToResult(await absences.SubmitAsync(request, cancellationToken)))
            .AddEndpointFilter(PinLockedException.Filter);

        app.MapPost("/api/kiosk/absences/mine", async (
            HttpRequest httpRequest,
            CorrectionAuthRequest request,
            IAbsenceService absences,
            [FromKeyedServices(KioskRateLimiters.AbsenceKey)] RequestRateLimiter limiter,
            CancellationToken cancellationToken) =>
            TooManyRequests(httpRequest, limiter)
                ?? CorrectionEndpoints.ToResult(await absences.ListOwnAsync(request, cancellationToken)))
            .AddEndpointFilter(PinLockedException.Filter);

        app.MapPost("/api/kiosk/absences/{id}/withdraw", async (
            string id,
            HttpRequest httpRequest,
            CorrectionAuthRequest request,
            IAbsenceService absences,
            [FromKeyedServices(KioskRateLimiters.AbsenceKey)] RequestRateLimiter limiter,
            CancellationToken cancellationToken) =>
            TooManyRequests(httpRequest, limiter)
                ?? CorrectionEndpoints.ToResult(await absences.WithdrawAsync(request, id, cancellationToken)))
            .AddEndpointFilter(PinLockedException.Filter);

        return app;
    }

    private static IResult? TooManyRequests(HttpRequest httpRequest, RequestRateLimiter limiter)
    {
        var ip = httpRequest.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return limiter.TryAcquire(ip) ? null : Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }
}
