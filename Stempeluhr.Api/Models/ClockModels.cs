namespace Stempeluhr.Api.Models;

public sealed record ClockRequest(string EmployeeId, string? Pin);

public sealed record KioskPinLoginRequest(string? Pin);

public sealed record KioskClockRequest(string EmployeeId, string? Pin, string Action, string? NfcCardId);

/// <summary>
/// Card identification request from the kiosk browser: resolves a scanned
/// card id to an employee without stamping anything. Used by the kiosk's
/// local-scan path when the card is not in its local cache.
/// </summary>
public sealed record KioskIdentifyRequest(string? CardId, string? TerminalId);

/// <summary>
/// Card identification input for <c>IClockService.IdentifyWithNfcCardAsync</c>.
/// </summary>
public sealed record NfcClockRequest(string? CardId, string? Action, string? TerminalId);

/// <summary>
/// One queued kiosk action from the browser client, submitted with its
/// original action timestamp so offline events can be replayed with
/// backdating. <paramref name="NfcCardId"/> optionally carries the card that
/// unlocked the kiosk session (live-path parity: actions of an NFC-unlocked
/// session replay without a PIN - the card must map to the same employee).
/// </summary>
public sealed record OfflineKioskClockEventDto(
    string EventId,
    string EmployeeId,
    string? Pin,
    string Action,
    DateTimeOffset PerformedAt,
    string? NfcCardId = null);

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
    string? Message,
    string? State = null);

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

/// <summary>
/// Stundenübersicht des Mitarbeiters. <see cref="TodaySeconds"/> ist die
/// Netto-Arbeitszeit heute; <see cref="TodayPauseSeconds"/> die Pausenzeit
/// heute (separat, da die Karte beides anzeigt). Woche/Monat: nur Netto.
/// </summary>
public sealed record HoursOverviewDto(
    int TodaySeconds,
    int TodayPauseSeconds,
    int WeekSeconds,
    int MonthSeconds);
