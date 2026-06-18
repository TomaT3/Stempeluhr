namespace Stempeluhr.Api.Models;

public sealed record AdminEmployeeStatusDto(
    string EmployeeId,
    bool IsRunning,
    string? StartedAt,
    int DurationSeconds,
    string StateText,
    bool IsAvailable);

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
            Pin = string.IsNullOrWhiteSpace(Pin) ? null : Pin.Trim(),
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
