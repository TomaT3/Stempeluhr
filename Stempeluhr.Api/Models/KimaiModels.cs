namespace Stempeluhr.Api.Models;

public sealed record KimaiImportRequest(string? BaseUrl, string? AdminApiToken);

public sealed record KimaiActivityDto(int Id, string Name, string? ParentTitle, int? ProjectId, bool Visible);

public sealed record KimaiProjectDto(int Id, string Name, string? ParentTitle, int? CustomerId, bool Visible);

public sealed record KimaiUserDto(int Id, string? Username, string? Email, string DisplayName, string? AvatarUrl);

/// <summary>Minimale Sicht auf einen Kimai-Timesheet-Eintrag für die Stundenübersicht.</summary>
public sealed record KimaiTimesheetEntryDto(
    int Id,
    DateTimeOffset? Begin,
    DateTimeOffset? End,
    int? DurationSeconds,
    int? ActivityId);
