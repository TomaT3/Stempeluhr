using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

/// <summary>Was der <see cref="AbsenceValidator"/> für einen Antrag braucht.</summary>
/// <param name="Others">Die übrigen Anträge des Mitarbeiters (ohne den geprüften).</param>
/// <param name="Timesheets">Kimai-Einträge rund um die betroffenen Tage (ab dem Vortag).</param>
/// <param name="Applying">
/// Beim Anwenden: ein Eintrag derselben Art am Tag gehört zum Antrag (eine
/// verlorene Antwort aus einem früheren Versuch) und ist kein Konflikt.
/// </param>
public sealed record AbsenceValidationInput(
    AbsenceKind Kind,
    IReadOnlyList<DateOnly> Days,
    bool IsAdmin,
    DateOnly Today,
    EmployeeSettings Employee,
    RuntimeSettings Settings,
    IEnumerable<AbsenceRequest> Others,
    IEnumerable<KimaiTimesheetEntryDto> Timesheets,
    TimeZoneInfo TimeZone,
    string? Comment = null,
    bool Applying = false);

/// <summary>Fehler (deutsch, für Mitarbeiter und Admin) oder null; bei Admins zusätzlich eine Warnung.</summary>
public sealed record AbsenceValidationResult(string? Error, string? Warning = null);

/// <summary>
/// Regeln für Abwesenheitsanträge, rein und ohne I/O. Geprüft wird beim
/// Absenden und erneut beim Anwenden (dann mit den Rechten des Admins, der
/// genehmigt).
/// </summary>
public static class AbsenceValidator
{
    public const int MaxDays = 62;
    public const int MaxCommentLength = 300;
    public const int EmployeeLookbackDays = 31;
    public const int AdminLookbackDays = 90;

    /// <summary>Frühester und spätester Tag, den man für eine Art wählen kann.</summary>
    public static (DateOnly Earliest, DateOnly Latest) Window(AbsenceKind kind, DateOnly today, bool isAdmin)
    {
        var earliest = today.AddDays(-(isAdmin ? AdminLookbackDays : EmployeeLookbackDays));
        var latest = kind switch
        {
            AbsenceKind.Vacation => today.AddMonths(18),
            AbsenceKind.Sick => today.AddDays(14),
            _ => today.AddMonths(12),
        };
        return (earliest, latest);
    }

    /// <summary>Was für diese Art noch eingerichtet werden muss; null, wenn alles da ist.</summary>
    public static string? MissingSettings(AbsenceKind kind, RuntimeSettings settings, EmployeeSettings employee)
    {
        var missing = new List<string>();
        if (settings.AbsenceProjectId is null)
        {
            missing.Add("Abwesenheits-Projekt");
        }
        if (AbsenceKinds.ActivityId(kind, settings) is null)
        {
            missing.Add($"Aktivität {AbsenceKinds.Label(kind)}");
        }
        if (settings.AbsenceDayStartTime is null)
        {
            missing.Add("Beginn der Abwesenheitseinträge");
        }
        if (kind != AbsenceKind.FlexDay && employee.AbsenceDayHours is null)
        {
            missing.Add("Stunden pro Abwesenheitstag des Mitarbeiters");
        }
        if (kind == AbsenceKind.Vacation && employee.VacationDaysPerYear is null)
        {
            missing.Add("Urlaubstage pro Jahr des Mitarbeiters");
        }

        return missing.Count == 0
            ? null
            : $"{AbsenceKinds.Label(kind)} ist noch nicht eingerichtet. Es fehlt: {string.Join(", ", missing)}.";
    }

    public static AbsenceValidationResult Validate(AbsenceValidationInput input)
    {
        var (kind, days, settings, employee) = (input.Kind, input.Days, input.Settings, input.Employee);
        if (MissingSettings(kind, settings, employee) is { } missing)
        {
            return new(missing);
        }

        if (days.Count == 0)
        {
            return new("Bitte mindestens einen Tag wählen.");
        }

        if (days.Count > MaxDays)
        {
            return new($"Ein Antrag umfasst höchstens {MaxDays} Tage.");
        }

        if (input.Comment?.Length > MaxCommentLength)
        {
            return new($"Der Kommentar darf höchstens {MaxCommentLength} Zeichen haben.");
        }

        var (earliest, latest) = Window(kind, input.Today, input.IsAdmin);
        foreach (var day in days)
        {
            if (day < earliest)
            {
                return new($"Der {AbsenceKinds.Format(day)} liegt zu weit zurück (frühestens {AbsenceKinds.Format(earliest)}).");
            }
            if (day > latest)
            {
                return new($"Der {AbsenceKinds.Format(day)} liegt zu weit in der Zukunft (spätestens {AbsenceKinds.Format(latest)}).");
            }
            if (employee.EntryDate is { } entry && day < entry)
            {
                return new($"Der {AbsenceKinds.Format(day)} liegt vor dem Eintritt am {AbsenceKinds.Format(entry)}.");
            }
            if (employee.ExitDate is { } exit && day > exit)
            {
                return new($"Der {AbsenceKinds.Format(day)} liegt nach dem Austritt am {AbsenceKinds.Format(exit)}.");
            }
        }

        var others = input.Others.Where(other => other.EmployeeId == employee.Id).ToArray();
        foreach (var day in days)
        {
            if (others.FirstOrDefault(other => other.BlocksDays && other.Days.Contains(day)) is { } blocking)
            {
                return new($"Für den {AbsenceKinds.Format(day)} gibt es schon einen Antrag ({AbsenceKinds.Label(blocking.Kind)}).");
            }
        }

        if (CheckTimesheets(input) is { } timesheetError)
        {
            return new(timesheetError);
        }

        return kind == AbsenceKind.Vacation ? CheckBalance(input, others) : new(null);
    }

    private static string? CheckTimesheets(AbsenceValidationInput input)
    {
        var (settings, timeZone) = (input.Settings, input.TimeZone);
        var days = input.Days.ToHashSet();
        foreach (var entry in input.Timesheets.Where(entry => entry.Begin is not null).OrderBy(entry => entry.Begin))
        {
            var day = AbsencePlan.LocalDay(entry.Begin!.Value, timeZone);
            if (!days.Contains(day))
            {
                continue;
            }

            if (AbsenceKinds.KindOf(entry, settings) is { } existing)
            {
                if (!input.Applying || existing != input.Kind)
                {
                    return $"Für den {AbsenceKinds.Format(day)} ist in Kimai schon eine Abwesenheit ({AbsenceKinds.Label(existing)}) eingetragen.";
                }
                continue;
            }

            // Pausen und sonstige Einträge auf dem Abwesenheitsprojekt sind keine Arbeit.
            var isPause = settings.PauseActivityId is { } pause && entry.ActivityId == pause;
            if (isPause || entry.ProjectId == settings.AbsenceProjectId)
            {
                continue;
            }

            // Zählt der Tag, an dem die Arbeit beginnt: das Ende einer
            // Nachtschicht vom Vortag blockiert den Tag nicht.
            return input.Kind == AbsenceKind.Sick
                ? $"Am {AbsenceKinds.Format(day)} wurde schon gearbeitet. Am Tag mit Arbeit bitte ab dem Folgetag krankmelden."
                : $"Am {AbsenceKinds.Format(day)} wurde schon gearbeitet.";
        }

        return null;
    }

    private static AbsenceValidationResult CheckBalance(AbsenceValidationInput input, IReadOnlyCollection<AbsenceRequest> others)
    {
        var warnings = new List<string>();
        foreach (var year in input.Days.GroupBy(day => day.Year).OrderBy(group => group.Key))
        {
            var balance = VacationEntitlementCalculator.Calculate(input.Employee, year.Key, others);
            var requested = year.Count();
            if (requested <= balance.Remaining)
            {
                continue;
            }

            if (!input.IsAdmin)
            {
                return new($"Urlaub {year.Key}: {requested} Tage gewählt, der Rest beträgt nur {AbsenceKinds.FormatDays(balance.Remaining)} Tage.");
            }

            warnings.Add($"Urlaub übersteigt den Rest {year.Key} um {AbsenceKinds.FormatDays(requested - balance.Remaining)} Tage.");
        }

        return new(null, warnings.Count == 0 ? null : string.Join(" ", warnings));
    }
}
