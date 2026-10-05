using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;
using static Stempeluhr.Api.Tests.TimeCorrection;

namespace Stempeluhr.Api.Tests;

public sealed class TimeCorrectionServiceTests : IDisposable
{
    private static readonly CorrectionAuthRequest MaxAuth = new("max", "1234", null);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"stempeluhr-corrections-{Guid.NewGuid():N}");
    private readonly FakeKimai _kimai = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly ManualClock _clock = new(Now);
    private readonly PinAttemptGuard _guard;
    private readonly TimeCorrectionStore _store;
    private readonly TimeCorrectionService _service;

    public TimeCorrectionServiceTests()
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

    private static SubmitCorrectionRequest Submit(
        string kind,
        int? timesheetId = null,
        DateTimeOffset? begin = null,
        DateTimeOffset? end = null,
        DateTimeOffset? pauseBegin = null,
        DateTimeOffset? pauseEnd = null,
        string? taskId = null,
        string? comment = null,
        string? source = null,
        CorrectionAuthRequest? auth = null)
    {
        auth ??= MaxAuth;
        return new SubmitCorrectionRequest(
            auth.EmployeeId, auth.Pin, auth.NfcCardId, kind, timesheetId,
            begin is { } b ? Text(b) : null, end is { } e ? Text(e) : null,
            pauseBegin is { } pb ? Text(pb) : null, pauseEnd is { } pe ? Text(pe) : null,
            taskId, comment, source);
    }

    private async Task<TimeCorrectionDto> Submitted(SubmitCorrectionRequest request)
    {
        var result = await _service.SubmitAsync(request);
        Assert.True(result.Outcome == CorrectionOutcome.Ok, result.Message);
        return result.Value!;
    }

    private async Task<TimeCorrectionDto> Approved(string id)
    {
        var result = await _service.ApproveAsync(id, "Admin");
        Assert.Equal(CorrectionOutcome.Ok, result.Outcome);
        return result.Value!;
    }

    private FakeKimai.Sheet WorkSheet(int fromHour = 6, int toHour = 11) => _kimai.Add(At(fromHour), At(toHour));

    // ------------------------------------------------------------ Kinds

    [Fact]
    public async Task Submit_AddPause_StoresAPendingRequestWithSnapshotAndLocalTimes()
    {
        var sheet = WorkSheet();

        var dto = await Submitted(Submit("addPause", sheet.Id, pauseBegin: At(8), pauseEnd: At(8, 30), comment: " vergessen ", source: "terminal-1"));

        Assert.Equal(TimeCorrectionStatus.Pending, dto.Status);
        Assert.Equal(TimeCorrectionKind.AddPause, dto.Kind);
        Assert.Equal("2026-10-05T08:00", dto.PauseBegin);
        Assert.Equal("2026-10-05T08:30", dto.PauseEnd);
        Assert.Equal("Europe/Berlin", dto.TimeZone);
        Assert.Equal("vergessen", dto.Comment);
        Assert.Equal("terminal-1", dto.Source);
        Assert.Equal("2026-10-05T06:00", dto.Original!.Begin);
        var stored = _store.Find(dto.Id)!;
        Assert.Equal(At(8), stored.PauseBegin);
        Assert.Equal(sheet.Id, stored.TimesheetId);
        Assert.Equal(new TimeCorrectionOriginal(At(6), At(11), WorkActivity, WorkProject, "Stempeluhr", true), stored.Original);
        Assert.Empty(_kimai.Writes);
        Assert.Equal(["submitted:Pending"], _notifier.Events);
    }

    [Fact]
    public async Task Approve_AddPause_ShortensTheWorkCreatesThePauseAndTheRemainingWork()
    {
        var sheet = WorkSheet();
        var dto = await Submitted(Submit("addPause", sheet.Id, pauseBegin: At(8), pauseEnd: At(8, 30)));

        var result = await Approved(dto.Id);

        Assert.Equal(TimeCorrectionStatus.Applied, result.Status);
        Assert.Equal(["shorten", "pause", "rest"], result.AppliedSteps);
        Assert.Equal("Admin", result.DecidedBy);
        Assert.Equal(
        [
            $"patch {sheet.Id} end=08:00",
            "create act=2 proj=1 08:00-08:30 desc=Pause billable=False",
            "create act=1 proj=1 08:30-11:00 desc=Stempeluhr billable=True",
        ], _kimai.Writes);
        Assert.Equal(["submitted:Pending", "decided:Applied"], _notifier.Events);
    }

    [Fact]
    public async Task Approve_AddPause_RestKeepsProjectActivityDescriptionAndBillableOfTheOriginal()
    {
        var sheet = _kimai.Add(At(6), At(11), activity: 6, project: 5);
        sheet.Description = "Kunde X Projekt";
        sheet.Billable = false;
        var dto = await Submitted(Submit("addPause", sheet.Id, pauseBegin: At(8), pauseEnd: At(8, 30)));

        await Approved(dto.Id);

        Assert.Equal("create act=6 proj=5 08:30-11:00 desc=Kunde X Projekt billable=False".Replace("act=6 proj=5 08:30", "act=6 proj=5 08:30"), _kimai.Writes[^1]);
    }

    [Fact]
    public async Task Approve_AddPause_EndingAtTheOldEnd_CreatesNoRestWork()
    {
        var sheet = WorkSheet();
        var dto = await Submitted(Submit("addPause", sheet.Id, pauseBegin: At(10, 30), pauseEnd: At(11)));

        var result = await Approved(dto.Id);

        Assert.Equal(["shorten", "pause"], result.AppliedSteps);
        Assert.Equal(2, _kimai.Writes.Count);
        Assert.Equal(TimeCorrectionStatus.Applied, result.Status);
    }

    [Fact]
    public async Task Approve_SetEnd_PatchesTheEnd()
    {
        var sheet = WorkSheet();
        var dto = await Submitted(Submit("setEnd", sheet.Id, end: At(10, 15)));

        Assert.Equal(TimeCorrectionStatus.Applied, (await Approved(dto.Id)).Status);

        Assert.Equal([$"patch {sheet.Id} end=10:15"], _kimai.Writes);
    }

    [Fact]
    public async Task Approve_AddShift_WithPauseAndTask_CreatesWorkPauseWork()
    {
        var dto = await Submitted(Submit("addShift", begin: At(6), end: At(10), pauseBegin: At(8), pauseEnd: At(8, 30), taskId: "kx"));
        Assert.Equal("Kunde X", dto.TaskLabel);

        var result = await Approved(dto.Id);

        Assert.Equal(TimeCorrectionStatus.Applied, result.Status);
        Assert.Equal(
        [
            "create act=6 proj=5 06:00-08:00 desc=Nachgetragen (Korrekturantrag) billable=True",
            "create act=2 proj=1 08:00-08:30 desc=Pause billable=False",
            "create act=6 proj=5 08:30-10:00 desc=Nachgetragen (Korrekturantrag) billable=True",
        ], _kimai.Writes);
    }

    [Fact]
    public async Task Approve_AddShift_WithoutPause_CreatesOneEntryOnTheMainTask_AcrossMidnight()
    {
        var dto = await Submitted(Submit("addShift", begin: Day(4, 22), end: Day(5, 6)));

        await Approved(dto.Id);

        Assert.Equal(["create act=1 proj=1 22:00-06:00 desc=Nachgetragen (Korrekturantrag) billable=True"], _kimai.Writes);
        Assert.Equal(Day(4, 22), Assert.Single(_kimai.Sheets).Begin);
    }

    [Fact]
    public async Task Approve_ChangeTimes_OnlyPatchesWhatChanged_AndKeepsTheSecondsOfAnUnchangedTime()
    {
        var sheet = _kimai.Add(At(6).AddSeconds(30), At(11));
        // The kiosk shows 06:00 for 06:00:30 and sends it back unchanged.
        var dto = await Submitted(Submit("changeTimes", sheet.Id, begin: At(6), end: At(11, 30)));
        Assert.Null(dto.Begin);
        Assert.Equal("2026-10-05T11:30", dto.End);

        await Approved(dto.Id);

        Assert.Equal([$"patch {sheet.Id} end=11:30"], _kimai.Writes);
        Assert.Equal(At(6).AddSeconds(30), sheet.Begin);
    }

    // ------------------------------------------------------------ Conflict, idempotence, errors

    [Fact]
    public async Task Approve_AfterTheSheetChanged_FailsWithoutBookingAnything()
    {
        var sheet = WorkSheet();
        var dto = await Submitted(Submit("addPause", sheet.Id, pauseBegin: At(8), pauseEnd: At(8, 30)));
        sheet.End = At(10); // changed in Kimai after the request

        var result = await Approved(dto.Id);

        Assert.Equal(TimeCorrectionStatus.Failed, result.Status);
        Assert.Equal("Eintrag wurde inzwischen geändert", result.Error);
        Assert.Empty(_kimai.Writes);
    }

    [Theory]
    [InlineData("description")]
    [InlineData("activity")]
    [InlineData("billable")]
    public async Task Approve_AfterOtherFieldsChanged_FailsAsWell(string field)
    {
        var sheet = WorkSheet();
        var dto = await Submitted(Submit("setEnd", sheet.Id, end: At(10)));
        switch (field)
        {
            case "description": sheet.Description = "anderer Text"; break;
            case "activity": sheet.Activity = 6; break;
            default: sheet.Billable = false; break;
        }

        var result = await Approved(dto.Id);

        Assert.Equal(TimeCorrectionStatus.Failed, result.Status);
        Assert.Empty(_kimai.Writes);
    }

    [Fact]
    public async Task Approve_WhenKimaiBooksSomethingOverlappingMeanwhile_FailsWithTheRuleMessage()
    {
        var sheet = WorkSheet();
        var dto = await Submitted(Submit("addPause", sheet.Id, pauseBegin: At(8), pauseEnd: At(8, 30)));
        _kimai.Add(At(8, 10), At(8, 20), PauseActivity); // someone stamped a pause there

        var result = await Approved(dto.Id);

        Assert.Equal(TimeCorrectionStatus.Failed, result.Status);
        Assert.Contains("überschneidet", result.Error);
        Assert.Empty(_kimai.Writes);
    }

    [Fact]
    public async Task Retry_AfterAPartialFailure_ContinuesAtTheOpenStepWithoutDoubleBooking()
    {
        var sheet = WorkSheet();
        var dto = await Submitted(Submit("addPause", sheet.Id, pauseBegin: At(8), pauseEnd: At(8, 30)));
        _kimai.FailBeforeCreate = 1; // the pause cannot be created

        var failed = await Approved(dto.Id);

        Assert.Equal(TimeCorrectionStatus.Failed, failed.Status);
        Assert.Equal(["shorten"], failed.AppliedSteps);
        Assert.Contains("500", failed.Error);

        var retried = (await _service.RetryAsync(dto.Id, "Admin")).Value!;

        Assert.Equal(TimeCorrectionStatus.Applied, retried.Status);
        Assert.Equal(["shorten", "pause", "rest"], retried.AppliedSteps);
        Assert.Single(_kimai.Writes, write => write.StartsWith("patch"));
        Assert.Single(_kimai.Sheets, entry => entry.Activity == PauseActivity);
        Assert.Equal(3, _kimai.Sheets.Count);
        Assert.Equal(At(8), sheet.End);
    }

    [Fact]
    public async Task Retry_WhenKimaiCreatedTheEntryButTheAnswerWasLost_DoesNotCreateItTwice()
    {
        var sheet = WorkSheet();
        var dto = await Submitted(Submit("addPause", sheet.Id, pauseBegin: At(8), pauseEnd: At(8, 30)));
        _kimai.FailAfterCreate = 1; // pause exists in Kimai, the client saw an error

        var failed = await Approved(dto.Id);
        Assert.Equal(TimeCorrectionStatus.Failed, failed.Status);
        Assert.Equal(["shorten"], failed.AppliedSteps);

        var retried = (await _service.RetryAsync(dto.Id, "Admin")).Value!;

        Assert.Equal(TimeCorrectionStatus.Applied, retried.Status);
        Assert.Single(_kimai.Sheets, entry => entry.Activity == PauseActivity);
        Assert.Equal(3, _kimai.Sheets.Count);
    }

    [Fact]
    public async Task Approve_AfterTheFirstStepRanButTheProgressWasNotSaved_DoesNotPatchAgain()
    {
        var sheet = WorkSheet();
        var dto = await Submitted(Submit("addPause", sheet.Id, pauseBegin: At(8), pauseEnd: At(8, 30)));
        sheet.End = At(8); // the PATCH went through, then the process died before saving it

        var result = await Approved(dto.Id);

        Assert.Equal(TimeCorrectionStatus.Applied, result.Status);
        Assert.DoesNotContain(_kimai.Writes, write => write.StartsWith("patch"));
        Assert.Equal(3, _kimai.Sheets.Count);
    }

    [Fact]
    public async Task Approve_Twice_BooksOnce_AndReturnsTheCurrentState()
    {
        var sheet = WorkSheet();
        var dto = await Submitted(Submit("setEnd", sheet.Id, end: At(10)));

        var first = await Approved(dto.Id);
        var second = await Approved(dto.Id);

        Assert.Equal(TimeCorrectionStatus.Applied, first.Status);
        Assert.Equal(TimeCorrectionStatus.Applied, second.Status);
        Assert.Single(_kimai.Writes);
        Assert.Equal(["submitted:Pending", "decided:Applied"], _notifier.Events);
    }

    [Fact]
    public async Task Approve_ConcurrentlyFromAdminAndTelegram_BooksOnce()
    {
        var sheet = WorkSheet();
        var dto = await Submitted(Submit("addPause", sheet.Id, pauseBegin: At(8), pauseEnd: At(8, 30)));

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => _service.ApproveAsync(dto.Id, i == 0 ? "Admin" : "Telegram")));

        Assert.All(results, result => Assert.Equal(TimeCorrectionStatus.Applied, result.Value!.Status));
        Assert.Equal(3, _kimai.Writes.Count);
        Assert.Equal(3, _kimai.Sheets.Count);
    }

    [Fact]
    public async Task Approve_WhenKimaiAnswers400_FailsWithTheMessage_NotWithAnException()
    {
        var sheet = WorkSheet();
        var dto = await Submitted(Submit("setEnd", sheet.Id, end: At(10)));
        _kimai.Sheets.Single().End = At(11);
        var failing = new FailingPatchKimai(_kimai);
        var service = new TimeCorrectionService(
            new StubSettingsStore(Settings(Max(), Anna())), new EmployeeService(), failing, _store, _notifier, _guard, clock: _clock);

        var result = (await service.ApproveAsync(dto.Id, "Admin")).Value!;

        Assert.Equal(TimeCorrectionStatus.Failed, result.Status);
        Assert.Contains("400", result.Error);
        Assert.Contains("Erfassungsmodus", result.Error);
        Assert.Empty(_kimai.Writes);
    }

    [Fact]
    public async Task Approve_WhenKimaiIsUnreachable_FailsAndCanBeRetried()
    {
        var sheet = WorkSheet();
        var dto = await Submitted(Submit("setEnd", sheet.Id, end: At(10)));
        _kimai.Unreachable = true;

        var failed = await Approved(dto.Id);
        Assert.Equal(TimeCorrectionStatus.Failed, failed.Status);
        Assert.Equal("Kimai ist nicht erreichbar.", failed.Error);

        _kimai.Unreachable = false;
        Assert.Equal(TimeCorrectionStatus.Applied, (await _service.RetryAsync(dto.Id, "Admin")).Value!.Status);
        Assert.Equal(At(10), sheet.End);
    }

    // ------------------------------------------------------------ Ownership (UserId)

    [Fact]
    public async Task Submit_ForAForeignTimesheet_IsRejectedAndNothingIsStoredOrWritten()
    {
        // Kimai answers GET /api/timesheets/{id} for a colleague's sheet.
        var foreign = _kimai.Add(At(6), At(11), user: 12);

        var result = await _service.SubmitAsync(Submit("setEnd", foreign.Id, end: At(10)));

        Assert.Equal(CorrectionOutcome.Invalid, result.Outcome);
        Assert.Equal("Eintrag nicht gefunden.", result.Message);
        Assert.Empty(_store.List());
        Assert.Empty(_kimai.Writes);
        Assert.Empty(_notifier.Events);
    }

    [Fact]
    public async Task Submit_WhenKimaiGivesNoUserIdForTheSheet_IsRejectedInsteadOfGuessed()
    {
        var anonymous = _kimai.Add(At(6), At(11), user: null);

        var result = await _service.SubmitAsync(Submit("setEnd", anonymous.Id, end: At(10)));

        Assert.Equal(CorrectionOutcome.Invalid, result.Outcome);
        Assert.Empty(_store.List());
    }

    [Fact]
    public async Task Submit_WhenTheOwnUserIdIsUnknown_IsRejected()
    {
        var sheet = WorkSheet();
        _kimai.UserId = null;

        var result = await _service.SubmitAsync(Submit("setEnd", sheet.Id, end: At(10)));

        Assert.Equal(CorrectionOutcome.Invalid, result.Outcome);
        Assert.Contains("Kimai-Benutzer", result.Message);
        Assert.Empty(_store.List());
    }

    [Fact]
    public async Task Approve_WhenTheSheetIsNotTheEmployeesAnymore_FailsWithoutWriting()
    {
        var sheet = WorkSheet();
        var dto = await Submitted(Submit("setEnd", sheet.Id, end: At(10)));
        sheet.User = 12;

        var result = await Approved(dto.Id);

        Assert.Equal(TimeCorrectionStatus.Failed, result.Status);
        Assert.Contains("nicht erlaubt", result.Error);
        Assert.Empty(_kimai.Writes);
    }

    [Fact]
    public async Task Submit_UnknownTimesheet_IsRejected()
    {
        var result = await _service.SubmitAsync(Submit("setEnd", 9999, end: At(10)));

        Assert.Equal(CorrectionOutcome.Invalid, result.Outcome);
        Assert.Equal("Eintrag nicht gefunden.", result.Message);
    }

    // ------------------------------------------------------------ Submit rules

    [Fact]
    public async Task Submit_OnlyOneOpenRequestPerTimesheet()
    {
        var sheet = WorkSheet();
        await Submitted(Submit("setEnd", sheet.Id, end: At(10)));

        var second = await _service.SubmitAsync(Submit("setEnd", sheet.Id, end: At(9)));

        Assert.Equal(CorrectionOutcome.Invalid, second.Outcome);
        Assert.Contains("offenen Antrag", second.Message);
        Assert.Single(_store.List());
    }

    [Fact]
    public async Task Submit_ViolatingARule_Returns400WithAGermanMessage()
    {
        var sheet = WorkSheet();

        var result = await _service.SubmitAsync(Submit("addPause", sheet.Id, pauseBegin: At(5), pauseEnd: At(5, 30)));

        Assert.Equal(CorrectionOutcome.Invalid, result.Outcome);
        Assert.Contains("innerhalb", result.Message);
    }

    [Theory]
    [InlineData("neuerEintrag")]
    [InlineData("")]
    [InlineData("7")]
    [InlineData(null)]
    public async Task Submit_UnknownKind_IsRejected(string? kind)
    {
        var result = await _service.SubmitAsync(Submit(kind!, begin: At(6), end: At(7)));

        Assert.Equal(CorrectionOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task Submit_UnparsableOrNonExistingLocalTimes_AreRejected()
    {
        var bad = Submit("addShift", begin: At(6), end: At(7)) with { End = "morgen früh" };
        Assert.Equal("Ungültige Zeitangabe.", (await _service.SubmitAsync(bad)).Message);

        // 2026-03-29 02:30 does not exist in Berlin (clocks jump from 02:00 to 03:00).
        var gap = Submit("addShift", begin: At(6), end: At(7)) with { Begin = "2026-03-29T02:30" };
        Assert.Contains("Zeitumstellung", (await _service.SubmitAsync(gap)).Message);
    }

    [Fact]
    public async Task Submit_UnknownTask_IsRejected()
    {
        var result = await _service.SubmitAsync(Submit("addShift", begin: At(6), end: At(7), taskId: "gibt-es-nicht"));

        Assert.Equal(CorrectionOutcome.Invalid, result.Outcome);
        Assert.Contains("Tätigkeit", result.Message);
    }

    [Fact]
    public async Task Submit_WhenKimaiIsUnreachable_IsUnavailableAndStoresNothing()
    {
        var sheet = WorkSheet();
        _kimai.Unreachable = true;

        var result = await _service.SubmitAsync(Submit("setEnd", sheet.Id, end: At(10)));

        Assert.Equal(CorrectionOutcome.Unavailable, result.Outcome);
        Assert.Empty(_store.List());
    }

    [Fact]
    public async Task Submit_SourceDefaultsToClock_AndTheCommentIsLimited()
    {
        var dto = await Submitted(Submit("addShift", begin: At(6), end: At(7)));
        Assert.Equal("clock", dto.Source);
        Assert.Null(dto.Comment);

        var tooLong = await _service.SubmitAsync(Submit("addShift", begin: At(8), end: At(9), comment: new string('x', 301)));
        Assert.Equal(CorrectionOutcome.Invalid, tooLong.Outcome);
    }

    // ------------------------------------------------------------ Withdraw, reject, resolve

    [Fact]
    public async Task Withdraw_OnlyPendingRequestsOfTheEmployee()
    {
        var sheet = WorkSheet();
        var dto = await Submitted(Submit("setEnd", sheet.Id, end: At(10)));

        // Another employee sees no such request.
        var foreign = await _service.WithdrawAsync(new CorrectionAuthRequest("anna", "5678", null), dto.Id);
        Assert.Equal(CorrectionOutcome.NotFound, foreign.Outcome);
        Assert.Equal(TimeCorrectionStatus.Pending, _store.Find(dto.Id)!.Status);

        var withdrawn = await _service.WithdrawAsync(MaxAuth, dto.Id);
        Assert.Equal(TimeCorrectionStatus.Withdrawn, withdrawn.Value!.Status);
        Assert.Equal(["submitted:Pending", "decided:Withdrawn"], _notifier.Events);

        // Withdrawn requests cannot be approved any more, nor withdrawn twice.
        Assert.Equal(CorrectionOutcome.Conflict, (await _service.WithdrawAsync(MaxAuth, dto.Id)).Outcome);
        Assert.Equal(TimeCorrectionStatus.Withdrawn, (await Approved(dto.Id)).Status);
        Assert.Empty(_kimai.Writes);
    }

    [Fact]
    public async Task Withdraw_AnAlreadyApprovedRequest_IsAConflict()
    {
        var sheet = WorkSheet();
        var dto = await Submitted(Submit("setEnd", sheet.Id, end: At(10)));
        await Approved(dto.Id);

        var result = await _service.WithdrawAsync(MaxAuth, dto.Id);

        Assert.Equal(CorrectionOutcome.Conflict, result.Outcome);
        Assert.Equal(TimeCorrectionStatus.Applied, _store.Find(dto.Id)!.Status);
    }

    [Fact]
    public async Task Withdraw_UnknownRequest_IsNotFound()
        => Assert.Equal(CorrectionOutcome.NotFound, (await _service.WithdrawAsync(MaxAuth, "unknown")).Outcome);

    [Fact]
    public async Task Reject_StoresTheNote_AndALaterApproveDoesNotBook()
    {
        var sheet = WorkSheet();
        var dto = await Submitted(Submit("setEnd", sheet.Id, end: At(10)));

        var rejected = (await _service.RejectAsync(dto.Id, "  Bitte mit dem Chef sprechen ", "Admin")).Value!;
        Assert.Equal(TimeCorrectionStatus.Rejected, rejected.Status);
        Assert.Equal("Bitte mit dem Chef sprechen", rejected.DecisionNote);

        Assert.Equal(TimeCorrectionStatus.Rejected, (await Approved(dto.Id)).Status);
        Assert.Equal(TimeCorrectionStatus.Rejected, (await _service.RejectAsync(dto.Id, "anders", "Telegram")).Value!.Status);
        Assert.Equal("Bitte mit dem Chef sprechen", _store.Find(dto.Id)!.DecisionNote);
        Assert.Empty(_kimai.Writes);
    }

    [Fact]
    public async Task Reject_AnAppliedRequest_ChangesNothing()
    {
        var sheet = WorkSheet();
        var dto = await Submitted(Submit("setEnd", sheet.Id, end: At(10)));
        await Approved(dto.Id);

        var result = await _service.RejectAsync(dto.Id, null, "Telegram");

        Assert.Equal(TimeCorrectionStatus.Applied, result.Value!.Status);
    }

    [Fact]
    public async Task ResolveManually_AndRetry_OnlyForFailedRequests()
    {
        var sheet = WorkSheet();
        var dto = await Submitted(Submit("setEnd", sheet.Id, end: At(10)));

        Assert.Equal(CorrectionOutcome.Conflict, (await _service.ResolveManuallyAsync(dto.Id, "Admin")).Outcome);
        Assert.Equal(CorrectionOutcome.Conflict, (await _service.RetryAsync(dto.Id, "Admin")).Outcome);

        sheet.End = At(9); // conflict -> Failed
        Assert.Equal(TimeCorrectionStatus.Failed, (await Approved(dto.Id)).Status);

        var resolved = await _service.ResolveManuallyAsync(dto.Id, "Admin");
        Assert.Equal(TimeCorrectionStatus.ResolvedManually, resolved.Value!.Status);
        Assert.Equal(CorrectionOutcome.Conflict, (await _service.RetryAsync(dto.Id, "Admin")).Outcome);
        Assert.Equal(["submitted:Pending", "decided:Failed", "decided:ResolvedManually"], _notifier.Events);
        Assert.Empty(_kimai.Writes);
    }

    [Fact]
    public async Task UnknownRequestIds_AreNotFound()
    {
        Assert.Equal(CorrectionOutcome.NotFound, (await _service.ApproveAsync("x", "Admin")).Outcome);
        Assert.Equal(CorrectionOutcome.NotFound, (await _service.RejectAsync("x", null, "Admin")).Outcome);
        Assert.Equal(CorrectionOutcome.NotFound, (await _service.RetryAsync("x", "Admin")).Outcome);
        Assert.Equal(CorrectionOutcome.NotFound, (await _service.ResolveManuallyAsync("x", "Admin")).Outcome);
    }

    [Fact]
    public async Task ANotifierThatThrows_NeverPreventsADecision()
    {
        _notifier.Throws = true;
        var sheet = WorkSheet();

        var dto = await Submitted(Submit("setEnd", sheet.Id, end: At(10)));
        var approved = await Approved(dto.Id);

        Assert.Equal(TimeCorrectionStatus.Applied, approved.Status);
        Assert.Equal(At(10), sheet.End);
        Assert.Equal(["submitted:Pending", "decided:Applied"], _notifier.Events);
    }

    // ------------------------------------------------------------ Auth

    [Fact]
    public async Task WrongPin_IsUnauthorized_AndCountsInThePinAttemptGuard()
    {
        var wrong = new CorrectionAuthRequest("max", "0000", null);

        for (var i = 0; i < PinAttemptGuard.EmployeeThreshold; i++)
        {
            Assert.Equal(CorrectionOutcome.Unauthorized, (await _service.ListOwnAsync(wrong)).Outcome);
        }

        // Locked: even the right PIN is refused with the lock exception (429 at the endpoint).
        await Assert.ThrowsAsync<PinLockedException>(() => _service.GetSelectableShiftsAsync(MaxAuth));
        await Assert.ThrowsAsync<PinLockedException>(() => _service.SubmitAsync(Submit("addShift", begin: At(6), end: At(7))));
        Assert.NotNull(_guard.RemainingLock(PinAttemptGuard.EmployeeKey("max")));
        // Another employee is not affected.
        Assert.Equal(CorrectionOutcome.Ok, (await _service.ListOwnAsync(new CorrectionAuthRequest("anna", "5678", null))).Outcome);
    }

    [Fact]
    public async Task TheCardOfAnotherEmployee_IsUnauthorized()
    {
        // Anna's card with Max's employee id.
        var result = await _service.ListOwnAsync(new CorrectionAuthRequest("max", null, "04D5E6F7"));

        Assert.Equal(CorrectionOutcome.Unauthorized, result.Outcome);
        Assert.Equal(CorrectionOutcome.Ok, (await _service.ListOwnAsync(new CorrectionAuthRequest("max", null, "04A2B3C4"))).Outcome);
        Assert.Equal(CorrectionOutcome.Unauthorized, (await _service.WithdrawAsync(new CorrectionAuthRequest("max", null, "04D5E6F7"), "x")).Outcome);
        Assert.Equal(CorrectionOutcome.Unauthorized, (await _service.SubmitAsync(Submit("addShift", begin: At(6), end: At(7), auth: new("max", null, "04D5E6F7")))).Outcome);
    }

    // ------------------------------------------------------------ Lists

    [Fact]
    public async Task SelectableShifts_GroupNightShiftsAcrossMidnight_AndFlagOpenRequests()
    {
        // Night shift 22:00-06:00 with a pause, then a second shift 17:00-20:00 on the same day.
        var night1 = _kimai.Add(Day(3, 22), Day(4, 2));
        var nightPause = _kimai.Add(Day(4, 2), Day(4, 2, 30), PauseActivity);
        var night2 = _kimai.Add(Day(4, 2, 30), Day(4, 6));
        var evening = _kimai.Add(Day(4, 17), Day(4, 20), activity: 6, project: 5);
        _kimai.Add(Day(4, 8), Day(4, 9), user: 12); // a colleague's sheet is never listed
        await Submitted(Submit("setEnd", night2.Id, end: Day(4, 5)));

        var result = await _service.GetSelectableShiftsAsync(MaxAuth);

        var list = result.Value!;
        Assert.Equal("Europe/Berlin", list.TimeZone);
        Assert.Equal(2, list.Shifts.Count);
        // newest first
        Assert.Equal([evening.Id], list.Shifts[0].Entries.Select(entry => entry.Id));
        Assert.Equal("Kunde X", list.Shifts[0].Entries[0].Label);
        var nightShift = list.Shifts[1];
        Assert.Equal("2026-10-03T22:00", nightShift.Begin);
        Assert.Equal("2026-10-04T06:00", nightShift.End);
        Assert.Equal([night1.Id, nightPause.Id, night2.Id], nightShift.Entries.Select(entry => entry.Id));
        Assert.Equal(["work", "pause", "work"], nightShift.Entries.Select(entry => entry.Kind));
        Assert.Equal(["Arbeit", "Pause", "Arbeit"], nightShift.Entries.Select(entry => entry.Label));
        Assert.Equal([false, false, true], nightShift.Entries.Select(entry => entry.HasOpenRequest));
    }

    [Fact]
    public async Task SelectableShifts_ListOnlyTheLast31Days_AndShowARunningEntryWithoutEnd()
    {
        _kimai.Add(Now.AddDays(-40), Now.AddDays(-40).AddHours(8));
        var running = _kimai.Add(At(9), null);

        var shift = Assert.Single((await _service.GetSelectableShiftsAsync(MaxAuth)).Value!.Shifts);

        var entry = Assert.Single(shift.Entries);
        Assert.Equal(running.Id, entry.Id);
        Assert.Null(entry.End);
        Assert.Null(shift.End);
    }

    [Fact]
    public async Task ListOwn_ReturnsOnlyTheOwnRequestsOfTheLast31Days_AndHidesKimaiErrorTexts()
    {
        var sheet = WorkSheet();
        var mine = await Submitted(Submit("setEnd", sheet.Id, end: At(10)));
        sheet.End = At(9);
        await Approved(mine.Id); // Failed: sheet changed
        var annaSheet = _kimai.Add(At(1), At(2));
        var anna = new CorrectionAuthRequest("anna", "5678", null);
        _store.Add(new TimeCorrectionRequest
        {
            Id = "anna-1", EmployeeId = "anna", EmployeeName = "Anna", Kind = TimeCorrectionKind.SetEnd, Source = "clock", CreatedAt = Now,
        });
        _store.Add(new TimeCorrectionRequest
        {
            Id = "old", EmployeeId = "max", EmployeeName = "Max", Kind = TimeCorrectionKind.SetEnd, Source = "clock",
            CreatedAt = Now.AddDays(-40), Status = TimeCorrectionStatus.Applied,
        });

        var own = (await _service.ListOwnAsync(MaxAuth)).Value!;

        var entry = Assert.Single(own);
        Assert.Equal(mine.Id, entry.Id);
        Assert.Equal(TimeCorrectionStatus.Failed, entry.Status);
        Assert.DoesNotContain("geändert", entry.Error);
        Assert.Contains("Chef", entry.Error);
        Assert.Equal(["anna-1"], (await _service.ListOwnAsync(anna)).Value!.Select(item => item.Id));
        _ = annaSheet;

        // The admin list keeps the real reason.
        Assert.Contains(_service.List(openOnly: true), item => item.Id == mine.Id && item.Error == "Eintrag wurde inzwischen geändert");
    }

    [Fact]
    public async Task AdminList_OpenOnlyContainsPendingAndFailed()
    {
        var sheet = WorkSheet();
        var pending = await Submitted(Submit("setEnd", sheet.Id, end: At(10)));
        _store.Add(new TimeCorrectionRequest
        {
            Id = "failed", EmployeeId = "max", EmployeeName = "Max", Kind = TimeCorrectionKind.SetEnd, Source = "clock",
            CreatedAt = Now.AddMinutes(-5), Status = TimeCorrectionStatus.Failed,
        });
        _store.Add(new TimeCorrectionRequest
        {
            Id = "done", EmployeeId = "max", EmployeeName = "Max", Kind = TimeCorrectionKind.SetEnd, Source = "clock",
            CreatedAt = Now.AddMinutes(-10), Status = TimeCorrectionStatus.Applied,
        });

        Assert.Equal([pending.Id, "failed"], _service.List(openOnly: true).Select(item => item.Id));
        Assert.Equal(3, _service.List(openOnly: false).Count);
    }

    /// <summary>Kimai rejecting every PATCH like a time-tracking mode that forbids edits.</summary>
    private sealed class FailingPatchKimai(FakeKimai inner) : IKimaiClient
    {
        public Task UpdateTimesheetTimesAsync(RuntimeSettings s, EmployeeSettings e, int id, DateTimeOffset? begin, DateTimeOffset? end, CancellationToken ct = default)
            => throw new KimaiApiException(System.Net.HttpStatusCode.BadRequest,
                """{"errors":{"errors":["Dieses Formular sollte keine zusätzlichen Felder enthalten."],"children":{"project":{}}}}""",
                "PATCH /api/timesheets", ["end"]);

        public Task<int?> GetCurrentUserIdAsync(RuntimeSettings s, EmployeeSettings e, CancellationToken ct = default) => inner.GetCurrentUserIdAsync(s, e, ct);
        public Task<string?> GetCurrentUserTimezoneAsync(RuntimeSettings s, EmployeeSettings e, CancellationToken ct = default) => inner.GetCurrentUserTimezoneAsync(s, e, ct);
        public Task<KimaiTimesheetDetailDto?> GetTimesheetAsync(RuntimeSettings s, EmployeeSettings e, int id, CancellationToken ct = default) => inner.GetTimesheetAsync(s, e, id, ct);
        public Task<IReadOnlyCollection<KimaiTimesheetEntryDto>> GetTimesheetsAsync(RuntimeSettings s, EmployeeSettings e, DateTime begin, DateTime end, CancellationToken ct = default) => inner.GetTimesheetsAsync(s, e, begin, end, ct);
        public Task<int> CreateTimesheetAsync(RuntimeSettings s, EmployeeSettings e, KimaiTimesheetTarget t, DateTimeOffset begin, DateTimeOffset end, string? description, CancellationToken ct = default) => inner.CreateTimesheetAsync(s, e, t, begin, end, description, ct);
        public Task<ClockStatusDto> GetStatusAsync(RuntimeSettings s, EmployeeSettings e, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StartAsync(RuntimeSettings s, EmployeeSettings e, KimaiTimesheetTarget t, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StartAtAsync(RuntimeSettings s, EmployeeSettings e, KimaiTimesheetTarget t, DateTimeOffset d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StopAsync(RuntimeSettings s, EmployeeSettings e, int id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StopAtAsync(RuntimeSettings s, EmployeeSettings e, int id, DateTimeOffset d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task BackdateEndAsync(RuntimeSettings s, EmployeeSettings e, int id, DateTimeOffset d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<KimaiRecentTimesheetDto>> GetRecentStoppedTimesheetsAsync(RuntimeSettings s, EmployeeSettings e, int count, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<KimaiUserDto>> GetUsersAsync(string b, string t, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<KimaiActivityDto>> GetActivitiesAsync(string b, string t, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<KimaiProjectDto>> GetProjectsAsync(string b, string t, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
