using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

public enum WorkTimeViolationKind
{
    /// <summary>Mehr als <see cref="WorkTimeLimitCalculator.ContinuousLimit"/> ohne Pause.</summary>
    Continuous,

    /// <summary>Mehr als <see cref="WorkTimeLimitCalculator.ShiftLimit"/> in einer Schicht.</summary>
    Shift,
}

/// <summary>
/// Eine überschrittene Grenze für die Gruppe von <paramref name="Start"/> bis
/// <paramref name="End"/> (laufend: jetzt). <paramref name="LimitReachedAt"/>
/// ist der Zeitpunkt, an dem die Arbeit der Gruppe die Grenze erreichte.
/// </summary>
public sealed record WorkTimeViolation(
    WorkTimeViolationKind Kind,
    DateTimeOffset Start,
    DateTimeOffset End,
    int WorkedSeconds,
    DateTimeOffset LimitReachedAt)
{
    /// <summary>
    /// Ob eine Warnung für den Zeitraum <paramref name="start"/> bis
    /// <paramref name="end"/> (Stand beim Versand) schon diese Gruppe meinte:
    /// Sie überschneidet sich damit und hatte die Grenze bis dahin schon
    /// erreicht. Ein später davor nachgetragener oder vorgezogener Beginn
    /// bleibt so dieselbe Gruppe. Ein Block, der erst durch eine nachträglich
    /// eingefügte Pause entsteht, erreicht die Grenze dagegen erst nach dem
    /// Versand und bekommt eine eigene Warnung.
    /// </summary>
    public bool WasCoveredBy(DateTimeOffset start, DateTimeOffset end) => start <= End && LimitReachedAt <= end;
}

/// <summary>
/// Prüft Kimai-Timesheets gegen die Arbeitszeit-Grenzen. Bewusst eine pure
/// static Klasse (wie <see cref="HoursOverviewCalculator"/>): ohne I/O,
/// vollständig unit-testbar.
///
/// Gezählt wird nur Arbeit (alles außer der Pause-Aktivität), netto.
/// Aufeinanderfolgende Arbeitszeiten gehören zusammen, solange die Lücke
/// dazwischen (Pause oder ausgestempelt) kürzer als die jeweilige Schwelle
/// ist: 15 Minuten für „am Stück“ (ArbZG), 8 Stunden für eine Schicht. Eine
/// Schicht endet also nicht an Mitternacht - Nachtschichten im Hotel zählen
/// als Ganzes, geteilte Dienste ebenso.
/// </summary>
public static class WorkTimeLimitCalculator
{
    public static readonly TimeSpan ContinuousLimit = TimeSpan.FromHours(6);
    public static readonly TimeSpan MinimumBreak = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ShiftLimit = TimeSpan.FromHours(10);
    public static readonly TimeSpan ShiftRest = TimeSpan.FromHours(8);

    /// <summary>So weit zurück lädt der Dienst die Timesheets.</summary>
    public static readonly TimeSpan Lookback = TimeSpan.FromHours(48);

    /// <summary>
    /// Nur Gruppen, die noch laufen oder in diesem Zeitraum endeten, werden
    /// gemeldet. Nachträge nach einem Ausfall erscheinen so noch, alte Fälle
    /// (etwa beim ersten Start) nicht.
    /// </summary>
    public static readonly TimeSpan ReportWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// Timesheets vor dem Abfragefenster fehlen in <paramref name="entries"/>.
    /// Eine dort abgeschnittene Gruppe zählt nur weniger Arbeit - das kann
    /// keine falsche Warnung auslösen, und eine schon gemeldete Gruppe
    /// überlappt weiterhin mit ihrem Merker.
    /// </summary>
    public static IReadOnlyList<WorkTimeViolation> Evaluate(
        IReadOnlyCollection<KimaiTimesheetEntryDto> entries,
        int? pauseActivityId,
        DateTimeOffset now)
    {
        var intervals = entries
            .Where(entry => entry.Begin is not null
                && !(pauseActivityId is not null && entry.ActivityId == pauseActivityId))
            // Laufendes Timesheet: zählt bis jetzt.
            .Select(entry => (Begin: entry.Begin!.Value, End: entry.End ?? now, Running: entry.End is null))
            .Where(interval => interval.End > interval.Begin)
            .OrderBy(interval => interval.Begin)
            .ToArray();

        var violations = new List<WorkTimeViolation>();
        Collect(intervals, MinimumBreak, ContinuousLimit, WorkTimeViolationKind.Continuous, now, violations);
        Collect(intervals, ShiftRest, ShiftLimit, WorkTimeViolationKind.Shift, now, violations);
        return violations;
    }

    private static void Collect(
        (DateTimeOffset Begin, DateTimeOffset End, bool Running)[] intervals,
        TimeSpan separatingGap,
        TimeSpan limit,
        WorkTimeViolationKind kind,
        DateTimeOffset now,
        List<WorkTimeViolation> violations)
    {
        var index = 0;
        while (index < intervals.Length)
        {
            var start = intervals[index].Begin;
            var end = start;
            var running = false;
            var worked = TimeSpan.Zero;
            DateTimeOffset? limitReachedAt = null;

            for (; index < intervals.Length && intervals[index].Begin - end < separatingGap; index++)
            {
                var interval = intervals[index];
                // Überlappende Timesheets (Datenfehler) nicht doppelt zählen.
                var countedFrom = interval.Begin > end ? interval.Begin : end;
                if (interval.End > countedFrom)
                {
                    if (limitReachedAt is null && worked + (interval.End - countedFrom) >= limit)
                    {
                        limitReachedAt = countedFrom + (limit - worked);
                    }
                    worked += interval.End - countedFrom;
                    end = interval.End;
                }
                running |= interval.Running;
            }

            if (worked > limit && (running || now - end <= ReportWindow))
            {
                violations.Add(new WorkTimeViolation(kind, start, end, (int)worked.TotalSeconds, limitReachedAt!.Value));
            }
        }
    }
}
