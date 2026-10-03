using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

/// <summary>
/// Offline starts must be created with the real timestamp or fail without
/// a replacement booking at now. End-backdate PATCHes retain their retry.
/// </summary>
public sealed class KimaiClientTests
{
    private static readonly DateTimeOffset T08 = Parse("2026-08-24T08:00:00Z");

    private static RuntimeSettings Settings => new() { BaseUrl = "http://kimai.test" };

    private static EmployeeSettings Employee => new() { Id = "max", ApiToken = "token" };
    private static readonly KimaiTimesheetTarget Target = new(1, 1, "Stempeluhr", true, null, null);

    [Fact]
    public async Task StartAt_CreatesWithCapturedTimestamp()
    {
        var handler = new ScriptedHandler(Resp(HttpStatusCode.OK, """{"id":7}"""));
        var client = CreateClient(handler);

        await client.StartAtAsync(Settings, Employee, Target, T08);

        Assert.Equal("POST /api/timesheets?full=true", Assert.Single(handler.Requests));
        using var body = System.Text.Json.JsonDocument.Parse(Assert.Single(handler.Bodies)!);
        Assert.Equal(T08, DateTimeOffset.Parse(body.RootElement.GetProperty("begin").GetString()!));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task StartAt_RejectionNeverCreatesAtNow(HttpStatusCode status)
    {
        var handler = new ScriptedHandler(Resp(status, "{}"));
        var client = CreateClient(handler);

        var thrown = await Assert.ThrowsAnyAsync<KimaiApiException>(
            () => client.StartAtAsync(Settings, Employee, Target, T08));
        Assert.Equal(status, thrown.StatusCode);
        Assert.Equal("POST /api/timesheets?full=true", Assert.Single(handler.Requests));
    }

    [Fact]
    public async Task StartAt_TimeClockFormExplainsForbiddenBegin()
    {
        const string details = """{"code":400,"errors":{"errors":["This form should not contain extra fields."],"children":{"project":{},"activity":{},"description":{},"user":{},"tags":{},"billable":{}}}}""";
        var handler = new ScriptedHandler(Resp(HttpStatusCode.BadRequest, details));
        var error = await Assert.ThrowsAsync<KimaiApiException>(() => CreateClient(handler).StartAtAsync(Settings, Employee, Target, T08));
        Assert.Equal(new[] { "begin" }, error.RejectedFields);
        Assert.Contains("Erfassungsmodus", error.Message);
        Assert.DoesNotContain("edit_billable", error.Message);
        Assert.Equal(details, error.Details);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{}")]
    [InlineData("{\"errors\":null}")]
    public async Task StartAt_UnrecognizedErrorPreservesKimaiDetails(string details)
    {
        var handler = new ScriptedHandler(Resp(HttpStatusCode.BadRequest, details));
        var error = await Assert.ThrowsAsync<KimaiApiException>(() => CreateClient(handler).StartAtAsync(Settings, Employee, Target, T08));
        Assert.Equal(details, error.Details);
        Assert.Empty(error.RejectedFields);
        Assert.Single(handler.Requests);
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
    public async Task GetRecentStoppedTimesheetsAsync_BuildsQueryWithoutUserFilter_AndParsesEntries()
    {
        var handler = new ScriptedHandler(
            Resp(HttpStatusCode.OK, """
                [
                    {"id":9,"begin":"2026-08-28T11:00:00+02:00","end":"2026-08-28T11:30:00+02:00","duration":1800,"activity":{"id":5},"project":{"id":1}},
                    {"id":8,"begin":"2026-08-28T07:00:00+02:00","end":"2026-08-28T11:00:00+02:00","duration":14400,"activity":{"id":21},"project":{"id":20}}
                ]
                """));
        var client = CreateClient(handler);

        var recent = await client.GetRecentStoppedTimesheetsAsync(Settings, Employee, 2, CancellationToken.None);

        // No user filter: Kimai rejects user=me with 400 (requirements \d+|all),
        // the default is the token owner. The sort parameter is spelled order.
        Assert.Equal("GET /api/timesheets?size=2&orderBy=end&order=DESC&state=stopped", handler.Requests.Single());
        Assert.Equal(2, recent.Count);
        Assert.Equal(5, recent[0].ActivityId);
        Assert.Equal(DateTimeOffset.Parse("2026-08-28T11:30:00+02:00"), recent[0].EndedAt);
        Assert.Equal((20, 21), (recent[1].ProjectId, recent[1].ActivityId));
        // The id lets the offline replay recognise the sheet it stopped itself.
        Assert.Equal((9, 8), (recent[0].Id, recent[1].Id));
    }

    [Fact]
    public async Task GetCurrentUserTimezoneAsync_ParsesTimezone()
    {
        var handler = new ScriptedHandler(
            Resp(HttpStatusCode.OK, """{"id":1,"timezone":"Europe/Berlin"}"""));
        var client = CreateClient(handler);

        var timezone = await client.GetCurrentUserTimezoneAsync(Settings, Employee);

        Assert.Equal("Europe/Berlin", timezone);
        Assert.Equal("GET /api/users/me", handler.Requests.Single());
    }

    [Fact]
    public async Task GetCurrentUserTimezoneAsync_KimaiError_ReturnsNull()
    {
        var handler = new ScriptedHandler(Resp(HttpStatusCode.InternalServerError));
        var client = CreateClient(handler);

        var timezone = await client.GetCurrentUserTimezoneAsync(Settings, Employee);

        Assert.Null(timezone);
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
        public List<string?> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            var response = _next < responses.Length
                ? responses[_next++]
                : Resp(HttpStatusCode.InternalServerError);
            return response;
        }
    }
}
