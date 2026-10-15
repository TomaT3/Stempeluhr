using System.Text.Json.Serialization;

namespace Stempeluhr.Api.Models;

[JsonConverter(typeof(CamelCaseEnumConverter<AbsenceKind>))]
public enum AbsenceKind
{
    Vacation,
    Sick,

    /// <summary>Gleittag zum Abbau von Überstunden, in Kimai mit Dauer 0 gebucht.</summary>
    FlexDay,
}

[JsonConverter(typeof(CamelCaseEnumConverter<AbsenceStatus>))]
public enum AbsenceStatus
{
    /// <summary>Wartet auf die Entscheidung von Chef oder Admin.</summary>
    Pending,

    /// <summary>Genehmigt und für jeden Tag in Kimai eingetragen.</summary>
    Applied,

    Rejected,

    /// <summary>Genehmigt, aber Kimai hat abgelehnt oder ein Tag hatte keinen Platz.</summary>
    Failed,

    Withdrawn,

    /// <summary>Der Admin hat einen <see cref="Failed"/>-Antrag von Hand in Kimai nachgetragen.</summary>
    ResolvedManually,

    /// <summary>Vom Admin storniert, die Einträge in Kimai sind gelöscht.</summary>
    Cancelled,
}

/// <summary>
/// Abwesenheitsantrag (<c>absences.json</c>). Die Tage sind Kalendertage in
/// der Kimai-Zeitzone des Mitarbeiters; jeder Tag zählt als ein Tag.
/// </summary>
public sealed record AbsenceRequest
{
    public const string AdminSource = "admin";

    public required string Id { get; init; }
    public required string EmployeeId { get; init; }
    public required string EmployeeName { get; init; }
    public required AbsenceKind Kind { get; init; }

    /// <summary>Sortiert und eindeutig.</summary>
    public required IReadOnlyList<DateOnly> Days { get; init; }

    /// <summary>Terminal-ID, <c>clock</c> oder <see cref="AdminSource"/>.</summary>
    public required string Source { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Name des Mitarbeiters oder <c>Admin</c>.</summary>
    public required string CreatedBy { get; init; }
    public string? Comment { get; init; }

    public AbsenceStatus Status { get; init; } = AbsenceStatus.Pending;
    public DateTimeOffset? DecidedAt { get; init; }

    /// <summary><c>Admin</c>, der Telegram-Name oder beim Zurückziehen der Mitarbeiter.</summary>
    public string? DecidedBy { get; init; }
    public string? DecisionNote { get; init; }
    public string? Error { get; init; }

    /// <summary>Storno durch den Admin; die Genehmigung (<see cref="DecidedAt"/>) bleibt sichtbar.</summary>
    public DateTimeOffset? CancelledAt { get; init; }
    public string? CancelledBy { get; init; }

    /// <summary>
    /// Gespeichert, bevor das Anwenden zum ersten Mal in Kimai schreibt. Ein
    /// Antrag mit diesem Zeitpunkt ist entschieden, auch wenn er nach einem
    /// Prozessabbruch noch <see cref="AbsenceStatus.Pending"/> ist: In Kimai
    /// können schon Einträge stehen (auch solche, deren ID nicht mehr in
    /// <see cref="AppliedDays"/> gelandet ist). Er lässt sich dann weder
    /// zurückziehen noch ablehnen, nur erneut genehmigen - das setzt fort.
    /// </summary>
    public DateTimeOffset? ApplyStartedAt { get; init; }

    /// <summary>Wartet noch auf eine Entscheidung; ein begonnenes Anwenden zählt als entschieden.</summary>
    [JsonIgnore]
    public bool IsUndecided => Status == AbsenceStatus.Pending && ApplyStartedAt is null;

    /// <summary>
    /// Fortschritt in Kimai: Tag (<c>yyyy-MM-dd</c>) → Timesheet-ID. „Erneut
    /// versuchen“ setzt beim ersten Tag ohne Eintrag fort, das Storno löscht
    /// genau diese Einträge.
    /// </summary>
    public IReadOnlyDictionary<string, int> AppliedDays { get; init; } = new Dictionary<string, int>();

    public long? TelegramChatId { get; init; }
    public long? TelegramMessageId { get; init; }

    [JsonIgnore]
    public bool IsAdminEntry => Source == AdminSource;

    /// <summary>Wartet auf eine Aktion (Entscheidung oder Nacharbeit).</summary>
    [JsonIgnore]
    public bool IsOpen => Status is AbsenceStatus.Pending or AbsenceStatus.Failed;

    /// <summary>Belegt seine Tage: offen oder (auch von Hand) in Kimai eingetragen.</summary>
    [JsonIgnore]
    public bool BlocksDays => IsOpen || Status is AbsenceStatus.Applied or AbsenceStatus.ResolvedManually;

    /// <summary>Zählt als genommen, wie im Urlaubskonto.</summary>
    [JsonIgnore]
    public bool IsTaken => Status is AbsenceStatus.Applied or AbsenceStatus.ResolvedManually;
}

// ---- Requests ----

/// <summary>Antrag vom Kiosk bzw. von /clock; Tage als <c>yyyy-MM-dd</c>.</summary>
public sealed record SubmitAbsenceRequest(
    string? EmployeeId,
    string? Pin,
    string? NfcCardId,
    string? Kind,
    IReadOnlyList<string>? Days,
    string? Comment,
    string? Source)
{
    public CorrectionAuthRequest Auth => new(EmployeeId, Pin, NfcCardId);
}

/// <summary>Der Admin erfasst für einen Mitarbeiter; gilt sofort als genehmigt.</summary>
public sealed record AdminCreateAbsenceRequest(string? EmployeeId, string? Kind, IReadOnlyList<string>? Days, string? Comment);

public sealed record UpdateVacationYearRequest(decimal CarryOverDays, decimal CorrectionDays, string? Note);

// ---- Responses ----

/// <summary>
/// Urlaubskonto eines Kalenderjahres. <see cref="Remaining"/> = Anspruch +
/// Übertrag + Korrektur − <see cref="Taken"/> − <see cref="Planned"/>.
/// </summary>
public sealed record VacationBalanceDto(
    int Year,
    decimal Entitlement,
    decimal CarryOver,
    decimal Correction,
    decimal Taken,
    decimal Planned,
    decimal Remaining);

public sealed record AbsenceDto(
    string Id,
    string EmployeeId,
    string EmployeeName,
    AbsenceKind Kind,
    AbsenceStatus Status,
    IReadOnlyList<string> Days,
    string Source,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    string? Comment,
    DateTimeOffset? DecidedAt,
    string? DecidedBy,
    string? DecisionNote,
    string? Error,
    // Tage, die schon in Kimai stehen (yyyy-MM-dd).
    IReadOnlyList<string> AppliedDays,
    DateTimeOffset? CancelledAt,
    string? CancelledBy,
    // Pending mit diesem Zeitpunkt: Anwenden wurde unterbrochen, erneut genehmigen setzt fort.
    DateTimeOffset? ApplyStartedAt)
{
    /// <summary>Nur bei der Admin-Erfassung: z. B. Urlaub über dem Rest.</summary>
    public string? Warning { get; init; }
}

/// <summary>Ein belegter Tag für den Kalender am Kiosk.</summary>
public sealed record AbsenceDayDto(string Day, AbsenceKind Kind, AbsenceStatus Status);

/// <summary>Zeitfenster, in dem eine Art beantragt werden kann; <see cref="Missing"/> nennt fehlende Einstellungen.</summary>
public sealed record AbsenceKindDto(AbsenceKind Kind, string Earliest, string Latest, string? Missing);

/// <summary>Kontostand und Kalenderdaten für den Kiosk.</summary>
public sealed record KioskAbsenceOverviewDto(
    string Today,
    IReadOnlyList<VacationBalanceDto> Balances,
    IReadOnlyList<AbsenceKindDto> Kinds,
    IReadOnlyList<AbsenceDayDto> Days);

/// <summary>Urlaubskonto eines Mitarbeiters für die Admin-Seite.</summary>
public sealed record AdminVacationBalanceDto(
    string EmployeeId,
    string EmployeeName,
    string? PersonnelNumber,
    bool IsEnabled,
    decimal? VacationDaysPerYear,
    string? Note,
    VacationBalanceDto Balance)
{
    /// <summary>Volle Beschäftigungsmonate im Jahr, Grundlage des Anspruchs.</summary>
    public int FullMonths { get; init; }
    public DateOnly? EntryDate { get; init; }
    public DateOnly? ExitDate { get; init; }
}
