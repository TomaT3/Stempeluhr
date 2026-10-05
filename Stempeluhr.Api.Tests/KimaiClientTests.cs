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

    [Theory]
    [InlineData("This form should not contain extra fields.")]
    [InlineData("Dieses Formular sollte keine zusätzlichen Felder enthalten.")]
    public async Task StartAt_TimeClockFormExplainsForbiddenBegin(string message)
    {
        var details = """{"code":400,"errors":{"errors":["MESSAGE"],"children":{"project":{},"activity":{},"description":{},"user":{},"tags":{},"billable":{}}}}""".Replace("MESSAGE", message);
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
            Resp(HttpStatusCode.InternalServerError),           // PATCH end -> transient
            Resp(HttpStatusCode.OK, "{}"));                     // PATCH end -> success
        var client = CreateClient(handler);

        await client.StopAtAsync(Settings, Employee, 42, T08);

        Assert.Equal(
        [
            "PATCH /api/timesheets/42",
            "PATCH /api/timesheets/42",
        ], handler.Requests);
        Assert.All(handler.Bodies, json =>
        {
            using var body = System.Text.Json.JsonDocument.Parse(json!);
            Assert.Equal(T08, DateTimeOffset.Parse(body.RootElement.GetProperty("end").GetString()!));
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task StopAt_RejectionNeverStopsAtNow(HttpStatusCode status)
    {
        const string details = """{"errors":{"errors":["Dieses Formular sollte keine zusätzlichen Felder enthalten."],"children":{"project":{},"activity":{}}}}""";
        var handler = new ScriptedHandler(Resp(status, details));
        var error = await Assert.ThrowsAsync<KimaiApiException>(() => CreateClient(handler).StopAtAsync(Settings, Employee, 42, T08));
        Assert.Equal("PATCH /api/timesheets/42", Assert.Single(handler.Requests));
        if (status == HttpStatusCode.BadRequest)
        {
            Assert.Equal(new[] { "end" }, error.RejectedFields);
            Assert.Contains("Erfassungsmodus", error.Message);
        }
        else Assert.Empty(error.RejectedFields);
    }

    [Fact]
    public async Task StartAt_InvalidAllowedBeginIsNotAnUnsupportedField()
    {
        const string details = """{"errors":{"errors":["Ungültiger Zeitpunkt."],"children":{"project":{},"activity":{},"description":{},"tags":{},"billable":{},"begin":{"errors":["Ungültiger Zeitpunkt."]}}}}""";
        var handler = new ScriptedHandler(Resp(HttpStatusCode.BadRequest, details));
        var error = await Assert.ThrowsAsync<KimaiApiException>(() => CreateClient(handler).StartAtAsync(Settings, Employee, Target, T08));
        Assert.Empty(error.RejectedFields);
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

    [Fact]
    public async Task GetTimesheetAsync_ReadsSingleTimesheet()
    {
        var handler = new ScriptedHandler(Resp(HttpStatusCode.OK,
            """{"id":42,"begin":"2026-08-28T08:00:00+02:00","end":"2026-08-28T16:00:00+02:00","activity":5,"project":{"id":3},"description":"Schicht","billable":false,"user":{"id":7}}"""));

        var sheet = await CreateClient(handler).GetTimesheetAsync(Settings, Employee, 42);

        Assert.Equal("GET /api/timesheets/42", Assert.Single(handler.Requests));
        Assert.NotNull(sheet);
        Assert.Equal(42, sheet.Id);
        Assert.Equal(DateTimeOffset.Parse("2026-08-28T08:00:00+02:00"), sheet.Begin);
        Assert.Equal(DateTimeOffset.Parse("2026-08-28T16:00:00+02:00"), sheet.End);
        Assert.Equal((5, 3, "Schicht", false), (sheet.ActivityId, sheet.ProjectId, sheet.Description, sheet.Billable));
        Assert.Equal(7, sheet.UserId);
    }

    [Fact]
    public async Task GetTimesheetAsync_ReturnsForeignOwner_SoCallersCanEnforceOwnership()
    {
        // Kimai answers a successful GET for another user's sheet if the token
        // may view it; the client must not hide that - UserId lets #96 reject it.
        var handler = new ScriptedHandler(Resp(HttpStatusCode.OK,
            """{"id":50,"begin":"2026-08-28T08:00:00+02:00","end":"2026-08-28T16:00:00+02:00","activity":5,"project":3,"user":12}"""));

        var sheet = await CreateClient(handler).GetTimesheetAsync(Settings, Employee, 50);

        Assert.NotNull(sheet);
        Assert.Equal(12, sheet.UserId);
    }

    [Fact]
    public async Task GetTimesheetAsync_RunningSheetHasNoEnd()
    {
        var handler = new ScriptedHandler(Resp(HttpStatusCode.OK,
            """{"id":43,"begin":"2026-08-28T08:00:00+02:00","end":null,"activity":{"id":5},"project":{"id":3}}"""));

        var sheet = await CreateClient(handler).GetTimesheetAsync(Settings, Employee, 43);

        Assert.NotNull(sheet);
        Assert.Null(sheet.End);
        Assert.Null(sheet.Description);
        Assert.True(sheet.Billable);
    }

    [Fact]
    public async Task GetTimesheetAsync_NotFound_ReturnsNull()
    {
        var handler = new ScriptedHandler(Resp(HttpStatusCode.NotFound, """{"code":404,"message":"Not Found"}"""));

        Assert.Null(await CreateClient(handler).GetTimesheetAsync(Settings, Employee, 99));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetTimesheetAsync_OtherErrorsStillThrow()
    {
        var handler = new ScriptedHandler(Resp(HttpStatusCode.Forbidden, "{}"));

        var error = await Assert.ThrowsAsync<KimaiApiException>(() => CreateClient(handler).GetTimesheetAsync(Settings, Employee, 42));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    [Fact]
    public async Task CreateTimesheet_SendsBeginAndEnd_AndReturnsId()
    {
        var handler = new ScriptedHandler(Resp(HttpStatusCode.OK, """{"id":77}"""));
        var end = T08.AddHours(4);

        var id = await CreateClient(handler).CreateTimesheetAsync(Settings, new EmployeeSettings { Id = "max", ApiToken = "token", Tags = ["a", "b"] }, Target, T08, end, "Rest");

        Assert.Equal(77, id);
        Assert.Equal("POST /api/timesheets?full=true", Assert.Single(handler.Requests));
        using var body = System.Text.Json.JsonDocument.Parse(Assert.Single(handler.Bodies)!);
        var root = body.RootElement;
        Assert.Equal("2026-08-24T08:00:00+00:00", root.GetProperty("begin").GetString());
        Assert.Equal("2026-08-24T12:00:00+00:00", root.GetProperty("end").GetString());
        Assert.Equal((1, 1, "Rest", "a,b", true),
            (root.GetProperty("project").GetInt32(), root.GetProperty("activity").GetInt32(),
             root.GetProperty("description").GetString(), root.GetProperty("tags").GetString(), root.GetProperty("billable").GetBoolean()));
    }

    [Fact]
    public async Task CreateTimesheet_IsNeverRetried()
    {
        var handler = new ScriptedHandler(Resp(HttpStatusCode.InternalServerError), Resp(HttpStatusCode.OK, """{"id":1}"""));

        await Assert.ThrowsAsync<KimaiApiException>(
            () => CreateClient(handler).CreateTimesheetAsync(Settings, Employee, Target, T08, T08.AddHours(1), null));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CreateTimesheet_ForbiddenEndExplainsTrackingMode()
    {
        const string details = """{"errors":{"errors":["Dieses Formular sollte keine zusätzlichen Felder enthalten."],"children":{"project":{},"activity":{},"description":{},"tags":{},"billable":{}}}}""";
        var handler = new ScriptedHandler(Resp(HttpStatusCode.BadRequest, details));

        var error = await Assert.ThrowsAsync<KimaiApiException>(
            () => CreateClient(handler).CreateTimesheetAsync(Settings, Employee, Target, T08, T08.AddHours(1), null));

        Assert.Equal(new[] { "begin", "end" }, error.RejectedFields);
        Assert.Contains("Erfassungsmodus", error.Message);
    }

    [Fact]
    public async Task UpdateTimesheetTimes_PatchesOnlyTheGivenFields()
    {
        var handler = new ScriptedHandler(Resp(HttpStatusCode.OK, "{}"), Resp(HttpStatusCode.OK, "{}"), Resp(HttpStatusCode.OK, "{}"));
        var client = CreateClient(handler);

        await client.UpdateTimesheetTimesAsync(Settings, Employee, 42, T08, null);
        await client.UpdateTimesheetTimesAsync(Settings, Employee, 42, null, T08.AddHours(8));
        await client.UpdateTimesheetTimesAsync(Settings, Employee, 42, T08, T08.AddHours(8));

        Assert.All(handler.Requests, request => Assert.Equal("PATCH /api/timesheets/42", request));
        // Compare parsed fields: the raw JSON escapes '+' as +.
        var sent = handler.Bodies.Select(json =>
        {
            using var body = System.Text.Json.JsonDocument.Parse(json!);
            return body.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        }).ToList();
        const string begin = "2026-08-24T08:00:00+00:00";
        const string end = "2026-08-24T16:00:00+00:00";
        Assert.Equal([("begin", begin)], sent[0].Select(p => (p.Key, p.Value!)));
        Assert.Equal([("end", end)], sent[1].Select(p => (p.Key, p.Value!)));
        Assert.Equal([("begin", begin), ("end", end)], sent[2].OrderBy(p => p.Key).Select(p => (p.Key, p.Value!)));
    }

    [Fact]
    public async Task UpdateTimesheetTimes_WithoutFieldsThrowsBeforeAnyRequest()
    {
        var handler = new ScriptedHandler();

        await Assert.ThrowsAsync<ArgumentException>(() => CreateClient(handler).UpdateTimesheetTimesAsync(Settings, Employee, 42, null, null));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UpdateTimesheetTimes_RetriesTransientFailuresOnce_NotTwice()
    {
        var handler = new ScriptedHandler(
            Resp(HttpStatusCode.InternalServerError),
            Resp(HttpStatusCode.OK, "{}"));

        await CreateClient(handler).UpdateTimesheetTimesAsync(Settings, Employee, 42, T08, null);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task UpdateTimesheetTimes_RejectedBeginExplainsTrackingMode()
    {
        const string details = """{"errors":{"errors":["Dieses Formular sollte keine zusätzlichen Felder enthalten."],"children":{"project":{},"activity":{}}}}""";
        var handler = new ScriptedHandler(Resp(HttpStatusCode.BadRequest, details));

        var error = await Assert.ThrowsAsync<KimaiApiException>(
            () => CreateClient(handler).UpdateTimesheetTimesAsync(Settings, Employee, 42, T08, null));

        Assert.Equal(new[] { "begin" }, error.RejectedFields);
        Assert.Contains("Erfassungsmodus", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetTimesheetsAsync_ParsesProjectAndDescription()
    {
        var handler = new ScriptedHandler(Resp(HttpStatusCode.OK,
            """[{"id":1,"begin":"2026-08-28T08:00:00+02:00","end":"2026-08-28T12:00:00+02:00","duration":14400,"activity":{"id":5},"project":{"id":3},"description":"Rezeption"}]"""));

        var entry = Assert.Single(await CreateClient(handler).GetTimesheetsAsync(
            Settings, Employee, new DateTime(2026, 8, 28), new DateTime(2026, 8, 29)));

        Assert.Equal((3, "Rezeption"), (entry.ProjectId, entry.Description));
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
