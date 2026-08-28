using System.Globalization;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

public sealed class HoursOverviewCalculatorTests
{
    private static readonly DateTimeOffset Now = Parse("2026-08-28T13:30:00+02:00"); // Freitag

    [Fact]
    public void Calculate_ExcludesPauseActivity_AndCountsRunningEntryLive()
    {
        var entries = new[]
        {
            Entry(1, "2026-08-28T08:00:00+02:00", "2026-08-28T12:00:00+02:00", 14400, ActivityId: 5),
            Entry(2, "2026-08-28T13:00:00+02:00", null, 0, ActivityId: 5),
            Entry(3, "2026-08-28T12:00:00+02:00", "2026-08-28T12:45:00+02:00", 2700, ActivityId: 99),
            Entry(4, "2026-08-26T08:00:00+02:00", "2026-08-26T16:00:00+02:00", 28800, ActivityId: 5),
            Entry(5, "2026-08-17T08:00:00+02:00", "2026-08-17T12:00:00+02:00", 14400, ActivityId: 5),
            Entry(6, "2026-07-31T08:00:00+02:00", "2026-07-31T12:00:00+02:00", 14400, ActivityId: 5),
        };

        var result = HoursOverviewCalculator.Calculate(entries, PauseActivityId: 99, Now);

        Assert.Equal(14400 + 1800, result.TodaySeconds);        // Netto heute: 4h + live 30min
        Assert.Equal(2700, result.TodayPauseSeconds);            // Pause heute: 45min
        Assert.Equal(14400 + 1800 + 28800, result.WeekSeconds);  // nur Netto
        Assert.Equal(14400 + 1800 + 28800 + 14400, result.MonthSeconds);
    }

    [Fact]
    public void Calculate_RunningEntryWithoutEnd_SumsFromBeginToNow()
    {
        var entries = new[] { Entry(1, "2026-08-28T08:00:00+02:00", null, 0, ActivityId: 5) };

        var result = HoursOverviewCalculator.Calculate(entries, PauseActivityId: null, Now);

        Assert.Equal(5 * 3600 + 30 * 60, result.TodaySeconds);
    }

    private static KimaiTimesheetEntryDto Entry(int id, string begin, string? end, int? duration, int? ActivityId) =>
        new(id, Parse(begin), end is null ? null : Parse(end), duration, ActivityId);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}
