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

    public async Task<IReadOnlyCollection<KimaiUserDto>> GetUsersAsync(
        string baseUrl,
        string apiToken,
        CancellationToken cancellationToken = default)
    {
        var users = await SendAsync<JsonElement[]>(baseUrl, apiToken, HttpMethod.Get, "api/users", null, cancellationToken);
        return users.Select(ParseKimaiUser).OrderBy(user => user.DisplayName).ToArray();
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
