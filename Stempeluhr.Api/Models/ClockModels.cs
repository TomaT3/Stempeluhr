namespace Stempeluhr.Api.Models;

public sealed record ClockRequest(string EmployeeId, string? Pin);

public sealed record KioskPinLoginRequest(string? Pin);

/// <summary>
/// One kiosk action. <paramref name="TaskId"/> is only used by "start" and
/// "switch": the task to clock in on resp. switch to (null/empty = the
/// default task).
/// </summary>
public sealed record KioskClockRequest(string EmployeeId, string? Pin, string Action, string? NfcCardId, string? TaskId = null);

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
/// <paramref name="TaskId"/> is the task of a "start" or the target of a
/// "switch" (null/empty = default task); queues from older clients simply
/// lack it.
/// </summary>
public sealed record OfflineKioskClockEventDto(
    string EventId,
    string EmployeeId,
    string? Pin,
    string Action,
    DateTimeOffset PerformedAt,
    string? NfcCardId = null,
    string? TaskId = null);

public sealed record OfflineKioskSyncRequest(IReadOnlyList<OfflineKioskClockEventDto>? Events);

public sealed record EmployeeDto(
    string Id,
    string DisplayName,
    string Initials,
    string Color,
    string? ImageUrl,
    bool RequiresPin,
    IReadOnlyList<EmployeeTaskDto>? Tasks = null,
    string? DefaultTaskLabel = null);

/// <summary>Weitere Tätigkeit für die Auswahl am Kiosk (nur Anzeige-Daten).</summary>
public sealed record EmployeeTaskDto(string Id, string Label);

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

/// <summary>
/// Stempelstatus. <see cref="ActiveTaskId"/>/<see cref="ActiveTaskLabel"/>
/// nennen die weitere Tätigkeit, auf der gerade gearbeitet wird (null =
/// Standard-Tätigkeit, fremde Buchung, Pause oder nicht eingestempelt).
/// <see cref="ActiveIsDefaultTask"/> ist nur true, wenn das laufende
/// Arbeits-Timesheet auf Projekt/Aktivität der Standard-Tätigkeit bucht -
/// eine gelöschte Tätigkeit oder eine Buchung aus der Kimai-Oberfläche ist
/// weder weitere noch Standard-Tätigkeit.
/// <see cref="Warning"/>: Die Aktion ist nicht wie gewählt gelungen - der
/// Text sagt, was stattdessen gebucht ist (z. B. ein von Kimai abgelehnter
/// Wechsel). Der Kiosk zeigt ihn statt <see cref="StateText"/> als Meldung.
/// </summary>
public sealed record ClockStatusDto(
    bool IsRunning,
    int? ActiveTimesheetId,
    string? StartedAt,
    int DurationSeconds,
    string State,
    string StateText,
    string? ActiveTaskId = null,
    string? ActiveTaskLabel = null,
    bool ActiveIsDefaultTask = false,
    string? Warning = null);

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
