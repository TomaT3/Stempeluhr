using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

public sealed class KimaiClient(HttpClient httpClient) : IKimaiClient
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
                await SendAsync<JsonElement>(
                    settings.BaseUrl, employee.ApiToken, HttpMethod.Patch,
                    $"api/timesheets/{timesheetId}",
                    new { begin = body.begin },
                    cancellationToken);
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

        // Then backdate the end timestamp to the real scan time.
        await SendAsync<JsonElement>(
            settings.BaseUrl,
            employee.ApiToken,
            HttpMethod.Patch,
            $"api/timesheets/{timesheetId}",
            new { end = stoppedAt.ToString("yyyy-MM-dd'T'HH:mm:sszzz") },
            cancellationToken);
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
