using System.Globalization;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

public sealed class HoursOverviewCalculatorTests
{
    private static readonly DateTimeOffset Now = Parse("2026-08-28T13:30:00+02:00"); // Freitag

    // Explizite Kimai-User-Zeitzone statt TimeZoneInfo.Local: Die Tests sind
    // damit unabhaengig von der Zeitzone der Testmaschine (CI laeuft UTC).
    private static TimeZoneInfo BerlinTz => TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

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

        var result = HoursOverviewCalculator.Calculate(entries, pauseActivityId: 99, Now, BerlinTz);

        Assert.Equal(14400 + 1800, result.TodaySeconds);        // Netto heute: 4h + live 30min
        Assert.Equal(2700, result.TodayPauseSeconds);            // Pause heute: 45min
        Assert.Equal(14400 + 1800 + 28800, result.WeekSeconds);  // nur Netto
        Assert.Equal(14400 + 1800 + 28800 + 14400, result.MonthSeconds);
    }

    [Fact]
    public void Calculate_RunningEntryWithoutEnd_SumsFromBeginToNow()
    {
        var entries = new[] { Entry(1, "2026-08-28T08:00:00+02:00", null, 0, ActivityId: 5) };

        var result = HoursOverviewCalculator.Calculate(entries, pauseActivityId: null, Now, BerlinTz);

        Assert.Equal(5 * 3600 + 30 * 60, result.TodaySeconds);
    }

    [Fact]
    public void Calculate_RunningNightShiftStartedYesterday_CountsToToday()
    {
        // Nachtschicht 22:00-02:00: laufendes Timesheet begann gestern, wird
        // aber JETZT gearbeitet - "Heute" darf nicht 0 sein.
        var now = Parse("2026-08-30T02:00:00+02:00"); // Sonntag 02:00
        var entries = new[]
        {
            Entry(1, "2026-08-29T22:00:00+02:00", null, 0, ActivityId: 5),          // laeuft
            Entry(2, "2026-08-29T16:00:00+02:00", "2026-08-29T20:00:00+02:00", 14400, ActivityId: 5), // gestoppt gestern
        };

        var result = HoursOverviewCalculator.Calculate(entries, pauseActivityId: null, now, BerlinTz);

        Assert.Equal(4 * 3600, result.TodaySeconds);        // live 22:00-02:00
        Assert.Equal(4 * 3600 + 14400, result.WeekSeconds); // gestoppt gestern + live
        Assert.Equal(4 * 3600 + 14400, result.MonthSeconds);
    }

    [Fact]
    public void Calculate_UsesKimaiUserTimezone_NotServerLocal()
    {
        // Container-UTC-Szenario: 02:00 Berlin = 00:00 UTC. Ohne explizite
        // User-Zeitzone (ToLocalTime auf UTC-Maschine) wuerde die um 00:30
        // Berlin begonnene Schicht in "gestern" landen -> heute faelschlich 0.
        var now = Parse("2026-08-30T00:00:00+00:00");                     // = 02:00 Berlin
        var entries = new[] { Entry(1, "2026-08-29T22:30:00+00:00", null, 0, ActivityId: 5) }; // = 00:30 Berlin

        var result = HoursOverviewCalculator.Calculate(entries, pauseActivityId: null, now, BerlinTz);

        Assert.Equal(90 * 60, result.TodaySeconds);
    }

    [Fact]
    public void Calculate_EntryWithoutBegin_IsSkipped()
    {
        var entries = new[]
        {
            new KimaiTimesheetEntryDto(1, null, null, 3600, 5),
            Entry(2, "2026-08-28T08:00:00+02:00", "2026-08-28T12:00:00+02:00", 14400, ActivityId: 5),
        };

        var result = HoursOverviewCalculator.Calculate(entries, pauseActivityId: null, Now, BerlinTz);

        Assert.Equal(14400, result.TodaySeconds);
        Assert.Equal(0, result.TodayPauseSeconds);
        Assert.Equal(14400, result.WeekSeconds);
        Assert.Equal(14400, result.MonthSeconds);
    }

    [Fact]
    public void Calculate_MissingDuration_FallsBackToEndMinusBegin()
    {
        var entries = new[] { Entry(1, "2026-08-28T08:00:00+02:00", "2026-08-28T12:00:00+02:00", null, ActivityId: 5) };

        var result = HoursOverviewCalculator.Calculate(entries, pauseActivityId: null, Now, BerlinTz);

        Assert.Equal(4 * 3600, result.TodaySeconds);
    }

    [Fact]
    public void Calculate_SundayWeekStart_CountsMondayToSunday()
    {
        var now = Parse("2026-08-30T12:00:00+02:00"); // Sonntag
        var entries = new[]
        {
            Entry(1, "2026-08-24T08:00:00+02:00", "2026-08-24T16:00:00+02:00", 28800, ActivityId: 5), // Mo
            Entry(2, "2026-08-30T08:00:00+02:00", "2026-08-30T12:00:00+02:00", 14400, ActivityId: 5), // So (heute)
            Entry(3, "2026-08-23T08:00:00+02:00", "2026-08-23T12:00:00+02:00", 14400, ActivityId: 5), // So (Vorwoche)
        };

        var result = HoursOverviewCalculator.Calculate(entries, pauseActivityId: null, now, BerlinTz);

        Assert.Equal(28800 + 14400, result.WeekSeconds);  // 23.08. zählt NICHT
        Assert.Equal(28800 + 14400 + 14400, result.MonthSeconds);
    }

    [Fact]
    public void GetUnionStart_WeekStartsInPreviousMonth_ReturnsWeekStart()
    {
        var now = Parse("2026-08-01T12:00:00+02:00"); // Samstag

        var result = HoursOverviewCalculator.GetUnionStart(now.DateTime);

        Assert.Equal(new DateTime(2026, 7, 27), result);
    }

    [Fact]
    public void GetUnionStart_MonthStartsBeforeWeekStart_ReturnsMonthStart()
    {
        var now = Parse("2026-08-04T12:00:00+02:00"); // Dienstag

        var result = HoursOverviewCalculator.GetUnionStart(now.DateTime);

        Assert.Equal(new DateTime(2026, 8, 1), result);
    }

    private static KimaiTimesheetEntryDto Entry(int id, string begin, string? end, int? duration, int? ActivityId) =>
        new(id, Parse(begin), end is null ? null : Parse(end), duration, ActivityId);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}
