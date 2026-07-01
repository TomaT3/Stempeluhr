using System.Net;
using System.Security.Cryptography;
using System.Text;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;

namespace Stempeluhr.Api.Api;

public static class NfcEndpoints
{
    private const string ReaderTokenHeader = "X-Nfc-Reader-Token";

    public static IEndpointRouteBuilder MapNfcEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/nfc/clock", async (
            HttpRequest httpRequest,
            NfcClockRequest request,
            IConfiguration configuration,
            IClockService clockService,
            INfcClockEventStore eventStore,
            CancellationToken cancellationToken) =>
        {
            if (!IsReaderAuthorized(httpRequest, configuration))
            {
                return Results.Unauthorized();
            }

            var clockEvent = await clockService.IdentifyWithNfcCardAsync(request, cancellationToken);
            eventStore.Publish(clockEvent);

            return clockEvent.Success ? Results.Ok(clockEvent) : Results.BadRequest(clockEvent);
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
