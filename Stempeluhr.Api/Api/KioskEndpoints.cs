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

        return app;
    }
}
