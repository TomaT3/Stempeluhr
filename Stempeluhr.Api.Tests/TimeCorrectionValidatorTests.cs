using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;
using static Stempeluhr.Api.Tests.TimeCorrection;

namespace Stempeluhr.Api.Tests;

public sealed class TimeCorrectionValidatorTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private static readonly RuntimeSettings Configured = Settings();

    private static FakeKimai.Sheet Work(int id, DateTimeOffset begin, DateTimeOffset? end, int activity = WorkActivity)
        => new() { Id = id, Begin = begin, End = end, Activity = activity };

    private static KimaiTimesheetEntryDto Entry(int id, DateTimeOffset begin, DateTimeOffset? end, int activity = WorkActivity)
        => new(id, begin, end, null, activity, WorkProject);

    private static string? Validate(
        TimeCorrectionRequest request,
        IReadOnlyCollection<KimaiTimesheetEntryDto>? timesheets = null,
        IReadOnlyCollection<TimeCorrectionRequest>? open = null,
        RuntimeSettings? settings = null,
        DateTimeOffset? now = null)
        => TimeCorrectionValidator.Validate(request, timesheets ?? [], open ?? [], now ?? Now, Berlin, settings ?? Configured);

    private static TimeCorrectionRequest AddPause(DateTimeOffset pauseBegin, DateTimeOffset pauseEnd, FakeKimai.Sheet? sheet = null)
        => Request(TimeCorrectionKind.AddPause, sheet ?? Work(10, At(6), At(11)),
            r => r with { PauseBegin = pauseBegin, PauseEnd = pauseEnd });

    private static TimeCorrectionRequest AddShift(DateTimeOffset begin, DateTimeOffset end, DateTimeOffset? pauseBegin = null, DateTimeOffset? pauseEnd = null)
        => Request(TimeCorrectionKind.AddShift, null, r => r with { Begin = begin, End = end, PauseBegin = pauseBegin, PauseEnd = pauseEnd });

    // ---- addPause

    [Fact]
    public void AddPause_InsideAStoppedWorkSheet_IsValid()
        => Assert.Null(Validate(AddPause(At(8), At(8, 30))));

    [Fact]
    public void AddPause_EndingAtTheOldEnd_IsValid()
        => Assert.Null(Validate(AddPause(At(10, 30), At(11))));

    [Theory]
    [InlineData(5, 30, 6, 30)]   // starts before the sheet
    [InlineData(10, 30, 11, 30)] // ends after the sheet
    [InlineData(6, 0, 6, 30)]    // would cut the work before the pause to length 0
    public void AddPause_OutsideTheSheet_IsRejected(int fromHour, int fromMinute, int toHour, int toMinute)
        => Assert.Contains("innerhalb", Validate(AddPause(At(fromHour, fromMinute), At(toHour, toMinute))));

    [Fact]
    public void AddPause_EndNotAfterBegin_IsRejected()
        => Assert.Contains("nach ihrem Beginn", Validate(AddPause(At(8, 30), At(8, 30))));

    [Fact]
    public void AddPause_LongerThanFourHours_IsRejected()
        => Assert.Contains("höchstens 4 Stunden", Validate(AddPause(At(6, 30), At(10, 31))));

    [Fact]
    public void AddPause_OnARunningSheet_IsRejected()
        => Assert.Contains("läuft noch", Validate(AddPause(At(8), At(8, 30), Work(10, At(6), null))));

    [Fact]
    public void AddPause_OnAPauseSheet_IsRejected()
        => Assert.Contains("Arbeitseintrag", Validate(AddPause(At(8), At(8, 30), Work(10, At(6), At(11), PauseActivity))));

    [Fact]
    public void AddPause_WithoutConfiguredPauseActivity_IsRejected()
        => Assert.Contains("Pausen-Aktivität", Validate(AddPause(At(8), At(8, 30)), settings: new RuntimeSettings { PauseActivityId = null }));

    [Fact]
    public void AddPause_WithoutPauseTimes_IsRejected()
        => Assert.Contains("Pause fehlen", Validate(Request(TimeCorrectionKind.AddPause, Work(10, At(6), At(11)))));

    // ---- setEnd

    [Fact]
    public void SetEnd_BeforeTheCurrentEnd_IsValid()
        => Assert.Null(Validate(Request(TimeCorrectionKind.SetEnd, Work(10, At(6), At(11)), r => r with { End = At(10, 30) })));

    [Theory]
    [InlineData(11, 0)]
    [InlineData(11, 30)]
    public void SetEnd_NotBeforeTheCurrentEnd_IsRejected(int hour, int minute)
        => Assert.Contains("vor dem bisherigen Ende",
            Validate(Request(TimeCorrectionKind.SetEnd, Work(10, At(6), At(11)), r => r with { End = At(hour, minute) })));

    [Fact]
    public void SetEnd_NotAfterTheBegin_IsRejected()
        => Assert.Contains("nach dem Beginn",
            Validate(Request(TimeCorrectionKind.SetEnd, Work(10, At(6), At(11)), r => r with { End = At(6) })));

    // ---- addShift

    [Fact]
    public void AddShift_WithPause_IsValid()
        => Assert.Null(Validate(AddShift(At(6), At(10), At(8), At(8, 30))));

    [Fact]
    public void AddShift_AcrossMidnight_IsOneShift()
        => Assert.Null(Validate(AddShift(Day(4, 22), Day(5, 6))));

    [Fact]
    public void AddShift_PauseMustLieInsideTheShift()
        => Assert.Contains("innerhalb der Schicht", Validate(AddShift(At(6), At(10), At(5), At(5, 30))));

    [Fact]
    public void AddShift_PauseTimesBelongTogether()
        => Assert.Contains("gehören zusammen", Validate(AddShift(At(6), At(10), At(8), null)));

    [Fact]
    public void AddShift_ExactlySixteenHours_IsValidButMoreIsNot()
    {
        Assert.Null(Validate(AddShift(Day(4, 18), Day(5, 10))));
        Assert.Contains("16 Stunden", Validate(AddShift(Day(4, 17, 59), Day(5, 10))));
    }

    [Fact]
    public void AddShift_EndNotAfterBegin_IsRejected()
        => Assert.Contains("nach dem Beginn", Validate(AddShift(At(8), At(8))));

    // ---- changeTimes

    [Fact]
    public void ChangeTimes_ExtendingIntoFreeTime_IsValid()
        => Assert.Null(Validate(Request(TimeCorrectionKind.ChangeTimes, Work(10, At(6), At(11)), r => r with { Begin = At(5, 30) })));

    [Fact]
    public void ChangeTimes_OverlappingTheNeighbour_IsRejected()
    {
        var neighbour = Entry(11, At(4), At(5, 45));
        var error = Validate(
            Request(TimeCorrectionKind.ChangeTimes, Work(10, At(6), At(11)), r => r with { Begin = At(5, 30) }),
            [neighbour]);
        Assert.Contains("überschneidet", error);
    }

    [Fact]
    public void ChangeTimes_WithoutAnyChange_IsRejected()
        => Assert.Contains("keine Änderung", Validate(Request(TimeCorrectionKind.ChangeTimes, Work(10, At(6), At(11)))));

    [Fact]
    public void ChangeTimes_IgnoresTheChangedSheetItself()
    {
        var own = Entry(10, At(6), At(11));
        Assert.Null(Validate(
            Request(TimeCorrectionKind.ChangeTimes, Work(10, At(6), At(11)), r => r with { End = At(11, 30) }), [own]));
    }

    // ---- common rules

    [Fact]
    public void FutureTimes_AreRejected_WithTwoMinutesTolerance()
    {
        Assert.Null(Validate(AddShift(At(10), At(12, 2))));
        Assert.Contains("Zukunft", Validate(AddShift(At(10), At(12, 3))));
    }

    [Fact]
    public void RequestsOlderThan31Days_AreRejected()
    {
        Assert.Contains("31 Tage", Validate(AddShift(Now.AddDays(-32), Now.AddDays(-32).AddHours(8))));
        Assert.Contains("31 Tage",
            Validate(Request(TimeCorrectionKind.SetEnd, Work(10, Now.AddDays(-32), Now.AddDays(-32).AddHours(9)),
                r => r with { End = Now.AddDays(-32).AddHours(8) })));
        Assert.Null(Validate(AddShift(Now.AddDays(-31).AddHours(1), Now.AddDays(-31).AddHours(9))));
    }

    [Fact]
    public void OverlappingAnotherSheet_IsRejected_ButAdjacentSheetsAreAllowed()
    {
        var existing = Entry(11, At(6), At(10));
        Assert.Contains("überschneidet", Validate(AddShift(At(9), At(11)), [existing]));
        Assert.Null(Validate(AddShift(At(10), At(11)), [existing]));
        Assert.Null(Validate(AddShift(At(4), At(6)), [existing]));
    }

    [Fact]
    public void OverlappingARunningSheet_IsRejected()
    {
        var running = Entry(11, At(9), null);
        Assert.Contains("läuft noch", Validate(AddShift(At(8), At(10)), [running]));
    }

    [Fact]
    public void OverlappingASheetFromTheDayBefore_IsRejected()
    {
        var night = Entry(11, Day(4, 22), Day(5, 6));
        Assert.Contains("überschneidet", Validate(AddShift(At(5), At(8)), [night]));
    }

    [Fact]
    public void OnlyOneOpenRequestPerTimesheet_ButTheRequestItselfIsIgnored()
    {
        var sheet = Work(10, At(6), At(11));
        var first = Request(TimeCorrectionKind.SetEnd, sheet, r => r with { End = At(10) });
        var second = Request(TimeCorrectionKind.SetEnd, sheet, r => r with { End = At(9) });

        Assert.Contains("offenen Antrag", Validate(second, open: [first]));
        Assert.Null(Validate(first, open: [first]));

        var closed = first with { Status = TimeCorrectionStatus.Applied };
        Assert.Null(Validate(second, open: [closed]));
        // A failed request still needs attention - it blocks the sheet like a pending one.
        Assert.Contains("offenen Antrag", Validate(second, open: [first with { Status = TimeCorrectionStatus.Failed }]));
    }

    [Fact]
    public void AddShift_OverlappingAnotherOpenAddShiftOfTheSameEmployee_IsRejected()
    {
        var first = AddShift(At(6), At(10));
        Assert.Contains("offenen Antrag", Validate(AddShift(At(9), At(11)), open: [first]));
        Assert.Null(Validate(AddShift(At(10), At(11)), open: [first]));
        Assert.Null(Validate(AddShift(At(9), At(11)), open: [first with { EmployeeId = "anna" }]));
    }

    [Fact]
    public void EntriesTheRequestCreatesItself_DoNotCountAsOverlap()
    {
        // Continuation after a partial failure: the pause (and the shortened
        // sheet's rest) already exist in Kimai.
        var request = AddPause(At(8), At(8, 30));
        var createdPause = Entry(20, At(8), At(8, 30), PauseActivity);
        var createdRest = Entry(21, At(8, 30), At(11));
        Assert.Null(Validate(request, [createdPause, createdRest]));

        // A different entry in that time still is an overlap.
        Assert.Contains("überschneidet", Validate(request, [Entry(22, At(8, 10), At(8, 20))]));
    }

    [Fact]
    public void LongComment_IsRejected()
    {
        var request = Request(TimeCorrectionKind.SetEnd, Work(10, At(6), At(11)),
            r => r with { End = At(10), Comment = new string('x', TimeCorrectionValidator.MaxCommentLength + 1) });
        Assert.Contains("300 Zeichen", Validate(request));
        Assert.Null(Validate(request with { Comment = new string('x', TimeCorrectionValidator.MaxCommentLength) }));
    }

    // ---- shifts

    [Fact]
    public void ShiftGrouper_KeepsANightShiftAcrossMidnightTogether()
    {
        var entries = new[]
        {
            Entry(1, Day(3, 22), Day(4, 2)),
            Entry(2, Day(4, 2, 30), Day(4, 3), PauseActivity),
            Entry(3, Day(4, 3), Day(4, 6)),
        };

        var shifts = ShiftGrouper.Group(entries, e => e.Begin!.Value, e => e.End, Now);

        Assert.Equal([1, 2, 3], Assert.Single(shifts).Select(e => e.Id));
    }

    [Fact]
    public void ShiftGrouper_SplitsAfterEightHoursWithoutWork_LikeTheWorkTimeWarning()
    {
        var entries = new[]
        {
            Entry(1, Day(4, 6), Day(4, 10)),
            Entry(2, Day(4, 17, 59), Day(4, 20)), // 7:59 h later: same shift
            Entry(3, Day(5, 4), Day(5, 8)),       // exactly 8 h after 20:00: next shift
        };

        var shifts = ShiftGrouper.Group(entries.Reverse(), e => e.Begin!.Value, e => e.End, Now);

        Assert.Equal(2, shifts.Count);
        Assert.Equal([1, 2], shifts[0].Select(e => e.Id));
        Assert.Equal([3], shifts[1].Select(e => e.Id));
    }

    [Fact]
    public void ShiftGrouper_LetsARunningEntryReachUntilNow()
    {
        // 14 h of running work: without "until now" the entry would end at its begin and 13 h pass before the next one.
        var entries = new[] { Entry(1, Day(4, 22), null), Entry(2, At(11), At(11, 30)) };

        Assert.Single(ShiftGrouper.Group(entries, e => e.Begin!.Value, e => e.End, Now));
    }
}
