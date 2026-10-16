using System.Net;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;
using static Stempeluhr.Api.Tests.Absence;

namespace Stempeluhr.Api.Tests;

/// <summary>
/// The absence flow against an in-memory Kimai: submit, decide, apply day by
/// day (idempotent, resumable), cancel, and the employee's own view.
/// </summary>
public sealed class AbsenceServiceTests : IDisposable
{
    private static readonly CorrectionAuthRequest MaxAuth = new("max", "1234", null);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"stempeluhr-absences-{Guid.NewGuid():N}");
    private readonly FakeKimai _kimai = new();
    private readonly RecordingAbsenceNotifier _notifier = new();
    private readonly ManualClock _clock = new(TimeCorrection.Now);
    private readonly PinAttemptGuard _guard;
    private readonly MemorySettingsStore _settings;
    private readonly AbsenceStore _store;
    private readonly AbsenceService _service;

    public AbsenceServiceTests()
    {
        _guard = new PinAttemptGuard(clock: _clock);
        _settings = new MemorySettingsStore(Settings(Max(), Anna()));
        _store = new AbsenceStore(Path.Combine(_directory, "absences.json"), clock: _clock);
        _service = new AbsenceService(_settings, new EmployeeService(), _kimai, _store, _notifier, _guard, clock: _clock);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static SubmitAbsenceRequest Submit(string kind, params DateOnly[] days)
        => new("max", "1234", null, kind, days.Select(day => day.ToString("yyyy-MM-dd")).ToArray(), null, "terminal-1");

    private async Task<AbsenceDto> Submitted(SubmitAbsenceRequest request)
    {
        var result = await _service.SubmitAsync(request);
        Assert.True(result.Outcome == CorrectionOutcome.Ok, result.Message);
        return result.Value!;
    }

    private async Task<AbsenceDto> Approved(string id)
    {
        var result = await _service.ApproveAsync(id, "Admin");
        Assert.Equal(CorrectionOutcome.Ok, result.Outcome);
        return result.Value!;
    }

    private string[] Creates => _kimai.Writes.Where(write => write.StartsWith("create", StringComparison.Ordinal)).ToArray();

    [Theory]
    [InlineData("vacation", "create act=41 proj=40 08:00-16:00 desc=Urlaub (Stempeluhr) billable=False")]
    [InlineData("sick", "create act=42 proj=40 08:00-16:00 desc=Krank (Stempeluhr) billable=False")]
    [InlineData("flexDay", "create act=43 proj=40 08:00-08:00 desc=Gleittag (Stempeluhr) billable=False")]
    public async Task EachKind_CreatesOneEntryPerDay(string kind, string expectedWrite)
    {
        var submitted = await Submitted(Submit(kind, D(10, 12), D(10, 13)));
        Assert.Equal(AbsenceStatus.Pending, submitted.Status);
        Assert.Empty(_kimai.Writes);

        var applied = await Approved(submitted.Id);

        Assert.Equal(AbsenceStatus.Applied, applied.Status);
        Assert.Equal([expectedWrite, expectedWrite], Creates);
        Assert.Equal(["2026-10-12", "2026-10-13"], applied.AppliedDays);
        Assert.Equal([TimeCorrection.Day(12, 8), TimeCorrection.Day(13, 8)], _kimai.Sheets.Select(sheet => sheet.Begin));
        var stored = _store.Find(submitted.Id)!;
        Assert.Equal(_kimai.Sheets.Select(sheet => sheet.Id), stored.AppliedDays.Values.Order());
        Assert.Equal(("Admin", "Max Mustermann", "terminal-1"), (stored.DecidedBy, stored.CreatedBy, stored.Source));
    }

    [Fact]
    public async Task AfterANightShift_TheEntryStartsWhenTheShiftEnds()
    {
        _kimai.Add(TimeCorrection.Day(11, 22), TimeCorrection.Day(12, 9, 15));
        var submitted = await Submitted(Submit("sick", D(10, 12)));

        await Approved(submitted.Id);

        Assert.Equal(["create act=42 proj=40 09:15-17:15 desc=Krank (Stempeluhr) billable=False"], Creates);
    }

    [Fact]
    public async Task ApprovingTwice_BooksOnce()
    {
        var submitted = await Submitted(Submit("vacation", D(10, 12)));

        await Approved(submitted.Id);
        var again = await Approved(submitted.Id);

        Assert.Equal(AbsenceStatus.Applied, again.Status);
        Assert.Single(Creates);
        Assert.Equal(["decided:Applied"], _notifier.Events.Where(e => e.StartsWith("decided", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task APartialFailure_AndRetry_ContinueWithoutDoubleBooking()
    {
        var submitted = await Submitted(Submit("vacation", D(10, 12), D(10, 13), D(10, 14)));
        _kimai.FailBeforeCreate = 2;

        var failed = await Approved(submitted.Id);

        Assert.Equal(AbsenceStatus.Failed, failed.Status);
        Assert.Equal(["2026-10-12"], failed.AppliedDays);
        Assert.Contains("500", failed.Error);

        var retried = await _service.RetryAsync(submitted.Id, "Admin");

        Assert.Equal(AbsenceStatus.Applied, retried.Value!.Status);
        Assert.Equal(3, _kimai.Sheets.Count);
        Assert.Equal(3, Creates.Length);
    }

    [Fact]
    public async Task ALostAnswer_IsAdoptedOnRetry_InsteadOfBookedAgain()
    {
        var submitted = await Submitted(Submit("sick", D(10, 6), D(10, 7)));
        _kimai.FailAfterCreate = 1;

        var failed = await Approved(submitted.Id);
        Assert.Equal(AbsenceStatus.Failed, failed.Status);
        Assert.Empty(failed.AppliedDays);

        var retried = (await _service.RetryAsync(submitted.Id, "Admin")).Value!;

        Assert.Equal(AbsenceStatus.Applied, retried.Status);
        Assert.Equal(2, _kimai.Sheets.Count);
        Assert.Equal(_kimai.Sheets.Select(sheet => sheet.Id).Order(), _store.Find(submitted.Id)!.AppliedDays.Values.Order());
    }

    [Fact]
    public async Task TheStartOfApplying_IsStoredBeforeTheFirstKimaiWrite()
    {
        var submitted = await Submitted(Submit("vacation", D(10, 12), D(10, 13)));
        var seen = new List<(AbsenceStatus, DateTimeOffset?)>();
        _kimai.BeforeCreate = () => seen.Add((_store.Find(submitted.Id)!.Status, _store.Find(submitted.Id)!.ApplyStartedAt));

        var applied = await Approved(submitted.Id);

        Assert.Equal([(AbsenceStatus.Pending, TimeCorrection.Now), (AbsenceStatus.Pending, TimeCorrection.Now)], seen);
        Assert.Equal(TimeCorrection.Now, applied.ApplyStartedAt);
    }

    /// <summary>
    /// The process stopped after the first day was written to Kimai: the
    /// request is still Pending, but decided. Review #140: withdrawing or
    /// rejecting it would orphan the Kimai entry and free the vacation days.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnInterruptedApply_CanNeitherBeWithdrawnNorRejected_AndApprovingContinues(bool idWasSaved)
    {
        var day1 = _kimai.Add(TimeCorrection.Day(12, 8), TimeCorrection.Day(12, 16), VacationActivity, Absence.Project);
        var interrupted = Request(AbsenceKind.Vacation, AbsenceStatus.Pending, D(10, 12), D(10, 13)) with
        {
            DecidedAt = TimeCorrection.Now,
            DecidedBy = "Chef",
            ApplyStartedAt = TimeCorrection.Now,
            // Without the id: stopped between the Kimai create and saving its id.
            AppliedDays = idWasSaved ? new Dictionary<string, int> { ["2026-10-12"] = day1.Id } : new Dictionary<string, int>(),
        };
        _store.Add(interrupted);

        Assert.Equal(CorrectionOutcome.Conflict, (await _service.WithdrawAsync(MaxAuth, interrupted.Id)).Outcome);
        Assert.Equal(AbsenceStatus.Pending, (await _service.RejectAsync(interrupted.Id, null, "Admin")).Value!.Status);
        var balance = (await _service.GetOverviewAsync(MaxAuth)).Value!.Balances[0];
        Assert.Equal(28m, balance.Remaining);

        var applied = await Approved(interrupted.Id);

        Assert.Equal((AbsenceStatus.Applied, "Chef"), (applied.Status, applied.DecidedBy));
        Assert.Equal(["create act=41 proj=40 08:00-16:00 desc=Urlaub (Stempeluhr) billable=False"], Creates);
        Assert.Equal(2, _kimai.Sheets.Count);
        Assert.Equal(day1.Id, _store.Find(interrupted.Id)!.AppliedDays["2026-10-12"]);
    }

    [Theory]
    [InlineData("0001-01-01", "liegt zu weit zurück")]
    [InlineData("9999-12-31", "liegt zu weit in der Zukunft")]
    public async Task ExtremeDates_AreRejectedBeforeKimaiIsQueried(string day, string message)
    {
        var employee = await _service.SubmitAsync(new SubmitAbsenceRequest("max", "1234", null, "vacation", [day], null, null));
        var admin = await _service.CreateForEmployeeAsync(new AdminCreateAbsenceRequest("max", "sick", [day], null), "Admin");

        Assert.Equal(CorrectionOutcome.Invalid, employee.Outcome);
        Assert.Contains(message, employee.Message);
        Assert.Equal(CorrectionOutcome.Invalid, admin.Outcome);
        Assert.Empty(_store.List());
    }

    [Fact]
    public async Task InputRules_DoNotNeedKimai()
    {
        _kimai.Unreachable = true;
        var tooMany = Enumerable.Range(0, 63).Select(i => D(11, 2).AddDays(i)).ToArray();

        var result = await _service.SubmitAsync(Submit("flexDay", tooMany));

        Assert.Equal((CorrectionOutcome.Invalid, "Ein Antrag umfasst höchstens 62 Tage."), (result.Outcome, result.Message));
    }

    [Fact]
    public async Task AKimaiRejection_IsFailedWithTheReason_AndCanBeResolvedManually()
    {
        var submitted = await Submitted(Submit("vacation", D(10, 12)));
        _kimai.FailBeforeCreate = 1;
        _kimai.FailStatus = HttpStatusCode.BadRequest;

        var failed = await Approved(submitted.Id);

        Assert.Equal(AbsenceStatus.Failed, failed.Status);
        Assert.Contains("Kimai API returned 400", failed.Error);
        // The employee only sees a neutral hint.
        var own = Assert.Single((await _service.ListOwnAsync(MaxAuth)).Value!);
        Assert.Equal("Die Abwesenheit konnte nicht in Kimai eingetragen werden. Der Chef kümmert sich darum.", own.Error);

        var resolved = await _service.ResolveManuallyAsync(submitted.Id, "Admin");
        Assert.Equal(AbsenceStatus.ResolvedManually, resolved.Value!.Status);
        Assert.Equal(CorrectionOutcome.Conflict, (await _service.RetryAsync(submitted.Id, "Admin")).Outcome);
    }

    [Fact]
    public async Task KimaiUnreachable_OnApprove_Is503_AndTheRequestStaysPending()
    {
        var submitted = await Submitted(Submit("vacation", D(10, 12)));
        _kimai.Unreachable = true;

        var result = await _service.ApproveAsync(submitted.Id, "Admin");

        Assert.Equal(CorrectionOutcome.Unavailable, result.Outcome);
        Assert.Equal((AbsenceStatus.Pending, null), (_store.Find(submitted.Id)!.Status, _store.Find(submitted.Id)!.DecidedBy));

        _kimai.Unreachable = false;
        Assert.Equal(AbsenceStatus.Applied, (await Approved(submitted.Id)).Status);
    }

    [Fact]
    public async Task KimaiUnreachable_OnSubmit_Is503_AndStoresNothing()
    {
        _kimai.Unreachable = true;

        var result = await _service.SubmitAsync(Submit("sick", D(10, 6)));

        Assert.Equal(CorrectionOutcome.Unavailable, result.Outcome);
        Assert.Empty(_store.List());
    }

    [Fact]
    public async Task ApplyingRechecksTheRules()
    {
        var submitted = await Submitted(Submit("vacation", D(10, 12)));
        // Meanwhile the employee worked on that day.
        _kimai.Add(TimeCorrection.Day(12, 7), TimeCorrection.Day(12, 12));

        var failed = await Approved(submitted.Id);

        Assert.Equal((AbsenceStatus.Failed, "Am 12.10.2026 wurde schon gearbeitet."), (failed.Status, failed.Error));
        Assert.Empty(Creates);
    }

    [Fact]
    public async Task ADayWithoutRoom_FailsWithAMessage()
    {
        var submitted = await Submitted(Submit("vacation", D(10, 12)));
        _kimai.Add(TimeCorrection.Day(11, 20), TimeCorrection.Day(12, 17));

        var failed = await Approved(submitted.Id);

        Assert.Equal(AbsenceStatus.Failed, failed.Status);
        Assert.Contains("kein Platz", failed.Error);
    }

    [Fact]
    public async Task TheAdminEntry_IsAppliedAtOnce_WithAWarningOverTheRemainder()
    {
        _store.Add(Request(AbsenceKind.Vacation, AbsenceStatus.Applied, [.. Enumerable.Range(0, 29).Select(i => D(1, 2).AddDays(i))]));

        var result = await _service.CreateForEmployeeAsync(
            new AdminCreateAbsenceRequest("max", "vacation", ["2026-10-12", "2026-10-13"], "Telefonisch"), "Admin");

        var created = result.Value!;
        Assert.Equal((AbsenceStatus.Applied, "Admin", "admin", "Admin"), (created.Status, created.CreatedBy, created.Source, created.DecidedBy));
        Assert.Equal("Urlaub übersteigt den Rest 2026 um 1 Tage.", created.Warning);
        Assert.Equal(2, Creates.Length);
        // Nur die Info für den Telegram-Chat (#137): nichts war zu entscheiden.
        await _service.WhenNotificationsCompleteAsync();
        Assert.Equal(["decided:Applied"], _notifier.Events);
    }

    [Fact]
    public async Task TheEmployee_CannotGoOverTheRemainder()
    {
        _store.Add(Request(AbsenceKind.Vacation, AbsenceStatus.Pending, [.. Enumerable.Range(0, 30).Select(i => D(1, 2).AddDays(i))]));

        var result = await _service.SubmitAsync(Submit("vacation", D(10, 12)));

        Assert.Equal((CorrectionOutcome.Invalid, "Urlaub 2026: 1 Tage gewählt, der Rest beträgt nur 0 Tage."), (result.Outcome, result.Message));
    }

    [Fact]
    public async Task TheAdmin_MayEnterSickDaysFurtherBack()
    {
        var employee = await _service.SubmitAsync(Submit("sick", Today.AddDays(-60)));
        var admin = await _service.CreateForEmployeeAsync(
            new AdminCreateAbsenceRequest("max", "sick", [Today.AddDays(-60).ToString("yyyy-MM-dd")], null), "Admin");

        Assert.Equal(CorrectionOutcome.Invalid, employee.Outcome);
        Assert.Equal(AbsenceStatus.Applied, admin.Value!.Status);
    }

    [Fact]
    public async Task ADay_CannotBeRequestedTwice()
    {
        await Submitted(Submit("vacation", D(10, 12), D(10, 13)));

        var second = await _service.SubmitAsync(Submit("flexDay", D(10, 13)));

        Assert.Equal((CorrectionOutcome.Invalid, "Für den 13.10.2026 gibt es schon einen Antrag (Urlaub)."), (second.Outcome, second.Message));
    }

    [Fact]
    public async Task ConcurrentSubmits_DoNotBothTakeTheSameDay()
    {
        var results = await Task.WhenAll(
            _service.SubmitAsync(Submit("vacation", D(10, 12))),
            _service.SubmitAsync(Submit("sick", D(10, 12))));

        Assert.Single(results, result => result.Outcome == CorrectionOutcome.Ok);
        Assert.Single(_store.List());
    }

    [Theory]
    [InlineData("holiday", null, "Unbekannte Art der Abwesenheit.")]
    [InlineData("7", null, "Unbekannte Art der Abwesenheit.")]
    [InlineData("sick", "12.10.2026", "Ungültiges Datum.")]
    public async Task InvalidInput_IsRejected(string kind, string? day, string message)
    {
        var result = await _service.SubmitAsync(new SubmitAbsenceRequest("max", "1234", null, kind, [day ?? "2026-10-12"], null, null));

        Assert.Equal((CorrectionOutcome.Invalid, message), (result.Outcome, result.Message));
    }

    [Fact]
    public async Task DaysAreSortedAndDistinct_AndTheSourceDefaultsToClock()
    {
        var submitted = await Submitted(new SubmitAbsenceRequest("max", "1234", null, "flexDay", ["2026-10-14", "2026-10-12", "2026-10-14"], "  ", "admin"));

        Assert.Equal(["2026-10-12", "2026-10-14"], submitted.Days);
        Assert.Equal(("clock", null), (submitted.Source, submitted.Comment));
    }

    [Fact]
    public async Task Rejecting_BooksNothing_AndALaterApproveIsANoOp()
    {
        var submitted = await Submitted(Submit("vacation", D(10, 12)));

        var rejected = (await _service.RejectAsync(submitted.Id, "  Bitte später  ", "Admin")).Value!;
        var approved = await Approved(submitted.Id);

        Assert.Equal((AbsenceStatus.Rejected, "Bitte später"), (rejected.Status, rejected.DecisionNote));
        Assert.Equal(AbsenceStatus.Rejected, approved.Status);
        Assert.Empty(_kimai.Writes);
    }

    [Fact]
    public async Task Cancelling_DeletesTheEntries_AlsoWhenOneIsAlreadyGone()
    {
        var submitted = await Submitted(Submit("vacation", D(10, 12), D(10, 13)));
        await Approved(submitted.Id);
        // Deleted by hand in Kimai: the delete answers 404 and counts as done.
        _kimai.Sheets.RemoveAt(0);

        var cancelled = (await _service.CancelAsync(submitted.Id, "Admin")).Value!;

        Assert.Equal((AbsenceStatus.Cancelled, "Admin"), (cancelled.Status, cancelled.CancelledBy));
        Assert.Empty(_kimai.Sheets);
        Assert.Empty(cancelled.AppliedDays);
        Assert.Equal("Admin", cancelled.DecidedBy);
        // A second cancel is a no-op, the days are free again.
        Assert.Equal(AbsenceStatus.Cancelled, (await _service.CancelAsync(submitted.Id, "Admin")).Value!.Status);
        await Submitted(Submit("sick", D(10, 12)));
    }

    [Fact]
    public async Task ACancelThatFails_StaysApplied_AndContinuesLater()
    {
        var submitted = await Submitted(Submit("vacation", D(10, 12), D(10, 13)));
        await Approved(submitted.Id);
        _kimai.Unreachable = true;

        var partial = (await _service.CancelAsync(submitted.Id, "Admin")).Value!;

        Assert.Equal(AbsenceStatus.Applied, partial.Status);
        Assert.Equal("Storno unvollständig: Kimai ist nicht erreichbar.", partial.Error);
        Assert.Equal(2, partial.AppliedDays.Count);

        _kimai.Unreachable = false;
        var cancelled = (await _service.CancelAsync(submitted.Id, "Admin")).Value!;
        Assert.Equal((AbsenceStatus.Cancelled, null), (cancelled.Status, cancelled.Error));
        Assert.Empty(_kimai.Sheets);
    }

    [Fact]
    public async Task OnlyAppliedAbsences_CanBeCancelled()
    {
        var submitted = await Submitted(Submit("vacation", D(10, 12)));

        Assert.Equal(CorrectionOutcome.Conflict, (await _service.CancelAsync(submitted.Id, "Admin")).Outcome);
        Assert.Equal(CorrectionOutcome.NotFound, (await _service.CancelAsync("unknown", "Admin")).Outcome);
    }

    [Fact]
    public async Task Withdrawing_OnlyOwnPendingRequests()
    {
        var submitted = await Submitted(Submit("vacation", D(10, 12)));

        Assert.Equal(CorrectionOutcome.NotFound, (await _service.WithdrawAsync(new("anna", "5678", null), submitted.Id)).Outcome);
        var withdrawn = (await _service.WithdrawAsync(MaxAuth, submitted.Id)).Value!;
        Assert.Equal((AbsenceStatus.Withdrawn, "Max Mustermann"), (withdrawn.Status, withdrawn.DecidedBy));
        Assert.Equal(CorrectionOutcome.Conflict, (await _service.WithdrawAsync(MaxAuth, submitted.Id)).Outcome);

        var applied = await Submitted(Submit("sick", D(10, 6)));
        await Approved(applied.Id);
        Assert.Equal(CorrectionOutcome.Conflict, (await _service.WithdrawAsync(MaxAuth, applied.Id)).Outcome);
    }

    [Fact]
    public async Task WrongPin_IsUnauthorized_AndCountsInThePinAttemptGuard()
    {
        var wrong = new CorrectionAuthRequest("max", "0000", null);

        for (var i = 0; i < PinAttemptGuard.EmployeeThreshold; i++)
        {
            Assert.Equal(CorrectionOutcome.Unauthorized, (await _service.GetOverviewAsync(wrong)).Outcome);
        }

        await Assert.ThrowsAsync<PinLockedException>(() => _service.SubmitAsync(Submit("sick", D(10, 6))));
        Assert.NotNull(_guard.RemainingLock(PinAttemptGuard.EmployeeKey("max")));
        Assert.Equal(CorrectionOutcome.Ok, (await _service.ListOwnAsync(new CorrectionAuthRequest("anna", "5678", null))).Outcome);
    }

    [Fact]
    public async Task TheCardOfAnotherEmployee_IsUnauthorized()
    {
        var result = await _service.SubmitAsync(new SubmitAbsenceRequest("max", null, "04D5E6F7", "sick", ["2026-10-06"], null, null));

        Assert.Equal(CorrectionOutcome.Unauthorized, result.Outcome);
        Assert.Equal(CorrectionOutcome.Ok, (await _service.ListOwnAsync(new CorrectionAuthRequest("max", null, "04A2B3C4"))).Outcome);
    }

    [Fact]
    public async Task TheOverview_ShowsBalancesWindowsAndBookedDays()
    {
        var submitted = await Submitted(Submit("vacation", D(10, 12), D(10, 13)));
        await Approved(submitted.Id);
        await Submitted(Submit("flexDay", D(12, 30)));

        var overview = (await _service.GetOverviewAsync(MaxAuth)).Value!;

        Assert.Equal("2026-10-05", overview.Today);
        // October 2026 + 18 months reaches 2028.
        Assert.Equal([2026, 2027, 2028], overview.Balances.Select(balance => balance.Year));
        Assert.Equal((30m, 2m, 0m, 28m), (overview.Balances[0].Entitlement, overview.Balances[0].Taken, overview.Balances[0].Planned, overview.Balances[0].Remaining));
        var sick = overview.Kinds.Single(kind => kind.Kind == AbsenceKind.Sick);
        Assert.Equal(new AbsenceKindDto(AbsenceKind.Sick, "2026-09-04", "2026-10-19", null), sick);
        Assert.Equal(
            [new AbsenceDayDto("2026-10-12", AbsenceKind.Vacation, AbsenceStatus.Applied),
             new AbsenceDayDto("2026-10-13", AbsenceKind.Vacation, AbsenceStatus.Applied),
             new AbsenceDayDto("2026-12-30", AbsenceKind.FlexDay, AbsenceStatus.Pending)],
            overview.Days);
    }

    [Fact]
    public async Task TheOverview_WorksWithoutKimai_AndNamesMissingSettings()
    {
        _kimai.Unreachable = true;
        var service = new AbsenceService(
            new MemorySettingsStore(Settings(Max(absenceDayHours: null))), new EmployeeService(), _kimai, _store, _notifier, clock: _clock);

        var overview = (await service.GetOverviewAsync(MaxAuth)).Value!;

        Assert.Contains("Stunden pro Abwesenheitstag", overview.Kinds.Single(kind => kind.Kind == AbsenceKind.Vacation).Missing);
        Assert.Null(overview.Kinds.Single(kind => kind.Kind == AbsenceKind.FlexDay).Missing);
        Assert.Equal(CorrectionOutcome.Invalid, (await service.SubmitAsync(Submit("sick", D(10, 6)))).Outcome);
    }

    [Fact]
    public async Task OwnList_ShowsOpenAndRecentRequestsOnly()
    {
        var old = Request(AbsenceKind.Sick, AbsenceStatus.Applied, D(9, 1, 2025)) with { CreatedAt = TimeCorrection.Now.AddDays(-400) };
        var plannedLongAgo = Request(AbsenceKind.Vacation, AbsenceStatus.Applied, D(11, 2)) with { CreatedAt = TimeCorrection.Now.AddDays(-400) };
        var foreign = Request(AbsenceKind.Sick, AbsenceStatus.Pending, D(10, 6)) with { EmployeeId = "anna" };
        _store.Add(old);
        _store.Add(plannedLongAgo);
        _store.Add(foreign);

        var own = (await _service.ListOwnAsync(MaxAuth)).Value!;

        Assert.Equal([plannedLongAgo.Id], own.Select(request => request.Id));
    }

    [Fact]
    public async Task AdminList_FiltersOpenAndYear()
    {
        var open = await Submitted(Submit("vacation", D(10, 12)));
        var applied = await Submitted(Submit("vacation", D(1, 4, 2027)));
        await Approved(applied.Id);

        Assert.Equal([open.Id], _service.List(openOnly: true).Select(request => request.Id));
        Assert.Equal(2, _service.List(openOnly: false).Count);
        Assert.Equal([applied.Id], _service.List(openOnly: false, year: 2027).Select(request => request.Id));
    }

    [Fact]
    public async Task UpdatingTheVacationAccount_SavesOnlyThatYear_AndKeepsEverythingElse()
    {
        var result = await _service.UpdateVacationYearAsync("max", 2026, new UpdateVacationYearRequest(4m, -1.5m, " Vorjahr "));

        var balance = result.Value!;
        Assert.Equal((4m, -1.5m, 32.5m, "Vorjahr"), (balance.Balance.CarryOver, balance.Balance.Correction, balance.Balance.Remaining, balance.Note));
        var max = _settings.Load().Employees.Single(employee => employee.Id == "max");
        Assert.Equal([new EmployeeVacationYear(2026, 4m, -1.5m, "Vorjahr")], max.VacationYears);
        Assert.Equal(("1234", "max-token", "Kunde X", 30m), (max.Pin, max.ApiToken, max.Tasks.Single().Label, max.VacationDaysPerYear));
        Assert.Equal(Absence.Project, _settings.Load().AbsenceProjectId);

        // All zero without a note removes the year.
        await _service.UpdateVacationYearAsync("max", 2026, new UpdateVacationYearRequest(0m, 0m, null));
        Assert.Empty(_settings.Load().Employees.Single(employee => employee.Id == "max").VacationYears);
    }

    [Theory]
    [InlineData(1999, 0, 0, "Ungültiges Jahr.")]
    [InlineData(2026, 400, 0, "Übertrag und Korrektur liegen zwischen -366 und 366 Tagen.")]
    [InlineData(2026, 0, -400, "Übertrag und Korrektur liegen zwischen -366 und 366 Tagen.")]
    public async Task UpdatingTheVacationAccount_ValidatesTheInput(int year, int carryOver, int correction, string message)
    {
        var result = await _service.UpdateVacationYearAsync("max", year, new UpdateVacationYearRequest(carryOver, correction, null));

        Assert.Equal((CorrectionOutcome.Invalid, message), (result.Outcome, result.Message));
        Assert.Equal(0, _settings.Saves);
        Assert.Equal(CorrectionOutcome.NotFound, (await _service.UpdateVacationYearAsync("nobody", 2026, new(0m, 0m, null))).Outcome);
    }

    [Fact]
    public async Task Balances_ListEveryEmployee()
    {
        var balances = _service.GetBalances(2026);

        Assert.Equal([("max", "1001", 30m), ("anna", null, 25m)],
            balances.Select(balance => (balance.EmployeeId, balance.PersonnelNumber, balance.Balance.Entitlement)));

        var entered = new AbsenceService(
            new MemorySettingsStore(Settings(Max(entryDate: D(3, 15)))), new EmployeeService(), _kimai, _store, _notifier, clock: _clock);
        var max = Assert.Single(entered.GetBalances(2026));
        Assert.Equal((9, D(3, 15), (DateOnly?)null), (max.FullMonths, max.EntryDate, max.ExitDate));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task NotifierFailures_NeverStopADecision()
    {
        _notifier.Throws = true;
        var submitted = await Submitted(Submit("vacation", D(10, 12)));

        var applied = await Approved(submitted.Id);
        await _service.WhenNotificationsCompleteAsync();

        Assert.Equal(AbsenceStatus.Applied, applied.Status);
        // The submit notification runs in the background: order is not guaranteed.
        Assert.Equal(["decided:Applied", "submitted:Pending"], _notifier.Events.Order(StringComparer.Ordinal));
    }

    private sealed class RecordingAbsenceNotifier : IAbsenceNotifier
    {
        private readonly object _gate = new();
        private readonly List<string> _events = [];

        public bool Throws { get; set; }

        public List<string> Events
        {
            get { lock (_gate) return [.. _events]; }
        }

        public Task OnSubmitted(AbsenceRequest request, CancellationToken cancellationToken = default) => Record($"submitted:{request.Status}");

        public Task OnDecided(AbsenceRequest request, CancellationToken cancellationToken = default) => Record($"decided:{request.Status}");

        private Task Record(string entry)
        {
            lock (_gate) _events.Add(entry);
            return Throws ? throw new InvalidOperationException("notifier down") : Task.CompletedTask;
        }
    }
}
