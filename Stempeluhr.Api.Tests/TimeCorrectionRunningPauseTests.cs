using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;
using static Stempeluhr.Api.Tests.TimeCorrection;

namespace Stempeluhr.Api.Tests;

/// <summary>Pause in einem noch laufenden Arbeits-Eintrag (Validator, Plan, Telegram-Text).</summary>
public sealed class RunningPauseRulesTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private static FakeKimai.Sheet Running(int id = 10, DateTimeOffset? begin = null)
        => new() { Id = id, Begin = begin ?? At(6), End = null };

    private static TimeCorrectionRequest AddPause(DateTimeOffset pauseBegin, DateTimeOffset pauseEnd, FakeKimai.Sheet? sheet = null)
        => Request(TimeCorrectionKind.AddPause, sheet ?? Running(), r => r with { PauseBegin = pauseBegin, PauseEnd = pauseEnd });

    private static string? Validate(
        TimeCorrectionRequest request,
        IReadOnlyCollection<KimaiTimesheetEntryDto>? timesheets = null,
        IReadOnlyCollection<TimeCorrectionRequest>? open = null,
        bool applying = false)
        => TimeCorrectionValidator.Validate(request, timesheets ?? [], open ?? [], Now, Berlin, Settings(), Max(), applying);

    // ---- Validator

    [Fact]
    public void Validator_RunningSheet_IsAllowed()
        => Assert.Null(Validate(AddPause(At(8), At(8, 30))));

    [Fact]
    public void Validator_PauseEndingNowOrWithinTheTolerance_IsAllowed()
    {
        Assert.Null(Validate(AddPause(At(11, 30), At(12))));
        Assert.Null(Validate(AddPause(At(11, 30), At(12, 2))));
    }

    [Fact]
    public void Validator_PauseEndingInTheFuture_IsRejected()
        => Assert.Contains("Zukunft", Validate(AddPause(At(11, 30), At(12, 3))));

    [Theory]
    [InlineData(5, 30)] // before the begin
    [InlineData(6, 0)]  // at the begin: the work before the pause would be empty
    public void Validator_PauseNotAfterTheBegin_IsRejected(int hour, int minute)
        => Assert.Contains("nach dem Beginn", Validate(AddPause(At(hour, minute), At(7))));

    [Fact]
    public void Validator_LongerThanFourHours_IsRejected()
        => Assert.Contains("höchstens 4 Stunden", Validate(AddPause(At(6, 30), At(10, 31))));

    [Fact]
    public void Validator_PauseEndBeforeBegin_IsRejected()
        => Assert.Contains("nach ihrem Beginn", Validate(AddPause(At(8, 30), At(8))));

    [Fact]
    public void Validator_PauseOverlappingAnotherEntry_IsRejected()
    {
        var other = new KimaiTimesheetEntryDto(11, At(8, 15), At(8, 45), null, WorkActivity, WorkProject);

        Assert.Contains("überschneidet", Validate(AddPause(At(8), At(8, 30)), [other]));
    }

    [Fact]
    public void Validator_AnotherOpenRequestForTheSheet_IsRejected()
    {
        var other = AddPause(At(9), At(9, 30));

        Assert.Contains("offenen Antrag", Validate(AddPause(At(8), At(8, 30)), open: [other]));
    }

    [Fact]
    public void Validator_RunningSheetOnAPauseActivity_IsRejected()
    {
        var pause = new FakeKimai.Sheet { Id = 10, Begin = At(6), End = null, Activity = PauseActivity };

        Assert.Contains("Arbeitseintrag", Validate(AddPause(At(8), At(8, 30), pause)));
    }

    [Fact]
    public void Validator_SetEndAndChangeTimes_StillNeedAStoppedSheet()
    {
        var setEnd = Request(TimeCorrectionKind.SetEnd, Running(), r => r with { End = At(8) });
        var changeTimes = Request(TimeCorrectionKind.ChangeTimes, Running(), r => r with { End = At(8) });

        Assert.Contains("läuft noch", Validate(setEnd));
        Assert.Contains("läuft noch", Validate(changeTimes));
    }

    // ---- Plan

    [Fact]
    public void Plan_RunningSheet_ShortensCreatesThePauseAndStartsTheRestRunning()
    {
        var steps = TimeCorrectionPlan.Steps(AddPause(At(8), At(8, 30)));

        Assert.Equal(
        [
            new PlannedStep("shorten", PlannedStepKind.Patch, null, At(8)),
            new PlannedStep("pause", PlannedStepKind.CreatePause, At(8), At(8, 30)),
            new PlannedStep("startWork", PlannedStepKind.StartWork, At(8, 30), null),
        ], steps);
    }

    [Fact]
    public void Plan_RunningSheetObservedRunning_StillStartsTheRestRunning()
    {
        var request = AddPause(At(8), At(8, 30)) with { ObservedAtApply = true, ObservedEndAtApply = null };

        Assert.Equal(["shorten", "pause", "startWork"], TimeCorrectionPlan.Steps(request).Select(step => step.Name));
    }

    [Fact]
    public void Plan_SheetStoppedBeforeApproval_CreatesTheRestUpToThatEnd()
    {
        var request = AddPause(At(8), At(8, 30)) with { ObservedAtApply = true, ObservedEndAtApply = At(11) };

        var steps = TimeCorrectionPlan.Steps(request);

        Assert.Equal(["shorten", "pause", "rest"], steps.Select(step => step.Name));
        Assert.Equal(new PlannedStep("rest", PlannedStepKind.CreateWork, At(8, 30), At(11)), steps[2]);
    }

    [Fact]
    public void Plan_SheetStoppedExactlyAtThePauseEnd_CreatesNoRest()
    {
        var request = AddPause(At(8), At(8, 30)) with { ObservedAtApply = true, ObservedEndAtApply = At(8, 30) };

        Assert.Equal(["shorten", "pause"], TimeCorrectionPlan.Steps(request).Select(step => step.Name));
    }

    [Fact]
    public void Plan_StoppedSheetAtSubmission_KeepsTheRestUpToTheOldEnd()
    {
        var request = AddPause(At(8), At(8, 30), new FakeKimai.Sheet { Id = 10, Begin = At(6), End = At(11) });

        Assert.Equal(new PlannedStep("rest", PlannedStepKind.CreateWork, At(8, 30), At(11)), TimeCorrectionPlan.Steps(request)[2]);
    }

    [Fact]
    public void IsCreatedBy_RunningRest_IgnoresTheEndButNeedsBeginAndActivity()
    {
        var step = new PlannedStep("startWork", PlannedStepKind.StartWork, At(8, 30), null);

        Assert.True(TimeCorrectionTargets.IsCreatedBy(new KimaiTimesheetEntryDto(1, At(8, 30), null, null, WorkActivity), step, WorkActivity));
        Assert.True(TimeCorrectionTargets.IsCreatedBy(new KimaiTimesheetEntryDto(1, At(8, 30), At(10), null, WorkActivity), step, WorkActivity));
        Assert.False(TimeCorrectionTargets.IsCreatedBy(new KimaiTimesheetEntryDto(1, At(8, 30), null, null, PauseActivity), step, WorkActivity));
        Assert.False(TimeCorrectionTargets.IsCreatedBy(new KimaiTimesheetEntryDto(1, At(8, 31), null, null, WorkActivity), step, WorkActivity));
    }

    [Fact]
    public void IsCreatedBy_StoppedStep_StillNeedsTheSameEnd()
    {
        var step = new PlannedStep("rest", PlannedStepKind.CreateWork, At(8, 30), At(11));

        Assert.True(TimeCorrectionTargets.IsCreatedBy(new KimaiTimesheetEntryDto(1, At(8, 30), At(11), null, WorkActivity), step, WorkActivity));
        Assert.False(TimeCorrectionTargets.IsCreatedBy(new KimaiTimesheetEntryDto(1, At(8, 30), null, null, WorkActivity), step, WorkActivity));
    }

    // ---- Telegram

    private static TimeCorrectionOriginal Original(DateTimeOffset begin, DateTimeOffset? end)
        => new(begin, end, WorkActivity, WorkProject, "Stempeluhr", true);

    [Fact]
    public void Telegram_RunningSheet_ShowsBeforeAndAfter()
    {
        var request = Request(TimeCorrectionKind.AddPause, configure: r => r with
        {
            Original = Original(At(6), null), PauseBegin = At(12), PauseEnd = At(12, 30),
        });

        Assert.Equal(
            "📝 Korrekturantrag · Max Mustermann\nPause nachtragen\n"
            + "Schicht: Mo 05.10. 06:00 – läuft\n"
            + "Pause: Mo 05.10. 12:00 – 12:30\n"
            + "Danach: Mo 05.10. 06:00–12:00 · Pause 12:00–12:30 · ab 12:30 (läuft)",
            TelegramMessageFactory.BuildCorrectionRequest(request, Berlin));
    }

    [Fact]
    public void Telegram_RunningNightShift_NamesTheDayWhereItChanges()
    {
        var request = Request(TimeCorrectionKind.AddPause, configure: r => r with
        {
            Original = Original(Day(5, 22), null), PauseBegin = Day(6, 2), PauseEnd = Day(6, 2, 30),
        });

        Assert.Contains(
            "Danach: Mo 05.10. 22:00–Di 06.10. 02:00 · Pause 02:00–02:30 · ab 02:30 (läuft)",
            TelegramMessageFactory.BuildCorrectionRequest(request, Berlin));
    }

    [Fact]
    public void Telegram_SheetStoppedBeforeApproval_NamesTheRestUpToThatEnd()
    {
        var request = Request(TimeCorrectionKind.AddPause, configure: r => r with
        {
            Original = Original(At(6), null), PauseBegin = At(8), PauseEnd = At(8, 30),
            ObservedAtApply = true, ObservedEndAtApply = At(11),
        });

        Assert.Contains(
            "Danach: Mo 05.10. 06:00–08:00 · Pause 08:00–08:30 · ab 08:30 bis 11:00",
            TelegramMessageFactory.BuildCorrectionDecision(request, Berlin));
    }

    [Fact]
    public void Telegram_StoppedSheet_HasNoAfterLine()
    {
        var request = Request(TimeCorrectionKind.AddPause, configure: r => r with
        {
            Original = Original(At(6), At(11)), PauseBegin = At(8), PauseEnd = At(8, 30),
        });

        Assert.DoesNotContain("Danach", TelegramMessageFactory.BuildCorrectionRequest(request, Berlin));
    }
}

/// <summary>Pause in einem noch laufenden Arbeits-Eintrag: Absenden und Genehmigen gegen ein In-Memory-Kimai.</summary>
public sealed class RunningPauseServiceTests : IDisposable
{
    private static readonly CorrectionAuthRequest MaxAuth = new("max", "1234", null);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"stempeluhr-corrections-{Guid.NewGuid():N}");
    private readonly FakeKimai _kimai = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly ManualClock _clock = new(Now);
    private readonly TimeCorrectionStore _store;
    private readonly TimeCorrectionService _service;

    public RunningPauseServiceTests()
    {
        _store = new TimeCorrectionStore(Path.Combine(_directory, "time-corrections.json"));
        _service = new TimeCorrectionService(
            new StubSettingsStore(Settings(Max(), Anna())), new EmployeeService(), _kimai, _store, _notifier,
            new PinAttemptGuard(clock: _clock), clock: _clock);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private FakeKimai.Sheet RunningSheet() => _kimai.Add(At(6), null);

    private async Task<TimeCorrectionDto> SubmitPause(FakeKimai.Sheet sheet, DateTimeOffset begin, DateTimeOffset end)
    {
        var request = new SubmitCorrectionRequest(
            MaxAuth.EmployeeId, MaxAuth.Pin, null, "addPause", sheet.Id, null, null, Text(begin), Text(end), null, null, null);
        var result = await _service.SubmitAsync(request);
        await _service.WhenNotificationsCompleteAsync();
        Assert.True(result.Outcome == CorrectionOutcome.Ok, result.Message);
        return result.Value!;
    }

    private async Task<TimeCorrectionDto> Approved(string id)
    {
        var result = await _service.ApproveAsync(id, "Admin");
        Assert.Equal(CorrectionOutcome.Ok, result.Outcome);
        return result.Value!;
    }

    private async Task<TimeCorrectionDto> Retried(string id)
    {
        var result = await _service.RetryAsync(id, "Admin");
        Assert.Equal(CorrectionOutcome.Ok, result.Outcome);
        return result.Value!;
    }

    [Fact]
    public async Task Submit_OnARunningSheet_StoresAPendingRequestWithoutBooking()
    {
        var sheet = RunningSheet();

        var dto = await SubmitPause(sheet, At(8), At(8, 30));

        Assert.Equal(TimeCorrectionStatus.Pending, dto.Status);
        Assert.Null(dto.Original!.End);
        var stored = _store.Find(dto.Id)!;
        Assert.Null(stored.Original!.End);
        Assert.False(stored.ObservedAtApply);
        Assert.Empty(_kimai.Writes);
        Assert.Null(sheet.End);
    }

    [Fact]
    public async Task Submit_PauseEndingInTheFuture_IsRejected()
    {
        var sheet = RunningSheet();
        var request = new SubmitCorrectionRequest(
            "max", "1234", null, "addPause", sheet.Id, null, null, Text(At(11, 30)), Text(At(12, 30)), null, null, null);

        var result = await _service.SubmitAsync(request);

        Assert.Equal(CorrectionOutcome.Invalid, result.Outcome);
        Assert.Contains("Zukunft", result.Message);
        Assert.Empty(_store.List());
    }

    [Fact]
    public async Task Submit_SetEndOnARunningSheet_IsStillRejected()
    {
        var sheet = RunningSheet();
        var request = new SubmitCorrectionRequest(
            "max", "1234", null, "setEnd", sheet.Id, null, Text(At(8)), null, null, null, null, null);

        var result = await _service.SubmitAsync(request);

        Assert.Equal(CorrectionOutcome.Invalid, result.Outcome);
        Assert.Contains("läuft noch", result.Message);
    }

    [Fact]
    public async Task Approve_WhileRunning_ShortensCreatesThePauseAndRestartsTheWorkRunning()
    {
        var sheet = _kimai.Add(At(6), null, activity: 6, project: 5);
        sheet.Description = "Kunde X Projekt";
        sheet.Billable = false;
        var dto = await SubmitPause(sheet, At(8), At(8, 30));

        var result = await Approved(dto.Id);

        Assert.Equal(TimeCorrectionStatus.Applied, result.Status);
        Assert.Equal(["shorten", "pause", "startWork"], result.AppliedSteps);
        Assert.Equal(
        [
            $"patch {sheet.Id} end=08:00",
            "create act=2 proj=1 08:00-08:30 desc=Pause billable=False",
            "start act=6 proj=5 08:30- desc=Kunde X Projekt billable=False",
        ], _kimai.Writes);
        var rest = _kimai.Sheets.Single(entry => entry.Begin == At(8, 30));
        Assert.Null(rest.End);
        Assert.Equal(At(8), sheet.End);
        var stored = _store.Find(dto.Id)!;
        Assert.True(stored.ObservedAtApply);
        Assert.Null(stored.ObservedEndAtApply);
    }

    [Fact]
    public async Task Approve_AfterTheEmployeeStoppedMeanwhile_BooksTheRestUpToThatEnd()
    {
        var sheet = RunningSheet();
        var dto = await SubmitPause(sheet, At(8), At(8, 30));
        sheet.End = At(11);

        var result = await Approved(dto.Id);

        Assert.Equal(TimeCorrectionStatus.Applied, result.Status);
        Assert.Equal(["shorten", "pause", "rest"], result.AppliedSteps);
        Assert.Equal(
        [
            $"patch {sheet.Id} end=08:00",
            "create act=2 proj=1 08:00-08:30 desc=Pause billable=False",
            "create act=1 proj=1 08:30-11:00 desc=Stempeluhr billable=True",
        ], _kimai.Writes);
        Assert.DoesNotContain(_kimai.Sheets, entry => entry.End is null);
        Assert.Equal(At(11), _store.Find(dto.Id)!.ObservedEndAtApply);
    }

    [Fact]
    public async Task Approve_AfterTheEmployeeStoppedExactlyAtThePauseEnd_BooksNoRest()
    {
        var sheet = RunningSheet();
        var dto = await SubmitPause(sheet, At(8), At(8, 30));
        sheet.End = At(8, 30);

        var result = await Approved(dto.Id);

        Assert.Equal(["shorten", "pause"], result.AppliedSteps);
        Assert.Equal(TimeCorrectionStatus.Applied, result.Status);
        Assert.Equal(2, _kimai.Writes.Count);
    }

    [Fact]
    public async Task Approve_WhenTheEmployeeStoppedBeforeThePauseEnded_FailsAsChanged()
    {
        var sheet = RunningSheet();
        var dto = await SubmitPause(sheet, At(8), At(8, 30));
        sheet.End = At(8, 15);

        var result = await Approved(dto.Id);

        Assert.Equal(TimeCorrectionStatus.Failed, result.Status);
        Assert.Equal("Eintrag wurde inzwischen geändert", result.Error);
        Assert.Empty(_kimai.Writes);
        Assert.False(_store.Find(dto.Id)!.ObservedAtApply);
    }

    [Fact]
    public async Task Approve_WhenTheBeginChangedMeanwhile_FailsAsChanged()
    {
        var sheet = RunningSheet();
        var dto = await SubmitPause(sheet, At(8), At(8, 30));
        sheet.Begin = At(6, 10);

        var result = await Approved(dto.Id);

        Assert.Equal("Eintrag wurde inzwischen geändert", result.Error);
        Assert.Empty(_kimai.Writes);
    }

    [Fact]
    public async Task Retry_AfterTheRestartFailed_ContinuesWithTheRestartAndDoesNotRepeatEarlierSteps()
    {
        var sheet = RunningSheet();
        var dto = await SubmitPause(sheet, At(8), At(8, 30));
        _kimai.FailBeforeCreate = 2; // the pause is created (1st), the restart (2nd) fails

        var failed = await Approved(dto.Id);

        Assert.Equal(TimeCorrectionStatus.Failed, failed.Status);
        Assert.Equal(["shorten", "pause"], failed.AppliedSteps);
        // The sheet is stopped at 08:00 now: Only the stored observation still says "was running".
        Assert.Equal(At(8), sheet.End);

        var retried = await Retried(dto.Id);

        Assert.Equal(TimeCorrectionStatus.Applied, retried.Status);
        Assert.Equal(["shorten", "pause", "startWork"], retried.AppliedSteps);
        Assert.Single(_kimai.Writes, write => write.StartsWith("patch"));
        Assert.Single(_kimai.Writes, write => write.StartsWith("start"));
        Assert.Single(_kimai.Sheets, entry => entry.Activity == PauseActivity);
        Assert.Single(_kimai.Sheets, entry => entry.End is null && entry.Begin == At(8, 30));
        Assert.Equal(3, _kimai.Sheets.Count);
    }

    [Fact]
    public async Task Retry_AfterAStoppedPlanFailed_KeepsFollowingTheStoppedPlan()
    {
        var sheet = RunningSheet();
        var dto = await SubmitPause(sheet, At(8), At(8, 30));
        sheet.End = At(11);
        _kimai.FailBeforeCreate = 2; // pause created, rest fails

        var failed = await Approved(dto.Id);
        Assert.Equal(["shorten", "pause"], failed.AppliedSteps);

        var retried = await Retried(dto.Id);

        Assert.Equal(["shorten", "pause", "rest"], retried.AppliedSteps);
        Assert.DoesNotContain(_kimai.Writes, write => write.StartsWith("start"));
        Assert.Single(_kimai.Sheets, entry => entry.Begin == At(8, 30) && entry.End == At(11));
        Assert.DoesNotContain(_kimai.Sheets, entry => entry.End is null);
    }

    [Fact]
    public async Task Retry_WhenKimaiStartedTheWorkButTheAnswerWasLost_DoesNotStartItTwice()
    {
        var sheet = RunningSheet();
        var dto = await SubmitPause(sheet, At(8), At(8, 30));
        _kimai.FailAfterCreate = 2; // the restart exists in Kimai, the client saw an error

        var failed = await Approved(dto.Id);
        Assert.Equal(TimeCorrectionStatus.Failed, failed.Status);
        Assert.Equal(["shorten", "pause"], failed.AppliedSteps);

        var retried = await Retried(dto.Id);

        Assert.Equal(TimeCorrectionStatus.Applied, retried.Status);
        Assert.Single(_kimai.Writes, write => write.StartsWith("start"));
        Assert.Equal(3, _kimai.Sheets.Count);
    }

    [Fact]
    public async Task Retry_WhenTheRestartedWorkWasStoppedMeanwhile_RecognizesItAndBooksNothingMore()
    {
        var sheet = RunningSheet();
        var dto = await SubmitPause(sheet, At(8), At(8, 30));
        _kimai.FailAfterCreate = 2;
        await Approved(dto.Id);
        _kimai.Sheets.Single(entry => entry.Begin == At(8, 30)).End = At(10);
        var writes = _kimai.Writes.Count;

        var retried = await Retried(dto.Id);

        Assert.Equal(TimeCorrectionStatus.Applied, retried.Status);
        Assert.Equal(writes, _kimai.Writes.Count);
    }

    [Fact]
    public async Task Approve_WhenSomethingElseRunsBeforeTheRestart_Fails_AndStartsNothing()
    {
        var sheet = RunningSheet();
        var dto = await SubmitPause(sheet, At(8), At(8, 30));
        _kimai.FailBeforeCreate = 2; // restart fails once; the employee starts something else meanwhile
        await Approved(dto.Id);
        _kimai.Add(At(9), null, activity: 6, project: 5);

        var retried = await Retried(dto.Id);

        Assert.Equal(TimeCorrectionStatus.Failed, retried.Status);
        Assert.Equal("Eintrag wurde inzwischen geändert", retried.Error);
        Assert.Equal(["shorten", "pause"], retried.AppliedSteps);
        Assert.DoesNotContain(_kimai.Writes, write => write.StartsWith("start"));
    }

    [Fact]
    public async Task Retry_WhenTheEmployeeStoppedAfterTheObservationButBeforeTheShortening_FailsInsteadOfRestartingWork()
    {
        var sheet = RunningSheet();
        var dto = await SubmitPause(sheet, At(8), At(8, 30));
        // The observation "was running" is stored, but no step ran (e.g. the process stopped).
        _store.Update(_store.Find(dto.Id)! with { Status = TimeCorrectionStatus.Failed, ObservedAtApply = true, ObservedEndAtApply = null });
        sheet.End = At(11);

        var retried = await Retried(dto.Id);

        Assert.Equal(TimeCorrectionStatus.Failed, retried.Status);
        Assert.Equal("Eintrag wurde inzwischen geändert", retried.Error);
        Assert.Empty(_kimai.Writes);
    }

    [Fact]
    public async Task Approve_ARequestStoredBeforeTheObservationExisted_StillWorks()
    {
        // Older time-corrections.json files have neither field: a stopped sheet keeps its old plan.
        var sheet = _kimai.Add(At(6), At(11));
        var dto = await SubmitPause(sheet, At(8), At(8, 30));

        var result = await Approved(dto.Id);

        Assert.Equal(["shorten", "pause", "rest"], result.AppliedSteps);
        Assert.False(_store.Find(dto.Id)!.ObservedAtApply);
    }
}
