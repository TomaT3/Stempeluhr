using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;

namespace Stempeluhr.Api.Api;

public static class ClockEndpoints
{
    public static IEndpointRouteBuilder MapClockEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/clock/status", async (
            ClockRequest request,
            IClockService clockService,
            CancellationToken cancellationToken) =>
        {
            var status = await clockService.GetStatusAsync(request, cancellationToken);
            return status is null ? Results.Unauthorized() : Results.Ok(status);
        }).AddEndpointFilter(PinLockedException.Filter);

        app.MapPost("/api/clock/start", async (
            ClockRequest request,
            IClockService clockService,
            CancellationToken cancellationToken) =>
        {
            var status = await clockService.StartAsync(request, cancellationToken);
            return status is null ? Results.Unauthorized() : Results.Ok(status);
        }).AddEndpointFilter(PinLockedException.Filter);

        app.MapPost("/api/clock/stop", async (
            ClockRequest request,
            IClockService clockService,
            CancellationToken cancellationToken) =>
        {
            var status = await clockService.StopAsync(request, cancellationToken);
            return status is null ? Results.Unauthorized() : Results.Ok(status);
        }).AddEndpointFilter(PinLockedException.Filter);

        return app;
    }
}
