namespace Stempeluhr.Api.Models;

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
