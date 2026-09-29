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
/// Eine überschrittene Grenze. <paramref name="Key"/> hängt nur am Beginn der
/// Gruppe und bleibt über alle Prüfungen gleich, solange Kimai nichts davor
/// nachträgt - daran erkennt der Merker eine schon gesendete Warnung.
/// </summary>
public sealed record WorkTimeViolation(
    WorkTimeViolationKind Kind,
    string Key,
    DateTimeOffset Start,
    int WorkedSeconds);

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

    /// <param name="windowStart">
    /// Beginn des Kimai-Abfragefensters. Davor liegende Timesheets fehlen in
    /// <paramref name="entries"/>; eine Gruppe, die zu nah daran beginnt,
    /// könnte abgeschnitten sein und bekäme einen anderen Key - sie wird
    /// deshalb nicht bewertet.
    /// </param>
    public static IReadOnlyList<WorkTimeViolation> Evaluate(
        IReadOnlyCollection<KimaiTimesheetEntryDto> entries,
        int? pauseActivityId,
        DateTimeOffset windowStart,
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
        Collect(intervals, MinimumBreak, ContinuousLimit, WorkTimeViolationKind.Continuous, "continuous", windowStart, now, violations);
        Collect(intervals, ShiftRest, ShiftLimit, WorkTimeViolationKind.Shift, "shift", windowStart, now, violations);
        return violations;
    }

    private static void Collect(
        (DateTimeOffset Begin, DateTimeOffset End, bool Running)[] intervals,
        TimeSpan separatingGap,
        TimeSpan limit,
        WorkTimeViolationKind kind,
        string keyPrefix,
        DateTimeOffset windowStart,
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

            for (; index < intervals.Length && intervals[index].Begin - end < separatingGap; index++)
            {
                var interval = intervals[index];
                // Überlappende Timesheets (Datenfehler) nicht doppelt zählen.
                var countedFrom = interval.Begin > end ? interval.Begin : end;
                if (interval.End > countedFrom)
                {
                    worked += interval.End - countedFrom;
                    end = interval.End;
                }
                running |= interval.Running;
            }

            if (worked > limit
                && start - windowStart >= separatingGap
                && (running || now - end <= ReportWindow))
            {
                violations.Add(new WorkTimeViolation(
                    kind, $"{keyPrefix}:{start.UtcDateTime:O}", start, (int)worked.TotalSeconds));
            }
        }
    }
}
