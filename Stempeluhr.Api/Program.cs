using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<RuntimeSettingsStore>();
builder.Services.AddHttpClient<KimaiClient>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AngularDev", policy =>
    {
        policy
            .WithOrigins("http://localhost:4200", "https://localhost:4200")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var exception = context.Features.Get<IExceptionHandlerPathFeature>()?.Error;
        var statusCode = exception is KimaiApiException apiException
            ? (int)apiException.StatusCode
            : StatusCodes.Status500InternalServerError;

        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(new
        {
            message = exception is KimaiApiException ? "Kimai konnte die Anfrage nicht verarbeiten." : "Interner Fehler.",
            details = exception is KimaiApiException api ? api.Details : null
        });
    });
});

app.UseCors("AngularDev");
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/", () => Results.Redirect("/api/health"));

app.MapGet("/api/health", (RuntimeSettingsStore settingsStore) =>
{
    var settings = settingsStore.Load();
    var version = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion;

    return Results.Ok(new
    {
        ok = true,
        version,
        configuredEmployees = settings.Employees.Count,
        settingsConfigured = settings.IsConfigured
    });
});

app.MapGet("/api/employees", (RuntimeSettingsStore settingsStore) =>
{
    var settings = settingsStore.Load();
    var employees = settings.Employees
        .Where(employee => employee.IsEnabled && !string.IsNullOrWhiteSpace(employee.ApiToken))
        .Select(employee => new EmployeeDto(
            employee.Id,
            employee.DisplayName,
            Initials(employee.DisplayName),
            employee.Color,
            employee.ImageUrl,
            !string.IsNullOrWhiteSpace(employee.Pin)))
        .ToArray();

    return Results.Ok(employees);
});

app.MapPost("/api/clock/status", async (ClockRequest request, KimaiClient kimai, RuntimeSettingsStore settingsStore) =>
{
    var settings = settingsStore.Load();
    var employee = FindEmployee(settings, request);
    if (employee is null)
    {
        return Results.Unauthorized();
    }

    var status = await kimai.GetStatusAsync(settings, employee);
    return Results.Ok(status);
});

app.MapPost("/api/clock/start", async (ClockRequest request, KimaiClient kimai, RuntimeSettingsStore settingsStore) =>
{
    var settings = settingsStore.Load();
    var employee = FindEmployee(settings, request);
    if (employee is null)
    {
        return Results.Unauthorized();
    }

    var running = await kimai.GetStatusAsync(settings, employee);
    if (running.IsRunning)
    {
        return Results.Ok(running with { StateText = "Schon eingestempelt" });
    }

    await kimai.StartAsync(settings, employee);
    var status = await kimai.GetStatusAsync(settings, employee);
    return Results.Ok(status with { StateText = "Eingestempelt" });
});

app.MapPost("/api/clock/stop", async (ClockRequest request, KimaiClient kimai, RuntimeSettingsStore settingsStore) =>
{
    var settings = settingsStore.Load();
    var employee = FindEmployee(settings, request);
    if (employee is null)
    {
        return Results.Unauthorized();
    }

    var running = await kimai.GetStatusAsync(settings, employee);
    if (!running.IsRunning || running.ActiveTimesheetId is null)
    {
        return Results.Ok(running with { StateText = "Nicht eingestempelt" });
    }

    await kimai.StopAsync(settings, employee, running.ActiveTimesheetId.Value);
    var status = await kimai.GetStatusAsync(settings, employee);
    return Results.Ok(status with { StateText = "Ausgestempelt" });
});

app.MapGet("/api/admin/settings", (HttpRequest request, RuntimeSettingsStore settingsStore, IConfiguration configuration) =>
{
    if (!IsAdmin(request, settingsStore, configuration))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(AdminSettingsDto.FromSettings(settingsStore.Load()));
});

app.MapPut("/api/admin/settings", async (
    HttpRequest request,
    AdminSettingsUpdateDto update,
    RuntimeSettingsStore settingsStore,
    IConfiguration configuration) =>
{
    if (!IsAdmin(request, settingsStore, configuration) && !CanBootstrapFromLocalhost(request, settingsStore, configuration))
    {
        return Results.Unauthorized();
    }

    var current = settingsStore.Load();
    var settings = update.ToSettings(current);
    await settingsStore.SaveAsync(settings);

    return Results.Ok(AdminSettingsDto.FromSettings(settings));
});

app.MapPost("/api/admin/kimai-users", async (
    HttpRequest request,
    KimaiImportRequest importRequest,
    KimaiClient kimai,
    RuntimeSettingsStore settingsStore,
    IConfiguration configuration) =>
{
    if (!IsAdmin(request, settingsStore, configuration))
    {
        return Results.Unauthorized();
    }

    var settings = settingsStore.Load();
    var baseUrl = FirstNonEmpty(importRequest.BaseUrl, settings.BaseUrl);
    var token = FirstNonEmpty(importRequest.AdminApiToken, settings.AdminApiToken);

    if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
    {
        return Results.BadRequest(new { message = "Kimai-URL und Admin-API-Token fehlen." });
    }

    var users = await kimai.GetUsersAsync(baseUrl, token);
    return Results.Ok(users);
});

app.MapFallbackToFile("index.html");

app.Run();

static EmployeeSettings? FindEmployee(RuntimeSettings settings, ClockRequest request)
{
    var employee = settings.Employees.FirstOrDefault(candidate =>
        candidate.IsEnabled &&
        string.Equals(candidate.Id, request.EmployeeId, StringComparison.OrdinalIgnoreCase));

    if (employee is null)
    {
        return null;
    }

    if (string.IsNullOrWhiteSpace(employee.Pin))
    {
        return employee;
    }

    return string.Equals(employee.Pin, request.Pin, StringComparison.Ordinal) ? employee : null;
}

static bool IsAdmin(HttpRequest request, RuntimeSettingsStore settingsStore, IConfiguration configuration)
{
    var expected = FirstNonEmpty(configuration["Admin:Password"], settingsStore.Load().AdminPassword);
    if (string.IsNullOrWhiteSpace(expected))
    {
        return false;
    }

    var provided = request.Headers["X-Admin-Password"].FirstOrDefault();
    return string.Equals(provided, expected, StringComparison.Ordinal);
}

static bool CanBootstrapFromLocalhost(HttpRequest request, RuntimeSettingsStore settingsStore, IConfiguration configuration)
{
    var hasPassword = !string.IsNullOrWhiteSpace(configuration["Admin:Password"]) ||
        !string.IsNullOrWhiteSpace(settingsStore.Load().AdminPassword);

    return !hasPassword && IsLocalhost(request);
}

static bool IsLocalhost(HttpRequest request)
{
    var remoteIp = request.HttpContext.Connection.RemoteIpAddress;
    return remoteIp is null || IPAddress.IsLoopback(remoteIp);
}

static string Initials(string name)
{
    var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    return string.Concat(parts.Take(2).Select(part => char.ToUpperInvariant(part[0])));
}

static string FirstNonEmpty(params string?[] values)
{
    return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}

public sealed class RuntimeSettingsStore(IWebHostEnvironment environment, IConfiguration configuration)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object gate = new();

    public RuntimeSettings Load()
    {
        lock (gate)
        {
            var path = GetPath();
            if (!File.Exists(path))
            {
                return LoadFromConfiguration();
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<RuntimeSettings>(json, JsonOptions) ?? new RuntimeSettings();
        }
    }

    public async Task SaveAsync(RuntimeSettings settings, CancellationToken cancellationToken = default)
    {
        var path = GetPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    private RuntimeSettings LoadFromConfiguration()
    {
        var settings = new RuntimeSettings();
        configuration.GetSection("Kimai").Bind(settings);
        configuration.GetSection("Admin").Bind(settings);
        return settings;
    }

    private string GetPath()
    {
        var configuredPath = configuration["Stempeluhr:SettingsPath"];
        return string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(environment.ContentRootPath, "data", "settings.json")
            : configuredPath;
    }
}

public sealed class KimaiClient(IHttpClientFactory httpClientFactory)
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
            return new ClockStatusDto(false, null, null, 0, "Nicht eingestempelt");
        }

        var id = current.GetProperty("id").GetInt32();
        var startedAt = current.TryGetProperty("begin", out var begin) ? begin.GetString() : null;
        var durationSeconds = current.TryGetProperty("duration", out var duration) && duration.ValueKind == JsonValueKind.Number
            ? duration.GetInt32()
            : 0;

        return new ClockStatusDto(true, id, startedAt, durationSeconds, "Eingestempelt");
    }

    public Task StartAsync(RuntimeSettings settings, EmployeeSettings employee, CancellationToken cancellationToken = default)
    {
        var projectId = employee.ProjectId ?? settings.DefaultProjectId;
        var activityId = employee.ActivityId ?? settings.DefaultActivityId;

        if (projectId is null || activityId is null)
        {
            throw new InvalidOperationException("Projekt und Aktivitaet muessen konfiguriert sein.");
        }

        var body = new
        {
            begin = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
            project = projectId,
            activity = activityId,
            description = string.IsNullOrWhiteSpace(employee.Description) ? "Stempeluhr" : employee.Description,
            tags = employee.Tags.Length == 0 ? null : string.Join(",", employee.Tags),
            billable = employee.Billable
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
        using var httpClient = httpClientFactory.CreateClient();
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

public sealed class RuntimeSettings
{
    public string BaseUrl { get; init; } = string.Empty;
    public string? AdminPassword { get; init; }
    public string? AdminApiToken { get; init; }
    public int? DefaultProjectId { get; init; }
    public int? DefaultActivityId { get; init; }
    public List<EmployeeSettings> Employees { get; init; } = [];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && Employees.Any(employee => employee.IsEnabled);
}

public sealed class EmployeeSettings
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public int? KimaiUserId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? Pin { get; init; }
    public string ApiToken { get; init; } = string.Empty;
    public int? ProjectId { get; init; }
    public int? ActivityId { get; init; }
    public string Color { get; init; } = "#2563eb";
    public string? ImageUrl { get; init; }
    public string? Description { get; init; }
    public string[] Tags { get; init; } = [];
    public bool Billable { get; init; } = true;
    public bool IsEnabled { get; init; } = true;
}

public sealed record ClockRequest(string EmployeeId, string? Pin);
public sealed record EmployeeDto(string Id, string DisplayName, string Initials, string Color, string? ImageUrl, bool RequiresPin);
public sealed record ClockStatusDto(
    bool IsRunning,
    int? ActiveTimesheetId,
    string? StartedAt,
    int DurationSeconds,
    string StateText);

public sealed record KimaiImportRequest(string? BaseUrl, string? AdminApiToken);
public sealed record KimaiUserDto(int Id, string? Username, string? Email, string DisplayName, string? AvatarUrl);

public sealed record AdminSettingsDto(
    string BaseUrl,
    bool HasAdminPassword,
    bool HasAdminApiToken,
    int? DefaultProjectId,
    int? DefaultActivityId,
    IReadOnlyCollection<AdminEmployeeDto> Employees)
{
    public static AdminSettingsDto FromSettings(RuntimeSettings settings)
    {
        return new AdminSettingsDto(
            settings.BaseUrl,
            !string.IsNullOrWhiteSpace(settings.AdminPassword),
            !string.IsNullOrWhiteSpace(settings.AdminApiToken),
            settings.DefaultProjectId,
            settings.DefaultActivityId,
            settings.Employees.Select(AdminEmployeeDto.FromSettings).ToArray());
    }
}

public sealed record AdminEmployeeDto(
    string Id,
    int? KimaiUserId,
    string DisplayName,
    string? Pin,
    bool HasApiToken,
    int? ProjectId,
    int? ActivityId,
    string Color,
    string? ImageUrl,
    string? Description,
    string[] Tags,
    bool Billable,
    bool IsEnabled)
{
    public static AdminEmployeeDto FromSettings(EmployeeSettings employee)
    {
        return new AdminEmployeeDto(
            employee.Id,
            employee.KimaiUserId,
            employee.DisplayName,
            employee.Pin,
            !string.IsNullOrWhiteSpace(employee.ApiToken),
            employee.ProjectId,
            employee.ActivityId,
            employee.Color,
            employee.ImageUrl,
            employee.Description,
            employee.Tags,
            employee.Billable,
            employee.IsEnabled);
    }
}

public sealed record AdminSettingsUpdateDto(
    string? BaseUrl,
    string? AdminPassword,
    string? AdminApiToken,
    bool KeepAdminApiToken,
    int? DefaultProjectId,
    int? DefaultActivityId,
    IReadOnlyCollection<AdminEmployeeUpdateDto> Employees)
{
    public RuntimeSettings ToSettings(RuntimeSettings current)
    {
        var employees = Employees.Select(employee => employee.ToSettings(current)).ToList();
        return new RuntimeSettings
        {
            BaseUrl = BaseUrl?.Trim() ?? string.Empty,
            AdminPassword = string.IsNullOrWhiteSpace(AdminPassword) ? current.AdminPassword : AdminPassword,
            AdminApiToken = KeepAdminApiToken && string.IsNullOrWhiteSpace(AdminApiToken) ? current.AdminApiToken : AdminApiToken,
            DefaultProjectId = DefaultProjectId,
            DefaultActivityId = DefaultActivityId,
            Employees = employees
        };
    }
}

public sealed record AdminEmployeeUpdateDto(
    string? Id,
    int? KimaiUserId,
    string? DisplayName,
    string? Pin,
    string? ApiToken,
    bool KeepApiToken,
    int? ProjectId,
    int? ActivityId,
    string? Color,
    string? ImageUrl,
    string? Description,
    string[]? Tags,
    bool Billable,
    bool IsEnabled)
{
    public EmployeeSettings ToSettings(RuntimeSettings current)
    {
        var id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id;
        var existing = current.Employees.FirstOrDefault(employee => string.Equals(employee.Id, id, StringComparison.OrdinalIgnoreCase));

        return new EmployeeSettings
        {
            Id = id,
            KimaiUserId = KimaiUserId,
            DisplayName = DisplayName?.Trim() ?? string.Empty,
            Pin = string.IsNullOrWhiteSpace(Pin) ? null : Pin,
            ApiToken = KeepApiToken && string.IsNullOrWhiteSpace(ApiToken) ? existing?.ApiToken ?? string.Empty : ApiToken ?? string.Empty,
            ProjectId = ProjectId,
            ActivityId = ActivityId,
            Color = string.IsNullOrWhiteSpace(Color) ? "#2563eb" : Color,
            ImageUrl = string.IsNullOrWhiteSpace(ImageUrl) ? null : ImageUrl,
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description,
            Tags = Tags ?? [],
            Billable = Billable,
            IsEnabled = IsEnabled
        };
    }
}
