namespace Stempeluhr.Api.Models;

public sealed record ClockRequest(string EmployeeId, string? Pin);

/// <summary>
/// One queued card scan from an NFC terminal, submitted with its original
/// scan timestamp so offline events can be replayed with backdating.
/// </summary>
public sealed record OfflineNfcClockEventDto(
    string EventId,
    string CardId,
    string? TerminalId,
    DateTimeOffset ScannedAt);

public sealed record KioskPinLoginRequest(string? Pin);

public sealed record KioskClockRequest(string EmployeeId, string? Pin, string Action, string? NfcCardId);

public sealed record NfcClockRequest(string? CardId, string? Action, string? TerminalId);

public sealed record OfflineSyncRequest(IReadOnlyList<OfflineNfcClockEventDto>? Events);

public sealed record OfflineKioskClockEventDto(
    string EventId,
    string EmployeeId,
    string? Pin,
    string Action,
    DateTimeOffset PerformedAt);

public sealed record OfflineKioskSyncRequest(IReadOnlyList<OfflineKioskClockEventDto>? Events);

public sealed record EmployeeDto(
    string Id,
    string DisplayName,
    string Initials,
    string Color,
    string? ImageUrl,
    bool RequiresPin);

public sealed record KioskEmployeeSessionDto(EmployeeDto Employee, ClockStatusDto Status);

public sealed record NfcClockEventDto(
    string EventId,
    DateTimeOffset OccurredAt,
    string TerminalId,
    string? CardId,
    EmployeeDto? Employee,
    ClockStatusDto? Status,
    string Message,
    bool Success);

public sealed record NfcLatestEventDto(NfcClockEventDto? Event);

public sealed record OfflineSyncResultDto(
    int Accepted,
    int Duplicates,
    int Buffered,
    IReadOnlyList<OfflineSyncEventResultDto> Results);

public sealed record OfflineSyncEventResultDto(
    string EventId,
    string Status,
    string? Message);

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
