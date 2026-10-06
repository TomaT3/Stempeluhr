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
    public void ShiftRunningForMoreThanFortyHours_IsStillReported()
    {
        // Forgotten clock-out: the whole shift lies inside the 48 h lookback.
        var entries = new[] { Work(Now.AddHours(-42), null) };

        var shift = Assert.Single(Evaluate(entries), violation => violation.Kind == WorkTimeViolationKind.Shift);

        Assert.Equal(Now.AddHours(-42), shift.Start);
        Assert.Equal(Now, shift.End);
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
    public void EarlierEntryAddedLater_StillCountsAsTheReportedGroup()
    {
        var reported = Assert.Single(Evaluate([Work(Now.AddHours(-7), null)]));

        var later = Now.AddMinutes(5);
        var extended = Assert.Single(Evaluate(
            [Work(Now.AddHours(-8), Now.AddHours(-7).AddMinutes(-5)), Work(Now.AddHours(-7), null)], later));

        Assert.Equal(Now.AddHours(-8), extended.Start);
        Assert.True(extended.WasCoveredBy(reported.Start, reported.End));
    }

    [Fact]
    public void NextBlockAfterARealBreak_IsNotTheReportedOne()
    {
        var blockEnd = Now.AddHours(-1);
        var reported = Assert.Single(Evaluate([Work(blockEnd.AddHours(-7), null)], blockEnd));

        var next = Assert.Single(Evaluate(
            [Work(blockEnd.AddHours(-7), blockEnd), Work(blockEnd.AddMinutes(15), null)], blockEnd.AddHours(7)),
            violation => violation.Start > reported.Start);

        Assert.False(next.WasCoveredBy(reported.Start, reported.End));
    }

    [Fact]
    public void PauseInsertedIntoAReportedBlock_LeavesTheNewBlockUnreported()
    {
        // Warned at 15:00 for 08:00-15:00; then corrected to 08:00-12:00 and
        // 12:30-19:00. The second block reaches 6 h only at 18:30.
        var reported = Assert.Single(Evaluate(
            [Work("2026-09-30T08:00:00+02:00", null)], Parse("2026-09-30T15:00:00+02:00")));

        var split = Assert.Single(Evaluate(
            [
                Work("2026-09-30T08:00:00+02:00", "2026-09-30T12:00:00+02:00"),
                Pause("2026-09-30T12:00:00+02:00", "2026-09-30T12:30:00+02:00"),
                Work("2026-09-30T12:30:00+02:00", "2026-09-30T19:00:00+02:00"),
            ],
            Parse("2026-09-30T19:05:00+02:00")),
            violation => violation.Kind == WorkTimeViolationKind.Continuous);

        Assert.Equal(Parse("2026-09-30T12:30:00+02:00"), split.Start);
        Assert.False(split.WasCoveredBy(reported.Start, reported.End));
    }

    [Fact]
    public void PauseInsertedAfterADelayedWarning_LeavesTheNewBlockUnreported()
    {
        // API or Telegram down: the first warning went out only at 20:00,
        // when the split-off block had already passed 6 h (18:30).
        var reported = Assert.Single(Evaluate(
            [Work("2026-09-30T08:00:00+02:00", null)], Parse("2026-09-30T20:00:00+02:00")),
            violation => violation.Kind == WorkTimeViolationKind.Continuous);

        var split = Assert.Single(Evaluate(
            [
                Work("2026-09-30T08:00:00+02:00", "2026-09-30T12:00:00+02:00"),
                Pause("2026-09-30T12:00:00+02:00", "2026-09-30T12:30:00+02:00"),
                Work("2026-09-30T12:30:00+02:00", null),
            ],
            Parse("2026-09-30T20:05:00+02:00")),
            violation => violation.Kind == WorkTimeViolationKind.Continuous);

        Assert.False(split.WasCoveredBy(reported.Start, reported.End));
    }

    [Fact]
    public void GroupCutByTheQueryWindow_StaysCoveredByItsWarning()
    {
        // Worked with less than 8 h rest for days: the shift group reaches
        // back beyond the lookback, so its visible start moves later.
        var reported = new WorkTimeViolation(WorkTimeViolationKind.Shift, Now.AddHours(-60), Now.AddHours(-40), 36000);
        var windowStart = Now - WorkTimeLimitCalculator.Lookback;

        var cut = Assert.Single(WorkTimeLimitCalculator.Evaluate(
            [
                Work(windowStart.AddHours(2), windowStart.AddHours(10)),
                Work(windowStart.AddHours(16), Now.AddHours(-16)),
                Work(Now.AddHours(-10), null),
            ],
            PauseActivity, windowStart, Now),
            violation => violation.Kind == WorkTimeViolationKind.Shift);

        Assert.True(cut.StartMayBeCut);
        Assert.True(cut.WasCoveredBy(reported.Start, reported.End));
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

    [Fact]
    public void LatestShiftStart_BeginsAfterEightHoursWithoutWork_PausesAreNoWork()
    {
        var entries = new[]
        {
            Work("2026-09-29T06:00:00+02:00", "2026-09-29T13:00:00+02:00"),
            // 13:00-21:00 without work (a pause entry is no work): exactly 8 h, a new shift.
            Pause("2026-09-29T13:00:00+02:00", "2026-09-29T14:00:00+02:00"),
            Work("2026-09-29T21:00:00+02:00", "2026-09-30T02:00:00+02:00"),
            // 7 h 59 min later: still the same shift.
            Work("2026-09-30T09:59:00+02:00", null),
        };

        Assert.Equal(Parse("2026-09-29T21:00:00+02:00"),
            WorkTimeLimitCalculator.LatestShiftStart(entries, PauseActivity, Now));
    }

    [Fact]
    public void LatestShiftStart_IsNullWithoutWork()
    {
        Assert.Null(WorkTimeLimitCalculator.LatestShiftStart(
            [Pause("2026-09-30T08:00:00+02:00", "2026-09-30T09:00:00+02:00")], PauseActivity, Now));
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
