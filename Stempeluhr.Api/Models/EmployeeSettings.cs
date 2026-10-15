namespace Stempeluhr.Api.Models;

public sealed class EmployeeSettings
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanClock => IsEnabled && !string.IsNullOrWhiteSpace(ApiToken);

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public int? KimaiUserId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? Pin { get; init; }
    public string? NfcCardId { get; init; }
    public string ApiToken { get; init; } = string.Empty;
    public int? ProjectId { get; init; }
    public int? ActivityId { get; init; }
    public string Color { get; init; } = "#2563eb";
    public string? ImageUrl { get; init; }
    public string? Description { get; init; }
    public string[] Tags { get; init; } = [];
    public bool Billable { get; init; } = true;
    public bool IsEnabled { get; init; } = true;

    /// <summary>
    /// Weitere Tätigkeiten (z. B. Arbeit für andere Kunden), auf die der
    /// Mitarbeiter während der Arbeitszeit wechseln kann, ohne auszustempeln.
    /// Die Standard-Tätigkeit ergibt sich weiter aus ProjectId/ActivityId.
    /// </summary>
    public EmployeeTaskSettings[] Tasks { get; init; } = [];

    /// <summary>
    /// Anzeigename der Haupttätigkeit (ProjectId/ActivityId) am Kiosk, z. B.
    /// „Büro“. Null = neutraler Text „Standard-Tätigkeit“.
    /// </summary>
    public string? DefaultTaskLabel { get; init; }

    /// <summary>Personalnummer für den Steuerberater.</summary>
    public string? PersonnelNumber { get; init; }

    /// <summary>Eintrittsdatum; null = Beschäftigung nach vorn offen.</summary>
    public DateOnly? EntryDate { get; init; }

    /// <summary>Austrittsdatum (letzter Arbeitstag); null = unbefristet.</summary>
    public DateOnly? ExitDate { get; init; }

    /// <summary>Jahresurlaub in Tagen, z. B. 25 oder 30.</summary>
    public decimal? VacationDaysPerYear { get; init; }

    /// <summary>Stunden, die pro Urlaubs- bzw. Krankheitstag in Kimai gebucht werden.</summary>
    public decimal? AbsenceDayHours { get; init; }

    /// <summary>
    /// Übertrag und manuelle Korrektur des Urlaubskontos pro Kalenderjahr,
    /// z. B. für Urlaub, der vor Einführung der Abwesenheiten genommen wurde.
    /// </summary>
    private readonly EmployeeVacationYear[] vacationYears = [];
    public EmployeeVacationYear[] VacationYears
    {
        get => vacationYears;
        init => vacationYears = value ?? [];
    }
}

public sealed record EmployeeVacationYear(int Year, decimal CarryOverDays, decimal CorrectionDays, string? Note = null);

public sealed class EmployeeTaskSettings
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Label { get; init; } = string.Empty;
    public int? ProjectId { get; init; }
    public int? ActivityId { get; init; }
    public bool Billable { get; init; } = true;
}
