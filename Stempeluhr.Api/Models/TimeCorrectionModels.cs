using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stempeluhr.Api.Models;

/// <summary>JSON as camelCase text (<c>addPause</c>), like the kiosk and admin clients expect it.</summary>
public sealed class CamelCaseEnumConverter<T>() : JsonStringEnumConverter<T>(JsonNamingPolicy.CamelCase)
    where T : struct, Enum;

[JsonConverter(typeof(CamelCaseEnumConverter<TimeCorrectionKind>))]
public enum TimeCorrectionKind
{
    /// <summary>Pause von/bis in ein Arbeits-Timesheet eintragen, auch in ein laufendes.</summary>
    AddPause,

    /// <summary>Tatsächliches Ende eines Timesheets setzen (vor dem aktuellen Ende).</summary>
    SetEnd,

    /// <summary>Schicht (Einstempeln bis Ausstempeln), optional mit Pause, nachtragen.</summary>
    AddShift,

    /// <summary>Beginn und/oder Ende eines gestoppten Timesheets ändern.</summary>
    ChangeTimes,
}

[JsonConverter(typeof(CamelCaseEnumConverter<TimeCorrectionStatus>))]
public enum TimeCorrectionStatus
{
    /// <summary>Wartet auf die Entscheidung von Chef oder Admin.</summary>
    Pending,

    /// <summary>Genehmigt und vollständig in Kimai geschrieben.</summary>
    Applied,

    Rejected,

    /// <summary>Genehmigt, aber Kimai hat abgelehnt oder der Eintrag hat sich geändert.</summary>
    Failed,

    Withdrawn,

    /// <summary>Der Admin hat einen <see cref="Failed"/>-Antrag von Hand in Kimai nachgetragen.</summary>
    ResolvedManually,
}

/// <summary>
/// Snapshot des betroffenen Timesheets zum Zeitpunkt des Antrags. Beim
/// Anwenden wird das Timesheet neu gelesen und damit verglichen: Weicht es ab,
/// wird nie geraten, sondern der Antrag scheitert.
/// </summary>
public sealed record TimeCorrectionOriginal(
    DateTimeOffset Begin,
    DateTimeOffset? End,
    int ActivityId,
    int ProjectId,
    string? Description,
    bool Billable);

/// <summary>
/// Korrekturantrag eines Mitarbeiters (<c>time-corrections.json</c>). Alle
/// Zeiten sind absolute Zeitpunkte; <see cref="TimeZoneId"/> hält die
/// Kimai-Zeitzone des Mitarbeiters fest, in der sie angezeigt werden.
/// </summary>
public sealed record TimeCorrectionRequest
{
    public required string Id { get; init; }
    public required string EmployeeId { get; init; }
    public required string EmployeeName { get; init; }
    public required TimeCorrectionKind Kind { get; init; }

    /// <summary>Terminal-ID oder <c>clock</c>.</summary>
    public required string Source { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? Comment { get; init; }
    public string? TimeZoneId { get; init; }

    public int? TimesheetId { get; init; }
    public DateTimeOffset? Begin { get; init; }
    public DateTimeOffset? End { get; init; }
    public DateTimeOffset? PauseBegin { get; init; }
    public DateTimeOffset? PauseEnd { get; init; }

    /// <summary>Tätigkeit der nachgetragenen Schicht; leer = Haupttätigkeit.</summary>
    public string? TaskId { get; init; }
    public TimeCorrectionOriginal? Original { get; init; }

    public TimeCorrectionStatus Status { get; init; } = TimeCorrectionStatus.Pending;
    public DateTimeOffset? DecidedAt { get; init; }

    /// <summary><c>Admin</c> oder der Telegram-Name.</summary>
    public string? DecidedBy { get; init; }
    public string? DecisionNote { get; init; }
    public string? Error { get; init; }

    /// <summary>Schritte, die in Kimai schon ausgeführt sind (Fortschritt für „Erneut versuchen“).</summary>
    public IReadOnlyList<string> AppliedSteps { get; init; } = [];

    /// <summary>
    /// Nur bei einer Pause in einem laufenden Eintrag: <see cref="ObservedEndAtApply"/>
    /// wurde beim Genehmigen vor dem ersten Schritt festgehalten. Ohne dieses
    /// Flag ist ein leeres Ende nur „noch nicht gelesen“, nicht „läuft noch“.
    /// </summary>
    public bool ObservedAtApply { get; init; }

    /// <summary>
    /// Ende des Eintrags, wie es beim Genehmigen vor dem ersten Schritt in Kimai
    /// stand (null = lief noch). Gespeichert, damit „Erneut versuchen“ nach dem
    /// Kürzen weiß, ob die Rest-Arbeit laufend gestartet oder bis zu diesem Ende
    /// angelegt wird. Nur gültig mit <see cref="ObservedAtApply"/>.
    /// </summary>
    public DateTimeOffset? ObservedEndAtApply { get; init; }

    public long? TelegramChatId { get; init; }
    public long? TelegramMessageId { get; init; }

    /// <summary>Wartet auf eine Aktion (Entscheidung oder Nacharbeit) und fällt nie aus dem Store.</summary>
    [JsonIgnore]
    public bool IsOpen => Status is TimeCorrectionStatus.Pending or TimeCorrectionStatus.Failed;
}

// ---- Kiosk requests (Auth wie /api/kiosk/clock: EmployeeId + PIN oder Karte) ----

/// <summary>Auth-Teil jeder Kiosk-Anfrage: Mitarbeiter-ID plus PIN <b>oder</b> Karten-ID.</summary>
public sealed record CorrectionAuthRequest(string? EmployeeId, string? Pin, string? NfcCardId);

/// <summary>
/// Korrekturantrag vom Kiosk bzw. von /clock. Zeiten sind lokale Zeit in der
/// Kimai-Zeitzone des Mitarbeiters (<c>yyyy-MM-ddTHH:mm</c>).
/// </summary>
public sealed record SubmitCorrectionRequest(
    string? EmployeeId,
    string? Pin,
    string? NfcCardId,
    string? Kind,
    int? TimesheetId,
    string? Begin,
    string? End,
    string? PauseBegin,
    string? PauseEnd,
    string? TaskId,
    string? Comment,
    string? Source)
{
    public CorrectionAuthRequest Auth => new(EmployeeId, Pin, NfcCardId);
}

public sealed record RejectCorrectionRequest(string? Note);

// ---- Responses ----

public sealed record CorrectionEntryDto(
    int Id,
    string Begin,
    string? End,
    // "work" oder "pause"
    string Kind,
    // Anzeigename: "Arbeit", "Pause" oder die Bezeichnung der Tätigkeit
    string Label,
    bool HasOpenRequest);

public sealed record CorrectionShiftDto(string Begin, string? End, IReadOnlyList<CorrectionEntryDto> Entries);

/// <summary>Auswahlliste: Schichten der letzten 31 Tage, neueste zuerst.</summary>
public sealed record CorrectionTimesheetsDto(string TimeZone, IReadOnlyList<CorrectionShiftDto> Shifts);

/// <summary>
/// Ein Hinweis auf auffällige Arbeitszeit. <paramref name="Kind"/>:
/// <c>continuous</c> (über 6 h am Stück ohne Pause) oder <c>shift</c> (über
/// 10 h in der Schicht), nur aus der jüngsten Schicht. Zeiten lokal (<c>yyyy-MM-ddTHH:mm</c>);
/// <paramref name="End"/> null = die Arbeit läuft noch.
/// <paramref name="TimesheetId"/> (nur bei <c>continuous</c>): das laufende
/// Arbeits-Timesheet, sonst der längste gestoppte Arbeits-Eintrag des Blocks.
/// </summary>
public sealed record WorkTimeHintDto(string Kind, string Begin, string? End, int WorkedSeconds, int? TimesheetId);

/// <summary>Hinweise nach der Anmeldung (rein lesend), Zeiten in <paramref name="TimeZone"/>.</summary>
public sealed record WorkTimeHintsDto(string TimeZone, IReadOnlyList<WorkTimeHintDto> Hints);

public sealed record CorrectionOriginalDto(
    string Begin,
    string? End,
    string? Description,
    // "work" oder "pause", wie bei CorrectionEntryDto: auch Pausen-Einträge
    // lassen sich mit setEnd/changeTimes korrigieren.
    string Kind,
    string Label);

/// <summary>Ein Antrag für Kiosk und Admin-Seite; Zeiten lokal (<c>yyyy-MM-ddTHH:mm</c>) mit Zeitzonenname.</summary>
public sealed record TimeCorrectionDto(
    string Id,
    string EmployeeId,
    string EmployeeName,
    TimeCorrectionKind Kind,
    TimeCorrectionStatus Status,
    string Source,
    DateTimeOffset CreatedAt,
    string? Comment,
    string TimeZone,
    int? TimesheetId,
    string? Begin,
    string? End,
    string? PauseBegin,
    string? PauseEnd,
    string? TaskId,
    string? TaskLabel,
    CorrectionOriginalDto? Original,
    DateTimeOffset? DecidedAt,
    string? DecidedBy,
    string? DecisionNote,
    string? Error,
    IReadOnlyList<string> AppliedSteps,
    // Pause im laufenden Eintrag: beim Genehmigen festgehaltenes Ende
    // (null mit ObservedAtApply = lief noch).
    bool ObservedAtApply,
    string? ObservedEndAtApply);

public enum CorrectionOutcome
{
    Ok,
    Unauthorized,
    NotFound,
    Conflict,
    Invalid,

    /// <summary>Kimai ist gerade nicht erreichbar - es gibt bewusst keine Offline-Queue.</summary>
    Unavailable,
}

/// <summary>Ergebnis einer Service-Operation; die Endpunkte bilden <see cref="Outcome"/> auf HTTP ab.</summary>
public sealed record CorrectionResult<T>(CorrectionOutcome Outcome, T? Value, string? Message = null)
{
    public static CorrectionResult<T> Ok(T value) => new(CorrectionOutcome.Ok, value);
    public static CorrectionResult<T> Fail(CorrectionOutcome outcome, string message) => new(outcome, default, message);
}
