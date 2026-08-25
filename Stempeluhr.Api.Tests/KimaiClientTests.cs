using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

/// <summary>
/// Regression tests for the transient-retry treatment of the timestamp
/// backdate PATCHes (review round on the offline PR): the begin-backdate in
/// <see cref="KimaiClient.StartAtAsync"/>'s old-Kimai fallback previously had
/// NO retry - one transient 5xx left the fresh timesheet running with
/// begin=now, which a later offline replay could never correct ("Lief
/// bereits"). Now both backdates share one retry loop, and a definitively
/// failed begin-backdate stops the misdated sheet so the replay can recreate
/// it with the intended begin.
/// </summary>
public sealed class KimaiClientTests
{
    private static readonly DateTimeOffset T08 = Parse("2026-08-24T08:00:00Z");

    private static RuntimeSettings Settings => new() { BaseUrl = "http://kimai.test" };

    private static EmployeeSettings Employee => new() { Id = "max", ApiToken = "token" };

    [Fact]
    public async Task BeginBackdate_FallbackRetriesTransientPatchFailures()
    {
        var handler = new ScriptedHandler(
            Resp(HttpStatusCode.BadRequest),                    // POST create WITH begin -> old Kimai rejects
            Resp(HttpStatusCode.OK, """{"id":7}"""),            // POST create WITHOUT begin
            Resp(HttpStatusCode.InternalServerError),           // PATCH begin -> transient
            Resp(HttpStatusCode.InternalServerError),           // PATCH begin -> transient
            Resp(HttpStatusCode.OK, "{}"));                     // PATCH begin -> success
        var client = CreateClient(handler);

        await client.StartAtAsync(Settings, Employee, 1, 1, T08);

        Assert.Equal(
        [
            "POST /api/timesheets?full=true",
            "POST /api/timesheets?full=true",
            "PATCH /api/timesheets/7",
            "PATCH /api/timesheets/7",
            "PATCH /api/timesheets/7",
        ], handler.Requests);
    }

    [Fact]
    public async Task BeginBackdate_DefinitiveFailure_StopsMisdatedTimesheet()
    {
        var handler = new ScriptedHandler(
            Resp(HttpStatusCode.BadRequest),
            Resp(HttpStatusCode.OK, """{"id":7}"""),
            Resp(HttpStatusCode.InternalServerError),
            Resp(HttpStatusCode.ServiceUnavailable),
            Resp(HttpStatusCode.BadGateway),                    // all 3 retry attempts fail
            Resp(HttpStatusCode.OK, "{}"));                     // compensating stop succeeds
        var client = CreateClient(handler);

        // The compensating stop succeeds, but the backdate failure is now
        // re-thrown so the sync loop buffers the event as transient and a
        // later replay can recreate the sheet with the intended begin.
        await Assert.ThrowsAnyAsync<Exception>(
            () => client.StartAtAsync(Settings, Employee, 1, 1, T08));

        // The last request must be the compensating stop of the misdated
        // sheet so a later replay can recreate it with the intended begin.
        Assert.Equal("PATCH /api/timesheets/7/stop", handler.Requests[^1]);
        Assert.Equal(6, handler.Requests.Count);
    }

    [Fact]
    public async Task EndBackdate_StillRetriesTransientFailures()
    {
        var handler = new ScriptedHandler(
            Resp(HttpStatusCode.OK, "{}"),                      // PATCH stop
            Resp(HttpStatusCode.InternalServerError),           // PATCH end -> transient
            Resp(HttpStatusCode.OK, "{}"));                     // PATCH end -> success
        var client = CreateClient(handler);

        await client.StopAtAsync(Settings, Employee, 42, T08);

        Assert.Equal(
        [
            "PATCH /api/timesheets/42/stop",
            "PATCH /api/timesheets/42",
            "PATCH /api/timesheets/42",
        ], handler.Requests);
    }

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);

    private static KimaiClient CreateClient(ScriptedHandler handler) =>
        new(new HttpClient(handler), NullLogger<KimaiClient>.Instance);

    private static HttpResponseMessage Resp(HttpStatusCode status, string? json = null)
    {
        var response = new HttpResponseMessage(status);
        if (json is not null)
        {
            response.Content = new StringContent(json);
        }

        return response;
    }

    /// <summary>Answers every request from a fixed script and records method + path.</summary>
    private sealed class ScriptedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _next;

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            var response = _next < responses.Length
                ? responses[_next++]
                : Resp(HttpStatusCode.InternalServerError);
            return Task.FromResult(response);
        }
    }
}
