using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

public static class HoursOverviewCalculator
{
    public static HoursOverviewDto Calculate(
        IReadOnlyCollection<KimaiTimesheetEntryDto> entries,
        int? PauseActivityId,
        DateTimeOffset now)
    {
        var localNow = now.ToLocalTime().DateTime;
        var today = localNow.Date;
        var weekStart = StartOfWeek(localNow);
        var monthStart = new DateTime(localNow.Year, localNow.Month, 1);

        var todaySeconds = 0;
        var todayPauseSeconds = 0;
        var weekSeconds = 0;
        var monthSeconds = 0;

        foreach (var entry in entries)
        {
            if (entry.Begin is null)
            {
                continue;
            }

            var day = entry.Begin.Value.ToLocalTime().Date;
            var isPause = PauseActivityId is not null && entry.ActivityId == PauseActivityId;
            var seconds = entry.DurationSeconds ?? 0;

            if (entry.End is null)
            {
                // Laufendes Timesheet: Kimai liefert duration=0 -> Live-Elapsed.
                seconds = (int)Math.Max(0, (now - entry.Begin.Value).TotalSeconds);
            }
            else if (seconds == 0 && entry.End is not null)
            {
                seconds = (int)Math.Max(0, (entry.End.Value - entry.Begin.Value).TotalSeconds);
            }

            if (day >= today)
            {
                if (isPause) { todayPauseSeconds += seconds; }
                else { todaySeconds += seconds; }
            }

            if (!isPause && day >= weekStart && day <= localNow.Date) { weekSeconds += seconds; }
            if (!isPause && day >= monthStart && day <= localNow.Date) { monthSeconds += seconds; }
        }

        return new HoursOverviewDto(todaySeconds, todayPauseSeconds, weekSeconds, monthSeconds);
    }

    public static DateTime StartOfWeek(DateTime localNow)
    {
        var dayOfWeek = (int)localNow.DayOfWeek; // Sonntag = 0
        var daysSinceMonday = dayOfWeek == 0 ? 6 : dayOfWeek - 1;
        return localNow.Date.AddDays(-daysSinceMonday);
    }
}
