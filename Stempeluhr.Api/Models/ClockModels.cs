namespace Stempeluhr.Api.Models;

public sealed record ClockRequest(string EmployeeId, string? Pin);

public sealed record KioskPinLoginRequest(string? Pin);

public sealed record KioskClockRequest(string EmployeeId, string? Pin, string Action);

public sealed record EmployeeDto(
    string Id,
    string DisplayName,
    string Initials,
    string Color,
    string? ImageUrl,
    bool RequiresPin);

public sealed record KioskEmployeeSessionDto(EmployeeDto Employee, ClockStatusDto Status);

public sealed record ClockStatusDto(
    bool IsRunning,
    int? ActiveTimesheetId,
    string? StartedAt,
    int DurationSeconds,
    string State,
    string StateText);

public enum ClockActionResult
{
    Success,
    Unauthorized,
    BadRequest
}

public sealed record ClockActionResponse(ClockActionResult Result, ClockStatusDto? Status);
