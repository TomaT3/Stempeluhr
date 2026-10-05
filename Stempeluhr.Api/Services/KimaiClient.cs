using System.Diagnostics;
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
        var projectId = GetId(current, "project");
        var task = isPaused ? null : WorkTargetResolver.MatchTask(employee, projectId, activityId);
        var onDefault = !isPaused && task is null && WorkTargetResolver.IsDefault(settings, employee, projectId, activityId);

        return new ClockStatusDto(
            true,
            id,
            startedAt,
            durationSeconds,
            isPaused ? "paused" : "working",
            isPaused ? "In Pause" : "Eingestempelt",
            task?.Id,
            task?.Label,
            onDefault);
    }

    public Task StartAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        KimaiTimesheetTarget target,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            project = target.ProjectId,
            activity = target.ActivityId,
            description = target.Description,
            tags = employee.Tags.Length == 0 ? null : string.Join(",", employee.Tags),
            billable = target.Billable
        };

        return SendAsync<JsonElement>(settings.BaseUrl, employee.ApiToken, HttpMethod.Post, "api/timesheets?full=true", body, cancellationToken);
    }

    public Task StopAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        int timesheetId,
        CancellationToken cancellationToken = default)
    {
        return SendAsync<JsonElement>(settings.BaseUrl, employee.ApiToken, HttpMethod.Patch, $"api/timesheets/{timesheetId}/stop", null, cancellationToken);
    }

    public Task StartAtAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        KimaiTimesheetTarget target,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            project = target.ProjectId,
            activity = target.ActivityId,
            description = target.Description,
            tags = employee.Tags.Length == 0 ? null : string.Join(",", employee.Tags),
            billable = target.Billable,
            // full=true only expands the response. The tracking mode and the
            // token owner's permissions determine whether begin is allowed.
            begin = FormatTimestamp(startedAt)
        };

        // A 400 may mean that begin is forbidden, not that this is an old
        // API. Creating at 'now' and PATCHing afterwards then fails too and
        // leaves a running, incorrectly dated sheet. Only create atomically
        // with the captured time; permanent errors remain in the journal.
        return SendAsync<JsonElement>(settings.BaseUrl, employee.ApiToken, HttpMethod.Post, "api/timesheets?full=true", body, cancellationToken);
    }

    public Task StopAtAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        int timesheetId,
        DateTimeOffset stoppedAt,
        CancellationToken cancellationToken = default)
    {
        // Updating end also stops a running sheet. Validate and save the
        // captured time together; never stop at now before a rejected PATCH.
        return BackdateEndAsync(settings, employee, timesheetId, stoppedAt, cancellationToken);
    }

    /// <inheritdoc />
    public Task BackdateEndAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        int timesheetId,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken = default)
    {
        return BackdatePatchAsync(
            settings, employee, "end-backdate", timesheetId, endedAt.ToString("o"),
            new { end = FormatTimestamp(endedAt) },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task UpdateTimesheetTimesAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        int timesheetId,
        DateTimeOffset? begin,
        DateTimeOffset? end,
        CancellationToken cancellationToken = default)
    {
        if (begin is null && end is null)
        {
            throw new ArgumentException("begin or end must be set.");
        }

        var body = new Dictionary<string, string>();
        if (begin is { } b) body["begin"] = FormatTimestamp(b);
        if (end is { } e) body["end"] = FormatTimestamp(e);

        return BackdatePatchAsync(
            settings, employee, "times-update", timesheetId,
            $"begin={begin?.ToString("o")} end={end?.ToString("o")}",
            body, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<KimaiTimesheetDetailDto?> GetTimesheetAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        int timesheetId,
        CancellationToken cancellationToken = default)
    {
        JsonElement sheet;
        try
        {
            sheet = await SendAsync<JsonElement>(
                settings.BaseUrl, employee.ApiToken, HttpMethod.Get, $"api/timesheets/{timesheetId}", null, cancellationToken);
        }
        catch (KimaiApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var begin = ParseDateTimeOffset(sheet, "begin")
            ?? throw new InvalidOperationException($"Kimai timesheet {timesheetId} has no begin.");
        return new KimaiTimesheetDetailDto(
            GetId(sheet, "id") ?? timesheetId,
            begin,
            ParseDateTimeOffset(sheet, "end"),
            GetId(sheet, "activity") ?? throw new InvalidOperationException($"Kimai timesheet {timesheetId} has no activity."),
            GetId(sheet, "project") ?? throw new InvalidOperationException($"Kimai timesheet {timesheetId} has no project."),
            GetString(sheet, "description"),
            !sheet.TryGetProperty("billable", out var billable) || billable.ValueKind != JsonValueKind.False,
            GetId(sheet, "user"));
    }

    /// <inheritdoc />
    public async Task<int> CreateTimesheetAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        KimaiTimesheetTarget target,
        DateTimeOffset begin,
        DateTimeOffset end,
        string? description,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            project = target.ProjectId,
            activity = target.ActivityId,
            description,
            tags = employee.Tags.Length == 0 ? null : string.Join(",", employee.Tags),
            billable = target.Billable,
            begin = FormatTimestamp(begin),
            end = FormatTimestamp(end)
        };

        var created = await SendAsync<JsonElement>(
            settings.BaseUrl, employee.ApiToken, HttpMethod.Post, "api/timesheets?full=true", body, cancellationToken);
        return GetId(created, "id") ?? throw new InvalidOperationException("Kimai returned no timesheet id.");
    }

    private static string FormatTimestamp(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:sszzz");

    /// <summary>
    /// PATCHes begin/end timestamps with a short transient-retry loop. A lost
    /// backdate would otherwise silently leave a wrong stop time.
    /// </summary>
    private async Task BackdatePatchAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        string what,
        int timesheetId,
        string intended,
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
                    what, timesheetId, employee.Id, BackdateRetryCount, intended);
                throw;
            }
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<KimaiRecentTimesheetDto>> GetRecentStoppedTimesheetsAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        int count,
        CancellationToken cancellationToken = default)
    {
        // state=stopped excludes running and already-exported/closed entries;
        // without a user filter Kimai returns only the token owner's timesheets
        // (every employee has their own API token), so another employee's sheet
        // can never satisfy the interrupted-pauseEnd check. user=me would be
        // more explicit but Kimai rejects it with 400 (requirements: \d+|all).
        var entries = await SendAsync<JsonElement[]>(
            settings.BaseUrl,
            employee.ApiToken,
            HttpMethod.Get,
            $"api/timesheets?size={Math.Max(1, count)}&orderBy=end&order=DESC&state=stopped",
            null,
            cancellationToken);

        return entries
            .Where(entry => entry.ValueKind == JsonValueKind.Object)
            .Select(entry =>
            {
                DateTimeOffset? endedAt = null;
                if (entry.TryGetProperty("end", out var end) && end.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(end.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                {
                    endedAt = parsed;
                }

                return new KimaiRecentTimesheetDto(GetId(entry, "activity"), endedAt, GetId(entry, "project"), GetId(entry, "id"));
            })
            .ToList();
    }

    /// <inheritdoc />
    public async Task<string?> GetCurrentUserTimezoneAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var user = await SendAsync<JsonElement>(
                settings.BaseUrl,
                employee.ApiToken,
                HttpMethod.Get,
                "api/users/me",
                null,
                cancellationToken);

            return user.TryGetProperty("timezone", out var timezone) && timezone.ValueKind == JsonValueKind.String
                ? timezone.GetString()
                : null;
        }
        catch (KimaiApiException)
        {
            // The hours overview must degrade gracefully if the timezone
            // lookup fails (e.g. very old Kimai) - the caller falls back.
            return null;
        }
    }

    private const int BackdateRetryCount = 3;

    private static bool IsTransientBackdateFailure(Exception exception)
    {
        if (exception is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            return true;
        }

        return exception is KimaiApiException { IsTransient: true };
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

    public async Task<IReadOnlyCollection<KimaiTimesheetEntryDto>> GetTimesheetsAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        DateTime begin,
        DateTime end,
        CancellationToken cancellationToken = default)
    {
        var entries = new List<KimaiTimesheetEntryDto>();
        var page = 1;

        while (true)
        {
            // No user filter: Kimai returns only the token owner's timesheets
            // (every employee has their own API token). user=me would be the
            // explicit value but Kimai rejects it with 400 (requirements: \d+|all).
            var path = $"api/timesheets?begin={begin:yyyy-MM-ddTHH:mm:ss}&end={end:yyyy-MM-ddTHH:mm:ss}&size=500&page={page}&orderBy=begin&order=ASC";
            var batch = await SendAsync<JsonElement[]>(settings.BaseUrl, employee.ApiToken, HttpMethod.Get, path, null, cancellationToken);

            foreach (var item in batch)
            {
                entries.Add(new KimaiTimesheetEntryDto(
                    item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number ? idProp.GetInt32() : 0,
                    ParseDateTimeOffset(item, "begin"),
                    ParseDateTimeOffset(item, "end"),
                    item.TryGetProperty("duration", out var durProp) && durProp.ValueKind == JsonValueKind.Number ? durProp.GetInt32() : null,
                    GetId(item, "activity"),
                    GetId(item, "project"),
                    GetString(item, "description")));
            }

            if (batch.Length < 500)
            {
                return entries;
            }

            page++;
        }
    }

    private static DateTimeOffset? ParseDateTimeOffset(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(property.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
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

        var started = Stopwatch.GetTimestamp();
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var operation = $"{method} /{path.Split('?')[0]}";
        logger.LogDebug("Kimai {Operation}: HTTP {Status} in {ElapsedMs} ms",
            operation, (int)response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        if (!response.IsSuccessStatusCode)
        {
            var details = await response.Content.ReadAsStringAsync(cancellationToken);
            var rejectedFields = response.StatusCode == HttpStatusCode.BadRequest ? FindRejectedFields(details, body) : [];
            logger.LogWarning("Kimai {Operation}: HTTP {Status} after {ElapsedMs} ms; unsupported fields: {Fields}",
                operation, (int)response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                string.Join(",", rejectedFields));
            throw new KimaiApiException(response.StatusCode, details, operation, rejectedFields);
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidOperationException("Kimai returned an empty response.");
    }

    private static string[] FindRejectedFields(string details, object? body)
    {
        if (body is null) return [];
        try
        {
            using var document = JsonDocument.Parse(details);
            var errors = document.RootElement.GetProperty("errors");
            if (!errors.TryGetProperty("errors", out var messages) || messages.ValueKind != JsonValueKind.Array
                || !messages.EnumerateArray().Any(m => m.ValueKind == JsonValueKind.String)
                || !errors.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Object)
                return [];
            return JsonSerializer.SerializeToElement(body, JsonOptions).EnumerateObject()
                .Where(field => !children.TryGetProperty(field.Name, out _)).Select(field => field.Name).ToArray();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return []; // Preserve the original error for other Kimai response formats.
        }
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

public sealed class KimaiApiException(HttpStatusCode statusCode, string details, string? operation = null, string[]? rejectedFields = null)
    : Exception(Describe(statusCode, details, operation, rejectedFields))
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string Details { get; } = details;
    public string? Operation { get; } = operation;
    public IReadOnlyList<string> RejectedFields { get; } = rejectedFields ?? [];

    private static string Describe(HttpStatusCode status, string details, string? operation, string[]? fields)
    {
        var hint = fields?.Any(f => f is "begin" or "end") == true
            ? "Kimai erlaubt keine nachgetragenen Zeitpunkte für diesen API-Benutzer. Erfassungsmodus und Berechtigungen in Kimai prüfen. "
            : fields?.Contains("billable") == true
                ? "Kimai erlaubt diesem API-Benutzer das Feld billable nicht. Berechtigung edit_billable_own_timesheet prüfen. " : "";
        return $"{hint}Kimai API returned {(int)status}{(operation is null ? "" : $" ({operation})")}: {details}";
    }

    /// <summary>
    /// Kimai may accept the same request later: 5xx, 408 (timeout), 429
    /// (rate limit). The one classification for live path, replay and
    /// backdate retries; the kiosk queues exactly these answers (plus network
    /// errors) - every other status is final.
    /// </summary>
    public bool IsTransient => IsTransientStatus(StatusCode);

    public static bool IsTransientStatus(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code >= 500 || code == 408 || code == 429;
    }
}
