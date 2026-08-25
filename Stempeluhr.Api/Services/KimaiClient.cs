using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

public sealed class KimaiClient(HttpClient httpClient, ILogger<KimaiClient> logger) : IKimaiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ClockStatusDto> GetStatusAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        CancellationToken cancellationToken = default)
    {
        var active = await SendAsync<JsonElement[]>(settings.BaseUrl, employee.ApiToken, HttpMethod.Get, "api/timesheets/active", null, cancellationToken);
        var current = active.FirstOrDefault();

        if (current.ValueKind is JsonValueKind.Undefined)
        {
            return new ClockStatusDto(false, null, null, 0, "clockedOut", "Nicht eingestempelt");
        }

        var id = current.GetProperty("id").GetInt32();
        var startedAt = current.TryGetProperty("begin", out var begin) ? begin.GetString() : null;
        var durationSeconds = current.TryGetProperty("duration", out var duration) && duration.ValueKind == JsonValueKind.Number
            ? duration.GetInt32()
            : 0;
        var activityId = GetId(current, "activity");
        var isPaused = settings.PauseActivityId is not null && activityId == settings.PauseActivityId;

        return new ClockStatusDto(
            true,
            id,
            startedAt,
            durationSeconds,
            isPaused ? "paused" : "working",
            isPaused ? "In Pause" : "Eingestempelt");
    }

    public Task StartAsync(RuntimeSettings settings, EmployeeSettings employee, CancellationToken cancellationToken = default)
    {
        var projectId = employee.ProjectId ?? settings.DefaultProjectId;
        var activityId = employee.ActivityId ?? settings.DefaultActivityId;

        if (projectId is null || activityId is null)
        {
            throw new InvalidOperationException("Projekt und Aktivitaet muessen konfiguriert sein.");
        }

        return StartTimesheetAsync(
            settings,
            employee,
            projectId.Value,
            activityId.Value,
            string.IsNullOrWhiteSpace(employee.Description) ? "Stempeluhr" : employee.Description,
            employee.Billable,
            cancellationToken);
    }

    public Task StartPauseAsync(RuntimeSettings settings, EmployeeSettings employee, CancellationToken cancellationToken = default)
    {
        var projectId = employee.ProjectId ?? settings.DefaultProjectId;
        var activityId = settings.PauseActivityId;

        if (projectId is null || activityId is null)
        {
            throw new InvalidOperationException("Projekt und Pausen-Aktivitaet muessen konfiguriert sein.");
        }

        return StartTimesheetAsync(
            settings,
            employee,
            projectId.Value,
            activityId.Value,
            "Pause",
            false,
            cancellationToken);
    }

    public Task StopAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        int timesheetId,
        CancellationToken cancellationToken = default)
    {
        return SendAsync<JsonElement>(settings.BaseUrl, employee.ApiToken, HttpMethod.Patch, $"api/timesheets/{timesheetId}/stop", null, cancellationToken);
    }

    public async Task StartAtAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        int projectId,
        int activityId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            project = projectId,
            activity = activityId,
            description = string.IsNullOrWhiteSpace(employee.Description) ? "Stempeluhr" : employee.Description,
            tags = employee.Tags.Length == 0 ? null : string.Join(",", employee.Tags),
            billable = employee.Billable,
            // Kimai expects ISO 8601; with ?full=true the begin date is accepted on create.
            begin = startedAt.ToString("yyyy-MM-dd'T'HH:mm:sszzz")
        };

        try
        {
            await SendAsync<JsonElement>(settings.BaseUrl, employee.ApiToken, HttpMethod.Post, "api/timesheets?full=true", body, cancellationToken);
        }
        catch (KimaiApiException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            // Older Kimai versions reject `begin` on create. Fallback: create
            // now, then edit the begin date on the new timesheet.
            var created = await SendAsync<JsonElement>(
                settings.BaseUrl, employee.ApiToken, HttpMethod.Post,
                "api/timesheets?full=true",
                new { project = projectId, activity = activityId, description = body.description, tags = body.tags, billable = body.billable },
                cancellationToken);

            if (created.ValueKind is JsonValueKind.Object && created.TryGetProperty("id", out var idProperty) && idProperty.ValueKind == JsonValueKind.Number)
            {
                var timesheetId = idProperty.GetInt32();
                try
                {
                    // Same transient-retry as the end-backdate: without it ONE
                    // 5xx/network hiccup left the sheet running with begin=now.
                    await BackdatePatchAsync(
                        settings, employee, "begin-backdate", timesheetId, startedAt,
                        new { begin = body.begin },
                        cancellationToken);
                }
                catch (Exception backdateEx) when (IsTransientBackdateFailure(backdateEx))
                {
                    // The sheet now runs with begin=now: a later offline replay
                    // sees IsRunning == true and answers "Lief bereits" - the
                    // wrong start time would persist silently. Stop the
                    // misdated sheet so the replay can recreate it with the
                    // intended begin instead.
                    logger.LogWarning(
                        backdateEx,
                        "Kimai: begin-backdate for timesheet {TimesheetId} failed after retries - stopping the misdated sheet (intended begin {Intended}) so a later replay can recreate it",
                        timesheetId, startedAt);
                    try
                    {
                        await SendAsync<JsonElement>(
                            settings.BaseUrl, employee.ApiToken, HttpMethod.Patch,
                            $"api/timesheets/{timesheetId}/stop",
                            null,
                            cancellationToken);
                    }
                    catch (Exception stopEx)
                    {
                        // Stop failed: the sheet keeps running with begin=now
                        // and would answer any replayed start with "Lief
                        // bereits" - manual correction required. Re-throwing
                        // still preserves the event in the offline buffer, so
                        // the response-lost case stays recoverable once the
                        // sheet is corrected.
                        logger.LogError(
                            stopEx,
                            "Kimai: could not stop misdated timesheet {TimesheetId}; it keeps begin=now instead of {Intended}. Manual correction required.",
                            timesheetId, startedAt);
                        throw;
                    }

                    // Stop succeeded: the misdated sheet is closed and no longer
                    // blocks a replay. Swallowing the backdate error here would
                    // acknowledge the start-event even though nothing in Kimai
                    // reflects it - the whole offline session (including its
                    // later stop) would be lost silently. Re-throw so the sync
                    // loop buffers this event as transient and the replay can
                    // recreate it with the intended begin.
                    throw;
                }
            }
            else
            {
                // The timesheet was created without `begin`; without its ID we
                // cannot backdate it. Log loudly instead of silently accepting
                // a wrong start time.
                logger.LogWarning(
                    "Kimai fallback: created timesheet without id, begin backdate skipped ({BaseUrl}, user {User})",
                    settings.BaseUrl, employee.Id);
            }
        }
    }

    public async Task StopAtAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        int timesheetId,
        DateTimeOffset stoppedAt,
        CancellationToken cancellationToken = default)
    {
        // First stop the timesheet normally so Kimai computes a duration.
        await SendAsync<JsonElement>(settings.BaseUrl, employee.ApiToken, HttpMethod.Patch, $"api/timesheets/{timesheetId}/stop", null, cancellationToken);

        // Then backdate the end timestamp to the real scan time. If the stop
        // went through but this PATCH is lost to a transient error, the
        // timesheet keeps end=now and a later offline replay would see
        // IsRunning == false - it could never correct the end time on its own.
        await BackdatePatchAsync(
            settings, employee, "end-backdate", timesheetId, stoppedAt,
            new { end = stoppedAt.ToString("yyyy-MM-dd'T'HH:mm:sszzz") },
            cancellationToken);
    }

    /// <summary>
    /// PATCHes one timestamp correction (begin/end backdate) with a short
    /// transient-retry loop. A lost backdate silently leaves a WRONG time
    /// behind (begin=now keeps running; end=now shifts worked time) and a
    /// later offline replay usually cannot correct it anymore - the retry
    /// used to exist only for the end-backdate; the begin-backdate in
    /// <see cref="StartAtAsync"/>'s old-Kimai fallback now shares the same
    /// mechanism, so one transient 5xx no longer freezes a wrong start time.
    /// </summary>
    private async Task BackdatePatchAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        string what,
        int timesheetId,
        DateTimeOffset intendedTimestamp,
        object body,
        CancellationToken cancellationToken)
    {
        var path = $"api/timesheets/{timesheetId}";

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await SendAsync<JsonElement>(settings.BaseUrl, employee.ApiToken, HttpMethod.Patch, path, body, cancellationToken);
                return;
            }
            catch (Exception ex) when (IsTransientBackdateFailure(ex) && attempt < BackdateRetryCount)
            {
                logger.LogWarning(
                    ex,
                    "Kimai: {What} for timesheet {TimesheetId} failed (attempt {Attempt}/{Retries}); retrying",
                    what, timesheetId, attempt, BackdateRetryCount);
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), cancellationToken);
            }
            catch (Exception ex) when (IsTransientBackdateFailure(ex))
            {
                logger.LogError(
                    ex,
                    "Kimai: {What} for timesheet {TimesheetId} (employee {Employee}) failed after {Retries} attempts; " +
                    "the timesheet may keep a timestamp other than {Intended}. Manual correction may be required.",
                    what, timesheetId, employee.Id, BackdateRetryCount, intendedTimestamp);
                throw;
            }
        }
    }

    /// <inheritdoc />
    public async Task<KimaiRecentTimesheetDto?> GetLatestStoppedTimesheetAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        CancellationToken cancellationToken = default)
    {
        // state=stopped excludes running and already-exported/closed entries;
        // user=me scopes the query to the token owner so another employee's
        // timesheet can never satisfy the interrupted-pauseEnd check.
        var latest = await SendAsync<JsonElement[]>(
            settings.BaseUrl,
            employee.ApiToken,
            HttpMethod.Get,
            "api/timesheets?size=1&orderBy=end&sort=DESC&state=stopped&user=me",
            null,
            cancellationToken);

        var entry = latest.FirstOrDefault();
        if (entry.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        var activityId = GetId(entry, "activity");
        DateTimeOffset? endedAt = null;
        if (entry.TryGetProperty("end", out var end) && end.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(end.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            endedAt = parsed;
        }

        return new KimaiRecentTimesheetDto(activityId, endedAt);
    }

    private const int BackdateRetryCount = 3;

    private static bool IsTransientBackdateFailure(Exception exception)
    {
        if (exception is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            return true;
        }

        if (exception is KimaiApiException apiException)
        {
            var statusCode = (int)apiException.StatusCode;
            return statusCode >= 500 || statusCode == 408 || statusCode == 429;
        }

        return false;
    }

    public async Task<IReadOnlyCollection<KimaiUserDto>> GetUsersAsync(
        string baseUrl,
        string apiToken,
        CancellationToken cancellationToken = default)
    {
        var users = await SendAsync<JsonElement[]>(baseUrl, apiToken, HttpMethod.Get, "api/users", null, cancellationToken);
        return users.Select(ParseKimaiUser).OrderBy(user => user.DisplayName).ToArray();
    }

    public async Task<IReadOnlyCollection<KimaiActivityDto>> GetActivitiesAsync(
        string baseUrl,
        string apiToken,
        CancellationToken cancellationToken = default)
    {
        var activities = await SendAsync<JsonElement[]>(
            baseUrl,
            apiToken,
            HttpMethod.Get,
            "api/activities?visible=1&orderBy=name&order=ASC",
            null,
            cancellationToken);

        return activities.Select(ParseKimaiActivity).OrderBy(activity => activity.Name).ToArray();
    }

    public async Task<IReadOnlyCollection<KimaiProjectDto>> GetProjectsAsync(
        string baseUrl,
        string apiToken,
        CancellationToken cancellationToken = default)
    {
        var projects = await SendAsync<JsonElement[]>(
            baseUrl,
            apiToken,
            HttpMethod.Get,
            "api/projects?visible=1&orderBy=name&order=ASC",
            null,
            cancellationToken);

        return projects.Select(ParseKimaiProject).OrderBy(project => project.Name).ToArray();
    }

    private async Task<T> SendAsync<T>(
        string baseUrl,
        string apiToken,
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, BuildUri(baseUrl, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var details = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new KimaiApiException(response.StatusCode, details);
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidOperationException("Kimai returned an empty response.");
    }

    private Task StartTimesheetAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        int projectId,
        int activityId,
        string description,
        bool billable,
        CancellationToken cancellationToken)
    {
        var body = new
        {
            project = projectId,
            activity = activityId,
            description,
            tags = employee.Tags.Length == 0 ? null : string.Join(",", employee.Tags),
            billable
        };

        return SendAsync<JsonElement>(settings.BaseUrl, employee.ApiToken, HttpMethod.Post, "api/timesheets?full=true", body, cancellationToken);
    }

    private static KimaiUserDto ParseKimaiUser(JsonElement user)
    {
        var id = user.TryGetProperty("id", out var idProperty) && idProperty.ValueKind == JsonValueKind.Number
            ? idProperty.GetInt32()
            : 0;

        var username = GetString(user, "username");
        var email = GetString(user, "email");
        var displayName = FirstNonEmpty(
            GetString(user, "alias"),
            GetString(user, "displayName"),
            GetString(user, "name"),
            username,
            email,
            $"Kimai #{id}");

        return new KimaiUserDto(id, username, email, displayName, GetString(user, "avatar"));
    }

    private static KimaiActivityDto ParseKimaiActivity(JsonElement activity)
    {
        var id = activity.TryGetProperty("id", out var idProperty) && idProperty.ValueKind == JsonValueKind.Number
            ? idProperty.GetInt32()
            : 0;

        var name = FirstNonEmpty(GetString(activity, "name"), $"Aktivitaet #{id}");
        var visible = !activity.TryGetProperty("visible", out var visibleProperty)
            || visibleProperty.ValueKind is not JsonValueKind.False;

        return new KimaiActivityDto(
            id,
            name,
            GetString(activity, "parentTitle"),
            GetId(activity, "project"),
            visible);
    }

    private static KimaiProjectDto ParseKimaiProject(JsonElement project)
    {
        var id = project.TryGetProperty("id", out var idProperty) && idProperty.ValueKind == JsonValueKind.Number
            ? idProperty.GetInt32()
            : 0;

        var name = FirstNonEmpty(GetString(project, "name"), $"Projekt #{id}");
        var visible = !project.TryGetProperty("visible", out var visibleProperty)
            || visibleProperty.ValueKind is not JsonValueKind.False;

        return new KimaiProjectDto(
            id,
            name,
            GetString(project, "parentTitle"),
            GetId(project, "customer"),
            visible);
    }

    private static string? GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int? GetId(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number)
        {
            return property.GetInt32();
        }

        if (property.ValueKind == JsonValueKind.Object
            && property.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.Number)
        {
            return id.GetInt32();
        }

        return null;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static Uri BuildUri(string baseUrl, string path)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("Kimai-URL fehlt.");
        }

        return new Uri($"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}");
    }
}

public sealed class KimaiApiException(HttpStatusCode statusCode, string details)
    : Exception($"Kimai API returned {(int)statusCode}: {details}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string Details { get; } = details;
}
