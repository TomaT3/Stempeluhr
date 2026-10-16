using System.Globalization;
using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

public interface IMonthlyExportService
{
    /// <summary>Die Arbeitsmappe des Monats (.xlsx) für den Steuerberater.</summary>
    Task<MonthlyExportFile> ExportAsync(int year, int month, CancellationToken cancellationToken = default);
}

public sealed record MonthlyExportFile(string FileName, byte[] Content);

/// <summary>Kennzahlen eines Mitarbeiters; <c>null</c>, wenn Kimai nicht erreichbar war.</summary>
public sealed record MonthlyExportSummary(
    string? PersonnelNumber,
    string Name,
    int? WorkDays,
    decimal? WorkHours,
    int? VacationDays,
    int? SickDays,
    int? FlexDays,
    decimal? VacationSickHours,
    decimal? RemainingVacation,
    IReadOnlyList<string> Notes);

public sealed record MonthlyExportAbsence(
    string? PersonnelNumber, string Name, DateOnly Day, string Kind, decimal Hours, string Status, string RecordedBy);

/// <summary>Eine Schicht: zusammenhängende Einträge, <see cref="End"/> ist null, wenn sie noch läuft.</summary>
public sealed record MonthlyExportShift(
    string? PersonnelNumber, string Name, DateOnly Day, TimeOnly Begin, TimeOnly? End,
    int PauseMinutes, decimal NetHours, string Task);

public sealed record MonthlyExportEmployee(
    MonthlyExportSummary Summary,
    IReadOnlyList<MonthlyExportAbsence> Absences,
    IReadOnlyList<MonthlyExportShift> Shifts);

public sealed record MonthlyExportReport(
    int Year, int Month, IReadOnlyList<MonthlyExportEmployee> Employees, IReadOnlyList<string> Notes);

/// <summary>
/// Monatsexport für den Steuerberater. Quelle der Zeiten und Abwesenheiten
/// ist Kimai (mit dem Token jedes Mitarbeiters), der Abwesenheitsspeicher
/// reichert nur an und liefert Resturlaub sowie Hinweise. Ein Eintrag zählt
/// in dem Monat, in dem er BEGINNT - eine Nachtschicht vom 31. auf den 1.
/// gehört komplett zum Beginnmonat (wie im <see cref="HoursOverviewCalculator"/>).
/// </summary>
public sealed class MonthlyExportService(
    IRuntimeSettingsStore settingsStore,
    IKimaiClient kimai,
    AbsenceStore absenceStore,
    ILogger<MonthlyExportService>? logger = null) : IMonthlyExportService
{
    private const int MaxParallelEmployees = 4;

    // Einträge, zwischen denen weniger als das liegt, gehören zur selben Schicht
    // (Arbeit - Pause - Arbeit, Tätigkeitswechsel), sonst beginnt eine neue.
    private static readonly TimeSpan ShiftGap = TimeSpan.FromMinutes(5);

    public async Task<MonthlyExportFile> ExportAsync(int year, int month, CancellationToken cancellationToken = default)
    {
        var report = await BuildReportAsync(year, month, cancellationToken);
        return new MonthlyExportFile(FileName(year, month), XlsxWriter.Write(ToSheets(report)));
    }

    public static string FileName(int year, int month)
        => $"Stempeluhr-Export-{year.ToString("0000", CultureInfo.InvariantCulture)}-{month.ToString("00", CultureInfo.InvariantCulture)}.xlsx";

    public async Task<MonthlyExportReport> BuildReportAsync(int year, int month, CancellationToken cancellationToken = default)
    {
        var settings = settingsStore.Load();
        var firstDay = new DateOnly(year, month, 1);
        var lastDay = firstDay.AddMonths(1).AddDays(-1);
        var requests = absenceStore.List();

        var employees = settings.Employees
            .Where(employee => employee.IsEnabled
                && (employee.EntryDate is null || employee.EntryDate <= lastDay)
                && (employee.ExitDate is null || employee.ExitDate >= firstDay))
            .OrderBy(employee => employee.PersonnelNumber ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(employee => employee.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        using var gate = new SemaphoreSlim(MaxParallelEmployees);
        var results = await Task.WhenAll(employees.Select(async employee =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return await BuildEmployeeAsync(settings, employee, firstDay, lastDay, requests, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }));

        var notes = new List<string>();
        if (!settings.AbsencesConfigured)
        {
            notes.Add("Abwesenheits-Einstellungen unvollständig: Abwesenheiten sind nicht ausgewertet.");
        }
        return new MonthlyExportReport(year, month, results, notes);
    }

    private async Task<MonthlyExportEmployee> BuildEmployeeAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        DateOnly firstDay,
        DateOnly lastDay,
        IReadOnlyList<AbsenceRequest> requests,
        CancellationToken cancellationToken)
    {
        var name = employee.DisplayName;
        var mine = requests.Where(request => request.EmployeeId == employee.Id).ToArray();
        var notes = new List<string>();

        decimal? remaining = employee.VacationDaysPerYear is null ? null
            : VacationEntitlementCalculator.Calculate(employee, firstDay.Year, mine, lastDay).Remaining;

        var absences = new List<MonthlyExportAbsence>();
        var shifts = new List<MonthlyExportShift>();
        int? workDays = null, vacationDays = null, sickDays = null, flexDays = null;
        decimal? workHours = null, vacationSickHours = null;

        IReadOnlyList<KimaiTimesheetEntryDto>? entries = null;
        TimeZoneInfo timeZone = TimeZoneInfo.Local;
        if (string.IsNullOrWhiteSpace(employee.ApiToken))
        {
            notes.Add("Kein API-Token hinterlegt: Kimai wurde nicht abgefragt.");
        }
        else
        {
            try
            {
                timeZone = ClockService.ResolveTimezone(
                    await kimai.GetCurrentUserTimezoneAsync(settings, employee, cancellationToken));
                var all = await kimai.GetTimesheetsAsync(
                    settings, employee,
                    firstDay.ToDateTime(TimeOnly.MinValue),
                    lastDay.AddDays(1).ToDateTime(TimeOnly.MinValue).AddSeconds(-1),
                    cancellationToken);
                // Maßgeblich ist der Beginn in der Zeitzone des Mitarbeiters.
                entries = all
                    .Where(entry => entry.Begin is not null && InMonth(entry.Begin.Value, timeZone, firstDay))
                    .OrderBy(entry => entry.Begin)
                    .ThenBy(entry => entry.Id)
                    .ToArray();
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger?.LogWarning(exception, "Monatsexport: Kimai für {Employee} nicht erreichbar.", name);
                notes.Add("Kimai nicht erreichbar: Arbeitszeiten und Abwesenheiten fehlen.");
            }
        }

        if (entries is not null)
        {
            (workDays, workHours, vacationDays, sickDays, flexDays, vacationSickHours) =
                Evaluate(settings, employee, entries, timeZone, mine, absences, shifts, notes);
            AddRequestNotes(mine, entries, settings, timeZone, firstDay, lastDay, notes);
        }
        else
        {
            AddOpenRequestNotes(mine, firstDay, lastDay, notes);
        }

        var summary = new MonthlyExportSummary(
            employee.PersonnelNumber, name, workDays, workHours, vacationDays, sickDays, flexDays,
            vacationSickHours, remaining, notes);
        return new MonthlyExportEmployee(summary, absences, shifts);
    }

    private static (int WorkDays, decimal WorkHours, int Vacation, int Sick, int Flex, decimal VacationSickHours) Evaluate(
        RuntimeSettings settings,
        EmployeeSettings employee,
        IReadOnlyList<KimaiTimesheetEntryDto> entries,
        TimeZoneInfo timeZone,
        IReadOnlyList<AbsenceRequest> requests,
        List<MonthlyExportAbsence> absences,
        List<MonthlyExportShift> shifts,
        List<string> notes)
    {
        var workEntries = new List<KimaiTimesheetEntryDto>();
        var workDays = new HashSet<DateOnly>();
        var daysByKind = AbsenceKinds.All.ToDictionary(kind => kind, _ => new HashSet<DateOnly>());
        var workSeconds = 0L;
        var absenceSeconds = 0L;
        var running = false;
        var unknownAbsence = false;

        foreach (var entry in entries)
        {
            var day = LocalDay(entry.Begin!.Value, timeZone);
            // Vor der Verzweigung: auch eine laufende Abwesenheit ist unvollständig.
            if (entry.End is null)
            {
                running = true;
            }

            var isAbsenceProject = settings.AbsenceProjectId is { } project && entry.ProjectId == project;
            if (isAbsenceProject)
            {
                // Unvollständige Einstellungen: gar nicht auswerten statt teilweise
                // (die Summenzeile sagt "nicht ausgewertet"); Arbeit ist es trotzdem nicht.
                if (!settings.AbsencesConfigured)
                {
                    continue;
                }

                var kind = entry.ActivityId is null ? null : AbsenceKinds.KindOf(entry, settings);
                if (kind is null)
                {
                    unknownAbsence = true;
                    continue;
                }

                daysByKind[kind.Value].Add(day);
                var seconds = DurationSeconds(entry);
                if (kind != AbsenceKind.FlexDay)
                {
                    absenceSeconds += seconds;
                }
                var request = FindRequest(requests, entry, kind.Value, day);
                absences.Add(new MonthlyExportAbsence(
                    employee.PersonnelNumber, employee.DisplayName, day, AbsenceKinds.Label(kind.Value),
                    Hours(seconds),
                    entry.End is null ? "läuft noch (nicht abgeschlossen)"
                        : request is null ? "Eintrag in Kimai" : StatusLabel(request.Status),
                    request is null ? "direkt in Kimai" : request.CreatedBy));
                continue;
            }

            workEntries.Add(entry);
            if (!IsPause(entry, settings))
            {
                workDays.Add(day);
                workSeconds += DurationSeconds(entry);
            }
        }

        shifts.AddRange(BuildShifts(settings, employee, workEntries, timeZone));

        if (running)
        {
            notes.Add("Ein Eintrag läuft noch: seine Stunden sind nicht gezählt.");
        }
        if (unknownAbsence)
        {
            notes.Add("Einträge auf dem Abwesenheitsprojekt mit unbekannter Aktivität sind nicht gezählt.");
        }

        return (
            workDays.Count,
            Hours(workSeconds),
            daysByKind[AbsenceKind.Vacation].Count,
            daysByKind[AbsenceKind.Sick].Count,
            daysByKind[AbsenceKind.FlexDay].Count,
            Hours(absenceSeconds));
    }

    /// <summary>
    /// Fasst aufeinanderfolgende Arbeits- und Pausen-Einträge zu Schichten
    /// zusammen. Die Schicht gehört zum Tag ihres Beginns (Nachtschicht).
    /// </summary>
    private static IEnumerable<MonthlyExportShift> BuildShifts(
        RuntimeSettings settings,
        EmployeeSettings employee,
        IReadOnlyList<KimaiTimesheetEntryDto> entries,
        TimeZoneInfo timeZone)
    {
        var group = new List<KimaiTimesheetEntryDto>();
        foreach (var entry in entries)
        {
            if (group.Count > 0 && !Continues(group[^1], entry))
            {
                yield return ToShift(settings, employee, group, timeZone);
                group.Clear();
            }
            group.Add(entry);
        }
        if (group.Count > 0)
        {
            yield return ToShift(settings, employee, group, timeZone);
        }
    }

    private static bool Continues(KimaiTimesheetEntryDto previous, KimaiTimesheetEntryDto next)
        => previous.End is { } end && next.Begin!.Value - end <= ShiftGap;

    private static MonthlyExportShift ToShift(
        RuntimeSettings settings, EmployeeSettings employee, List<KimaiTimesheetEntryDto> group, TimeZoneInfo timeZone)
    {
        var begin = TimeZoneInfo.ConvertTime(group[0].Begin!.Value, timeZone);
        var last = group[^1];
        TimeOnly? end = last.End is { } stop ? TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(stop, timeZone).DateTime) : null;
        var pauseSeconds = group.Where(entry => IsPause(entry, settings)).Sum(DurationSeconds);
        var workSeconds = group.Where(entry => !IsPause(entry, settings)).Sum(DurationSeconds);
        var tasks = group
            .Where(entry => !IsPause(entry, settings))
            .Select(entry => TaskLabel(settings, employee, entry))
            .Distinct()
            .ToArray();
        return new MonthlyExportShift(
            employee.PersonnelNumber, employee.DisplayName, DateOnly.FromDateTime(begin.DateTime),
            TimeOnly.FromDateTime(begin.DateTime), end,
            (int)Math.Round(pauseSeconds / 60.0, MidpointRounding.AwayFromZero), Hours(workSeconds),
            string.Join(", ", tasks));
    }

    private static string TaskLabel(RuntimeSettings settings, EmployeeSettings employee, KimaiTimesheetEntryDto entry)
    {
        var task = employee.Tasks.FirstOrDefault(candidate =>
            candidate.ProjectId == entry.ProjectId && candidate.ActivityId == entry.ActivityId);
        if (task is not null && !string.IsNullOrWhiteSpace(task.Label))
        {
            return task.Label;
        }
        if (WorkTargetResolver.IsDefault(settings, employee, entry.ProjectId, entry.ActivityId))
        {
            return string.IsNullOrWhiteSpace(employee.DefaultTaskLabel) ? "Arbeit" : employee.DefaultTaskLabel;
        }
        return $"Kimai-Projekt {entry.ProjectId}, Aktivität {entry.ActivityId}";
    }

    // ------------------------------------------------------------ Hinweise

    private static void AddRequestNotes(
        IReadOnlyList<AbsenceRequest> requests,
        IReadOnlyList<KimaiTimesheetEntryDto> entries,
        RuntimeSettings settings,
        TimeZoneInfo timeZone,
        DateOnly firstDay,
        DateOnly lastDay,
        List<string> notes)
    {
        if (settings.AbsencesConfigured)
        {
            var booked = entries
                .Select(entry => (Kind: entry.ActivityId is null ? null : AbsenceKinds.KindOf(entry, settings),
                    Day: LocalDay(entry.Begin!.Value, timeZone)))
                .Where(item => item.Kind is not null)
                .Select(item => (item.Kind!.Value, item.Day))
                .ToHashSet();

            foreach (var request in requests.Where(request => request.IsTaken))
            {
                var missing = request.Days
                    .Where(day => InRange(day, firstDay, lastDay) && !booked.Contains((request.Kind, day)))
                    .ToArray();
                if (missing.Length > 0)
                {
                    notes.Add($"{AbsenceKinds.Label(request.Kind)} laut Antrag ohne Kimai-Eintrag: {FormatDays(missing)}.");
                }
            }
        }

        AddOpenRequestNotes(requests, firstDay, lastDay, notes);
    }

    private static void AddOpenRequestNotes(
        IReadOnlyList<AbsenceRequest> requests, DateOnly firstDay, DateOnly lastDay, List<string> notes)
    {
        foreach (var request in requests.Where(request => request.IsOpen))
        {
            var days = request.Days.Where(day => InRange(day, firstDay, lastDay)).ToArray();
            if (days.Length > 0)
            {
                var state = request.Status == AbsenceStatus.Failed ? "Fehlgeschlagener" : "Offener";
                notes.Add($"{state} Antrag ({AbsenceKinds.Label(request.Kind)}): {FormatDays(days)}.");
            }
        }
    }

    // ------------------------------------------------------------ Hilfen

    private static AbsenceRequest? FindRequest(IReadOnlyList<AbsenceRequest> requests, KimaiTimesheetEntryDto entry, AbsenceKind kind, DateOnly day)
        => requests.FirstOrDefault(request => request.AppliedDays.Values.Contains(entry.Id))
            ?? requests.FirstOrDefault(request => request.IsTaken && request.Kind == kind && request.Days.Contains(day));

    private static string StatusLabel(AbsenceStatus status) => status switch
    {
        AbsenceStatus.Applied => "Genehmigt",
        AbsenceStatus.ResolvedManually => "Genehmigt (von Hand eingetragen)",
        AbsenceStatus.Pending => "Offen",
        AbsenceStatus.Failed => "Fehlgeschlagen",
        AbsenceStatus.Rejected => "Abgelehnt",
        AbsenceStatus.Withdrawn => "Zurückgezogen",
        _ => "Storniert",
    };

    private static bool IsPause(KimaiTimesheetEntryDto entry, RuntimeSettings settings)
        => settings.PauseActivityId is { } pause && entry.ActivityId == pause;

    private static long DurationSeconds(KimaiTimesheetEntryDto entry)
    {
        if (entry.End is null)
        {
            return 0; // läuft noch: Kimai liefert duration=0, der Eintrag ist nicht abgeschlossen
        }
        return entry.DurationSeconds is > 0
            ? entry.DurationSeconds.Value
            : (long)Math.Max(0, (entry.End.Value - entry.Begin!.Value).TotalSeconds);
    }

    private static decimal Hours(long seconds) => Math.Round(seconds / 3600m, 2, MidpointRounding.AwayFromZero);

    private static DateOnly LocalDay(DateTimeOffset value, TimeZoneInfo timeZone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, timeZone).DateTime);

    private static bool InMonth(DateTimeOffset begin, TimeZoneInfo timeZone, DateOnly firstDay)
    {
        var day = LocalDay(begin, timeZone);
        return day.Year == firstDay.Year && day.Month == firstDay.Month;
    }

    private static bool InRange(DateOnly day, DateOnly first, DateOnly last) => day >= first && day <= last;

    private static string FormatDays(IEnumerable<DateOnly> days) => string.Join(", ", days.Select(AbsenceKinds.Format));

    // ------------------------------------------------------------ Excel

    public static IReadOnlyList<XlsxSheet> ToSheets(MonthlyExportReport report)
    {
        var overview = new XlsxSheet("Übersicht",
            ["Personalnummer", "Name", "Arbeitstage", "Arbeitsstunden", "Urlaubstage", "Krankheitstage", "Gleittage",
                "Std. Urlaub/Krank", "Resturlaub", "Hinweise"],
            [16, 28, 13, 16, 13, 15, 11, 18, 12, 70]);
        foreach (var employee in report.Employees)
        {
            var s = employee.Summary;
            overview.AddRow(s.PersonnelNumber, s.Name, s.WorkDays, s.WorkHours, s.VacationDays, s.SickDays, s.FlexDays,
                s.VacationSickHours, s.RemainingVacation, string.Join(" ", s.Notes));
        }
        var summaries = report.Employees.Select(employee => employee.Summary).ToArray();
        overview.AddBoldRow(null, "Summe",
            summaries.Sum(s => s.WorkDays ?? 0),
            summaries.Sum(s => s.WorkHours ?? 0m),
            summaries.Sum(s => s.VacationDays ?? 0),
            summaries.Sum(s => s.SickDays ?? 0),
            summaries.Sum(s => s.FlexDays ?? 0),
            summaries.Sum(s => s.VacationSickHours ?? 0m),
            null,
            string.Join(" ", report.Notes));

        var absences = new XlsxSheet("Abwesenheiten",
            ["Personalnummer", "Name", "Datum", "Art", "Stunden", "Status", "erfasst von"],
            [16, 28, 13, 12, 10, 30, 24]);
        foreach (var absence in report.Employees.SelectMany(employee => employee.Absences))
        {
            absences.AddRow(absence.PersonnelNumber, absence.Name, absence.Day, absence.Kind, absence.Hours,
                absence.Status, absence.RecordedBy);
        }

        var shifts = new XlsxSheet("Arbeitszeiten",
            ["Personalnummer", "Name", "Datum", "Beginn", "Ende", "Pause (min)", "netto (Std.)", "Tätigkeit"],
            [16, 28, 13, 9, 9, 12, 13, 36]);
        foreach (var shift in report.Employees.SelectMany(employee => employee.Shifts))
        {
            shifts.AddRow(shift.PersonnelNumber, shift.Name, shift.Day, shift.Begin, shift.End, shift.PauseMinutes,
                shift.NetHours, shift.Task);
        }

        return [overview, absences, shifts];
    }
}
