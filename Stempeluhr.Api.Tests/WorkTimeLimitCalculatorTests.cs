using System.Globalization;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

public sealed class WorkTimeLimitCalculatorTests
{
    private const int WorkActivity = 5;
    private const int PauseActivity = 99;
    private static readonly DateTimeOffset Now = Parse("2026-09-30T16:00:00+02:00");

    [Theory]
    [InlineData(361, true)]
    [InlineData(359, false)]
    public void RunningEntry_WarnsOnlyAfterSixHours(int minutes, bool warns)
    {
        var entries = new[] { Work(Now.AddMinutes(-minutes), null) };

        var violations = Evaluate(entries);

        Assert.Equal(warns, violations.Any(violation => violation.Kind == WorkTimeViolationKind.Continuous));
    }

    [Fact]
    public void ShortPause_DoesNotBreakTheBlock()
    {
        var entries = new[]
        {
            Work("2026-09-30T08:00:00+02:00", "2026-09-30T12:00:00+02:00"),
            Pause("2026-09-30T12:00:00+02:00", "2026-09-30T12:10:00+02:00"),
            Work("2026-09-30T12:10:00+02:00", "2026-09-30T14:15:00+02:00"),
        };

        var violation = Assert.Single(Evaluate(entries));

        Assert.Equal(WorkTimeViolationKind.Continuous, violation.Kind);
        Assert.Equal(Parse("2026-09-30T08:00:00+02:00"), violation.Start);
        Assert.Equal((6 * 60 + 5) * 60, violation.WorkedSeconds);
    }

    [Fact]
    public void PauseOfTwentyMinutes_StartsANewBlock()
    {
        var entries = new[]
        {
            Work("2026-09-30T08:00:00+02:00", "2026-09-30T12:00:00+02:00"),
            Pause("2026-09-30T12:00:00+02:00", "2026-09-30T12:20:00+02:00"),
            Work("2026-09-30T12:20:00+02:00", "2026-09-30T15:20:00+02:00"),
        };

        Assert.Empty(Evaluate(entries));
    }

    [Theory]
    [InlineData(14, true)]
    [InlineData(15, false)]
    public void ClockedOutGap_BreaksOnlyFromFifteenMinutes(int gapMinutes, bool warns)
    {
        var firstEnd = Parse("2026-09-30T12:00:00+02:00");
        var entries = new[]
        {
            Work(firstEnd.AddHours(-4), firstEnd),
            Work(firstEnd.AddMinutes(gapMinutes), firstEnd.AddMinutes(gapMinutes + 125)),
        };

        Assert.Equal(warns, Evaluate(entries).Any());
    }

    [Fact]
    public void TaskSwitches_FormOneBlock()
    {
        var entries = new[]
        {
            Work("2026-09-30T08:00:00+02:00", "2026-09-30T10:00:00+02:00"),
            Work("2026-09-30T10:00:00+02:00", "2026-09-30T12:00:00+02:00", activityId: 7),
            Work("2026-09-30T12:00:00+02:00", "2026-09-30T14:01:00+02:00"),
        };

        var violation = Assert.Single(Evaluate(entries));

        Assert.Equal(WorkTimeViolationKind.Continuous, violation.Kind);
        Assert.Equal((6 * 60 + 1) * 60, violation.WorkedSeconds);
    }

    [Fact]
    public void NightShift_CountsAcrossMidnight()
    {
        var now = Parse("2026-09-30T08:31:00+02:00");
        var entries = new[]
        {
            Work("2026-09-29T21:00:00+02:00", "2026-09-30T01:00:00+02:00"),
            Pause("2026-09-30T01:00:00+02:00", "2026-09-30T01:30:00+02:00"),
            Work("2026-09-30T01:30:00+02:00", "2026-09-30T05:00:00+02:00"),
            Pause("2026-09-30T05:00:00+02:00", "2026-09-30T05:30:00+02:00"),
            Work("2026-09-30T05:30:00+02:00", null),
        };

        var violation = Assert.Single(Evaluate(entries, now));

        Assert.Equal(WorkTimeViolationKind.Shift, violation.Kind);
        Assert.Equal(Parse("2026-09-29T21:00:00+02:00"), violation.Start);
        Assert.Equal((10 * 60 + 31) * 60, violation.WorkedSeconds);
    }

    [Fact]
    public void SplitShiftOfNineHours_DoesNotWarn()
    {
        var entries = new[]
        {
            Work("2026-09-30T10:00:00+02:00", "2026-09-30T14:00:00+02:00"),
            Work("2026-09-30T18:00:00+02:00", "2026-09-30T23:00:00+02:00"),
        };

        Assert.Empty(Evaluate(entries, Parse("2026-09-30T23:30:00+02:00")));
    }

    [Fact]
    public void SplitShiftOverTenHours_Warns()
    {
        var entries = new[]
        {
            Work("2026-09-30T10:00:00+02:00", "2026-09-30T14:00:00+02:00"),
            Work("2026-09-30T17:00:00+02:00", "2026-09-30T23:30:00+02:00"),
        };

        var shift = Assert.Single(
            Evaluate(entries, Parse("2026-09-30T23:45:00+02:00")),
            violation => violation.Kind == WorkTimeViolationKind.Shift);

        Assert.Equal(Parse("2026-09-30T10:00:00+02:00"), shift.Start);
        Assert.Equal((10 * 60 + 30) * 60, shift.WorkedSeconds);
    }

    [Theory]
    [InlineData(480, false)]
    [InlineData(479, true)]
    public void RestOfEightHours_SeparatesShifts(int restMinutes, bool warns)
    {
        var firstEnd = Parse("2026-09-30T01:00:00+02:00");
        var secondStart = firstEnd.AddMinutes(restMinutes);
        var entries = new[]
        {
            Work(firstEnd.AddHours(-5), firstEnd),
            Work(secondStart, secondStart.AddHours(6)),
        };

        var violations = Evaluate(entries, secondStart.AddHours(6));

        Assert.Equal(warns, violations.Any(violation => violation.Kind == WorkTimeViolationKind.Shift));
    }

    [Fact]
    public void PauseActivity_IsNeverWork()
    {
        var entries = new[] { Pause(Now.AddHours(-11), null) };

        Assert.Empty(Evaluate(entries));
    }

    [Fact]
    public void GroupNearTheWindowStart_IsNotEvaluated()
    {
        // Timesheets before the window are missing; the shift may be cut off.
        var entries = new[] { Work(Now.AddHours(-11), null) };

        var violation = Assert.Single(WorkTimeLimitCalculator.Evaluate(
            entries, PauseActivity, windowStart: Now.AddHours(-12), Now));

        Assert.Equal(WorkTimeViolationKind.Continuous, violation.Kind);
    }

    [Theory]
    [InlineData(25, false)]
    [InlineData(23, true)]
    public void FinishedGroup_IsReportedOnlyWithinADay(int endedHoursAgo, bool warns)
    {
        var end = Now.AddHours(-endedHoursAgo);
        var entries = new[] { Work(end.AddHours(-7), end) };

        Assert.Equal(warns, Evaluate(entries).Any());
    }

    [Fact]
    public void Keys_StayStableWhileTheShiftRuns()
    {
        var entries = new[] { Work(Now.AddHours(-11), null) };

        var first = Evaluate(entries).Select(violation => violation.Key).ToArray();
        var later = Evaluate(entries, Now.AddMinutes(5)).Select(violation => violation.Key).ToArray();

        Assert.Equal(2, first.Length);
        Assert.Equal(first, later);
    }

    [Fact]
    public void OverlappingTimesheets_AreNotCountedTwice()
    {
        var entries = new[]
        {
            Work("2026-09-30T08:00:00+02:00", "2026-09-30T12:00:00+02:00"),
            Work("2026-09-30T10:00:00+02:00", "2026-09-30T13:30:00+02:00"),
        };

        Assert.Empty(Evaluate(entries));
    }

    private static IReadOnlyList<WorkTimeViolation> Evaluate(
        IReadOnlyCollection<KimaiTimesheetEntryDto> entries, DateTimeOffset? now = null)
    {
        var at = now ?? Now;
        return WorkTimeLimitCalculator.Evaluate(entries, PauseActivity, at - WorkTimeLimitCalculator.Lookback, at);
    }

    private static KimaiTimesheetEntryDto Work(string begin, string? end, int activityId = WorkActivity) =>
        Work(Parse(begin), end is null ? null : Parse(end), activityId);

    private static KimaiTimesheetEntryDto Work(DateTimeOffset begin, DateTimeOffset? end, int activityId = WorkActivity) =>
        new(0, begin, end, end is null ? 0 : (int)(end.Value - begin).TotalSeconds, activityId);

    private static KimaiTimesheetEntryDto Pause(string begin, string? end) =>
        Work(begin, end, PauseActivity);

    private static KimaiTimesheetEntryDto Pause(DateTimeOffset begin, DateTimeOffset? end) =>
        Work(begin, end, PauseActivity);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
