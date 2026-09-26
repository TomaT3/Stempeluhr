using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;

namespace Stempeluhr.Api.Api;

public static class NfcEndpoints
{
    public static IEndpointRouteBuilder MapNfcEndpoints(this IEndpointRouteBuilder app)
    {
        // Latest card identification per terminal (published by
        // /api/kiosk/identify). The admin page shows it to assign a freshly
        // scanned card to an employee.
        app.MapGet("/api/nfc/events/latest", (
            string? terminalId,
            bool? fallbackToAny,
            INfcClockEventStore eventStore) =>
        {
            return Results.Ok(new NfcLatestEventDto(eventStore.GetLatest(terminalId, fallbackToAny == true)));
        });

        return app;
    }
}
