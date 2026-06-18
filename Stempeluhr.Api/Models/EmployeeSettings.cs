namespace Stempeluhr.Api.Models;

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
