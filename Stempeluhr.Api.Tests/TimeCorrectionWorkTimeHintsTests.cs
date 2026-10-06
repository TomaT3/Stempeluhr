using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;
using static Stempeluhr.Api.Tests.TimeCorrection;

namespace Stempeluhr.Api.Tests;

/// <summary>POST /api/kiosk/work-time-hints: read-only hints after login, same rules as the Telegram warning.</summary>
public sealed class TimeCorrectionWorkTimeHintsTests : IDisposable
{
    private static readonly CorrectionAuthRequest MaxAuth = new("max", "1234", null);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"stempeluhr-work-time-hints-{Guid.NewGuid():N}");
    private readonly FakeKimai _kimai = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly ManualClock _clock = new(Now);
    private readonly PinAttemptGuard _guard;
    private readonly TimeCorrectionStore _store;
    private readonly TimeCorrectionService _service;

    public TimeCorrectionWorkTimeHintsTests()
    {
        _guard = new PinAttemptGuard(clock: _clock);
        _store = new TimeCorrectionStore(Path.Combine(_directory, "time-corrections.json"));
        _service = new TimeCorrectionService(
            new StubSettingsStore(Settings(Max(), Anna())), new EmployeeService(), _kimai, _store, _notifier, _guard, clock: _clock);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private async Task<WorkTimeHintsDto> HintsAsync(CorrectionAuthRequest? auth = null)
    {
        var result = await _service.GetWorkTimeHintsAsync(auth ?? MaxAuth);
        Assert.True(result.Outcome == CorrectionOutcome.Ok, result.Message);
        return result.Value!;
    }

    [Fact]
    public async Task RunningBlockOverSixHours_IsAContinuousHintWithoutEnd()
    {
        // 05:00 to now (12:00 Berlin): 7 h.
        var sheet = _kimai.Add(At(5), null);

        var hints = await HintsAsync();

        Assert.Equal("Europe/Berlin", hints.TimeZone);
        var hint = Assert.Single(hints.Hints);
        Assert.Equal(new WorkTimeHintDto("continuous", "2026-10-05T05:00", null, 7 * 3600, sheet.Id), hint);
    }

    [Fact]
    public async Task RunningBlockUpToSixHours_HasNoHint()
    {
        _kimai.Add(At(6), null);

        Assert.Empty((await HintsAsync()).Hints);
    }

    [Fact]
    public async Task ABreakOfFifteenMinutes_SplitsTheBlock_AndATenMinuteBreakDoesNot()
    {
        // 05:00-08:00, 20 min pause, 08:20-12:00 (running): 3 h + 3 h 40 min, split by the pause.
        _kimai.Add(At(5), At(8));
        _kimai.Add(At(8), At(8, 20), activity: PauseActivity);
        _kimai.Add(At(8, 20), null);
        Assert.Empty((await HintsAsync()).Hints);

        // A 10 min break does not split: 6 h 50 min in one block.
        _kimai.Sheets.Clear();
        _kimai.Add(At(5), At(8));
        _kimai.Add(At(8), At(8, 10), activity: PauseActivity);
        _kimai.Add(At(8, 10), null);
        var hint = Assert.Single((await HintsAsync()).Hints);
        Assert.Equal("continuous", hint.Kind);
        Assert.Null(hint.End);
    }

    [Fact]
    public async Task StoppedShiftOverTenHours_IsAShiftHint_AlsoAsNightShiftOverMidnight()
    {
        // 20:00-01:00, 30 min pause, 01:30-07:00: 10 h 30 min work in one night shift, no 6 h block.
        _kimai.Add(Day(4, 20), Day(5, 1));
        _kimai.Add(Day(5, 1), Day(5, 1, 30), activity: PauseActivity);
        _kimai.Add(Day(5, 1, 30), Day(5, 7));

        var hint = Assert.Single((await HintsAsync()).Hints);

        Assert.Equal(new WorkTimeHintDto("shift", "2026-10-04T20:00", "2026-10-05T07:00", 10 * 3600 + 1800, null), hint);
    }

    [Fact]
    public async Task ShiftAndBlockTogether_AreBothReturned_BlockFirst()
    {
        // 22:00-09:00 without a break: 11 h, one block and one shift.
        var sheet = _kimai.Add(Day(4, 22), Day(5, 9));

        var hints = (await HintsAsync()).Hints;

        Assert.Equal(["continuous", "shift"], hints.Select(hint => hint.Kind));
        Assert.Equal(sheet.Id, hints[0].TimesheetId);
        Assert.Null(hints[1].TimesheetId);
        Assert.All(hints, hint => Assert.Equal(11 * 3600, hint.WorkedSeconds));
    }

    [Fact]
    public async Task ACaseOlderThan24HoursIsGone_AndOneJustInsideStays()
    {
        // Ended 28 h ago.
        _kimai.Add(Day(3, 21), Day(4, 8));
        Assert.Empty((await HintsAsync()).Hints);

        // Ended 23 h ago (Oct 4 13:00).
        _kimai.Sheets.Clear();
        _kimai.Add(Day(4, 1), Day(4, 13));
        Assert.Equal(["continuous", "shift"], (await HintsAsync()).Hints.Select(hint => hint.Kind));
    }

    [Fact]
    public async Task AnOpenRequestOnAnEntryOfTheBlock_SuppressesTheHint()
    {
        var first = _kimai.Add(Day(4, 22), Day(5, 2));
        _kimai.Add(Day(5, 2), Day(5, 2, 10), activity: PauseActivity);
        _kimai.Add(Day(5, 2, 10), Day(5, 9));
        Assert.Equal(["continuous", "shift"], (await HintsAsync()).Hints.Select(hint => hint.Kind));

        _store.Add(Request(TimeCorrectionKind.AddPause, first));

        Assert.Empty((await HintsAsync()).Hints);
    }

    [Theory]
    [InlineData(TimeCorrectionStatus.Pending, true)]
    [InlineData(TimeCorrectionStatus.Failed, true)]
    [InlineData(TimeCorrectionStatus.Applied, false)]
    [InlineData(TimeCorrectionStatus.Rejected, false)]
    [InlineData(TimeCorrectionStatus.Withdrawn, false)]
    [InlineData(TimeCorrectionStatus.ResolvedManually, false)]
    public async Task OnlyOpenRequestsSuppress(TimeCorrectionStatus status, bool suppresses)
    {
        var sheet = _kimai.Add(Day(4, 22), Day(5, 9));
        _store.Add(Request(TimeCorrectionKind.SetEnd, sheet, request => request with { Status = status }));

        Assert.Equal(suppresses, (await HintsAsync()).Hints.Count == 0);
    }

    [Fact]
    public async Task AnOpenRequestOfAnotherEmployee_DoesNotSuppress()
    {
        var sheet = _kimai.Add(Day(4, 22), Day(5, 9));
        _store.Add(Request(TimeCorrectionKind.SetEnd, sheet, request => request with { EmployeeId = "anna" }));

        Assert.Equal(2, (await HintsAsync()).Hints.Count);
    }

    [Fact]
    public async Task AnOpenRequestOnAnotherShift_DoesNotSuppress()
    {
        var old = _kimai.Add(Day(3, 8), Day(3, 12));
        _kimai.Add(Day(4, 22), Day(5, 9));
        _store.Add(Request(TimeCorrectionKind.SetEnd, old));

        Assert.Equal(2, (await HintsAsync()).Hints.Count);
    }

    [Fact]
    public async Task ContinuousTimesheetId_IsTheRunningWorkEntry_EvenWhenAStoppedOneIsLonger()
    {
        // 02:00-08:00 (6 h) + running 08:05-12:00 (3 h 55 min), 5 min gap.
        _kimai.Add(At(2), At(8));
        var running = _kimai.Add(At(8, 5), null);

        var hint = Assert.Single((await HintsAsync()).Hints);

        Assert.Equal("continuous", hint.Kind);
        Assert.Equal(running.Id, hint.TimesheetId);
    }

    [Fact]
    public async Task ContinuousTimesheetId_OfAStoppedBlock_IsTheLongestWorkEntry_NeverThePause()
    {
        // 03:00-04:00, 10 min pause, 04:10-08:00 (the longest), 08:00-09:30: 6 h 20 min in one block.
        _kimai.Add(At(3), At(4));
        _kimai.Add(At(4), At(4, 10), activity: PauseActivity);
        var longest = _kimai.Add(At(4, 10), At(8));
        _kimai.Add(At(8), At(9, 30));

        var hint = Assert.Single((await HintsAsync()).Hints);

        Assert.Equal(new WorkTimeHintDto("continuous", "2026-10-05T03:00", "2026-10-05T09:30", 6 * 3600 + 1200, longest.Id), hint);
    }

    [Fact]
    public async Task AWorkActivityOtherThanTheDefault_CountsAsWork()
    {
        // A task switch never interrupts a block: 05:00-08:00 default, 08:00-12:00 running on a task (project 5, activity 6).
        _kimai.Add(At(5), At(8));
        var running = _kimai.Add(At(8), null, activity: 6, project: 5);

        var hint = Assert.Single((await HintsAsync()).Hints);

        Assert.Equal(7 * 3600, hint.WorkedSeconds);
        Assert.Equal(running.Id, hint.TimesheetId);
    }

    [Fact]
    public async Task OtherEmployeesTimesheets_AreNotReported()
    {
        _kimai.Add(Day(4, 22), Day(5, 9), user: 99);

        Assert.Empty((await HintsAsync()).Hints);
    }

    [Fact]
    public async Task HintsAreReadOnly_NoBookingNoNotificationNoRequest()
    {
        _kimai.Add(At(5), null);

        await HintsAsync();

        Assert.Empty(_kimai.Writes);
        Assert.Empty(_notifier.Events);
        Assert.Empty(_store.List());
    }

    [Fact]
    public async Task TheCardAuthenticatesLikeThePin()
    {
        _kimai.Add(At(5), null);

        Assert.Single((await HintsAsync(new CorrectionAuthRequest("max", null, "04A2B3C4"))).Hints);
    }

    [Fact]
    public async Task WrongPin_IsUnauthorized_AndTheLockAppliesAfterTooManyAttempts()
    {
        var wrong = new CorrectionAuthRequest("max", "0000", null);

        for (var i = 0; i < PinAttemptGuard.EmployeeThreshold; i++)
        {
            Assert.Equal(CorrectionOutcome.Unauthorized, (await _service.GetWorkTimeHintsAsync(wrong)).Outcome);
        }

        await Assert.ThrowsAsync<PinLockedException>(() => _service.GetWorkTimeHintsAsync(MaxAuth));
    }

    [Fact]
    public async Task TheCardOfAnotherEmployee_IsUnauthorized()
    {
        var result = await _service.GetWorkTimeHintsAsync(new CorrectionAuthRequest("max", null, "04D5E6F7"));

        Assert.Equal(CorrectionOutcome.Unauthorized, result.Outcome);
    }

    [Fact]
    public async Task KimaiUnreachable_IsUnavailable()
    {
        _kimai.Unreachable = true;

        var result = await _service.GetWorkTimeHintsAsync(MaxAuth);

        Assert.Equal(CorrectionOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Value);
    }
}
