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
        // Pin the concrete transient exception type: a permanent error here
        // would be classified as "rejected" and lose the stamp instead.
        var thrown = await Assert.ThrowsAnyAsync<KimaiApiException>(
            () => client.StartAtAsync(Settings, Employee, 1, 1, T08));
        Assert.Equal(HttpStatusCode.BadGateway, thrown.StatusCode);

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

    [Fact]
    public async Task GetLatestStoppedTimesheetAsync_BuildsQueryWithoutUserFilter_AndParsesLatestEntry()
    {
        var handler = new ScriptedHandler(
            Resp(HttpStatusCode.OK, """
                [
                    {"id":9,"begin":"2026-08-28T07:00:00+02:00","end":"2026-08-28T11:30:00+02:00","duration":16200,"activity":{"id":5}}
                ]
                """));
        var client = CreateClient(handler);

        var latest = await client.GetLatestStoppedTimesheetAsync(Settings, Employee, CancellationToken.None);

        // No user filter: Kimai rejects user=me with 400 (requirements \d+|all),
        // the default is the token owner. The sort parameter is spelled order.
        Assert.Equal("GET /api/timesheets?size=1&orderBy=end&order=DESC&state=stopped", handler.Requests.Single());
        Assert.Equal(5, latest!.ActivityId);
        Assert.Equal(DateTimeOffset.Parse("2026-08-28T11:30:00+02:00"), latest.EndedAt);
    }

    [Fact]
    public async Task GetTimesheetsAsync_BuildsDateRangeQuery_AndParsesEntries()
    {
        var handler = new ScriptedHandler(
            Resp(HttpStatusCode.OK, """
                [
                    {"id":1,"begin":"2026-08-28T08:00:00+02:00","end":"2026-08-28T12:00:00+02:00","duration":14400,"activity":{"id":5}},
                    {"id":2,"begin":"2026-08-28T13:00:00+02:00","end":null,"duration":0,"activity":{"id":5}}
                ]
                """));
        var client = CreateClient(handler);

        var entries = await client.GetTimesheetsAsync(
            Settings, Employee,
            new DateTime(2026, 8, 28, 0, 0, 0),
            new DateTime(2026, 8, 28, 13, 30, 0));

        Assert.Equal("GET /api/timesheets?begin=2026-08-28T00:00:00&end=2026-08-28T13:30:00&size=500&page=1&orderBy=begin&order=ASC", handler.Requests.Single());
        Assert.Equal(2, entries.Count);
        Assert.Equal(14400, entries.ElementAt(0).DurationSeconds);
        Assert.Null(entries.ElementAt(1).End);
        Assert.Equal(5, entries.ElementAt(0).ActivityId);
    }

    [Fact]
    public async Task GetTimesheetsAsync_PaginatesPastFullPages()
    {
        var page1 = "[" + string.Join(",", Enumerable.Range(1, 500).Select(i =>
            $$$"""{"id":{{{i}}},"begin":"2026-08-01T08:00:00+02:00","end":"2026-08-01T12:00:00+02:00","duration":14400,"activity":{"id":5}}""")) + "]";
        var handler = new ScriptedHandler(
            Resp(HttpStatusCode.OK, page1),
            Resp(HttpStatusCode.OK, """[{"id":501,"begin":"2026-08-02T08:00:00+02:00","end":"2026-08-02T12:00:00+02:00","duration":14400,"activity":{"id":5}}]"""));
        var client = CreateClient(handler);

        var entries = await client.GetTimesheetsAsync(
            Settings, Employee,
            new DateTime(2026, 8, 1, 0, 0, 0),
            new DateTime(2026, 8, 31, 23, 59, 59));

        Assert.Equal(501, entries.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("page=1", handler.Requests[0]);
        Assert.Contains("page=2", handler.Requests[1]);
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
