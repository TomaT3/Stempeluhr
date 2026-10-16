using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;
using static Stempeluhr.Api.Tests.Absence;

namespace Stempeluhr.Api.Tests;

/// <summary>
/// Abwesenheitsanträge über Telegram: Nachricht mit Knöpfen, Bestätigung,
/// Entscheidung per Callback, Info bei Admin-Erfassung und Aktualisierung nach
/// Entscheidungen anderswo. Service, Store und Kimai sind echt (Fake-Kimai),
/// nur die Bot API ist ein HttpMessageHandler-Fake. Die Uhrzeit der
/// Entscheidung hängt an der Zeitzone des Servers und wird nicht geprüft.
/// </summary>
public sealed class TelegramAbsenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"stempeluhr-telegram-absences-{Guid.NewGuid():N}");
    private readonly FakeTelegram _telegram = new();
    private readonly FakeKimai _kimai = new();
    private readonly MutableSettingsStore _settings;
    private readonly AbsenceStore _store;
    private readonly TimeCorrectionStore _correctionStore;
    private readonly AbsenceService _service;
    private readonly TelegramAbsenceNotifier _notifier;
    private readonly TelegramUpdatePoller _poller;
    private readonly CapturingLogger<TelegramAbsenceNotifier> _notifierLog = new();

    public TelegramAbsenceTests()
    {
        var clock = new ManualClock(TimeCorrection.Now);
        _settings = new MutableSettingsStore(Configured());
        _store = new AbsenceStore(Path.Combine(_directory, "absences.json"), clock: clock);
        var api = new TelegramBotApi(new FakeTelegramClientFactory(_telegram));
        _notifier = new TelegramAbsenceNotifier(_settings, api, _store, _notifierLog);
        _service = new AbsenceService(
            _settings, new EmployeeService(), _kimai, _store, _notifier, new PinAttemptGuard(clock: clock), clock: clock);

        // Die Korrekturseite des Pollers ist hier nicht Thema: sie bekommt Attrappen.
        _correctionStore = new TimeCorrectionStore(Path.Combine(_directory, "time-corrections.json"));
        var correctionNotifier = new TelegramTimeCorrectionNotifier(_settings, api, _correctionStore);
        var corrections = new TimeCorrectionService(
            _settings, new EmployeeService(), _kimai, _correctionStore, correctionNotifier,
            new PinAttemptGuard(clock: clock), clock: clock);
        _poller = new TelegramUpdatePoller(
            _settings, api, corrections, _correctionStore, correctionNotifier, new CapturingLogger<TelegramUpdatePoller>(),
            delay: null, absences: _service, absenceStore: _store, absenceMessages: _notifier);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static RuntimeSettings Configured(string? correctionChat = FakeTelegram.CorrectionChat, params long[] approvers) => new()
    {
        BaseUrl = "http://kimai.test",
        DefaultProjectId = TimeCorrection.WorkProject,
        DefaultActivityId = TimeCorrection.WorkActivity,
        PauseActivityId = TimeCorrection.PauseActivity,
        AbsenceProjectId = Project,
        VacationActivityId = VacationActivity,
        SickActivityId = SickActivity,
        FlexDayActivityId = FlexActivity,
        TelegramBotToken = FakeTelegram.Token,
        TelegramChatId = "-555",
        TelegramCorrectionChatId = correctionChat,
        TelegramApproverUserIds = [.. approvers],
        Employees = [Max()],
    };

    private async Task<AbsenceDto> SubmitAsync(string kind, string? comment, params DateOnly[] days)
    {
        var result = await _service.SubmitAsync(new SubmitAbsenceRequest(
            "max", "1234", null, kind, days.Select(day => day.ToString("yyyy-MM-dd")).ToArray(), comment, "terminal-1"));
        Assert.True(result.Outcome == CorrectionOutcome.Ok, result.Message);
        // Die Nachricht geht im Hintergrund raus: warten, damit die Tests deterministisch bleiben.
        await _service.WhenNotificationsCompleteAsync();
        return result.Value!;
    }

    private Task<AbsenceDto> SubmitVacationAsync() => SubmitAsync("vacation", "Familienfeier", D(10, 12), D(10, 13), D(10, 14));

    private async Task TapAsync(params object[] updates)
    {
        _telegram.Updates.Enqueue(FakeTelegram.UpdatesOf(updates));
        await _poller.RunCycleAsync(CancellationToken.None);
    }

    private static string Data(TelegramAbsenceAction action, string id) => TelegramAbsenceCallback.Format(action, id);

    private string AnswerText(int index = 0) => _telegram.CallsTo("answerCallbackQuery")[index].Body.GetProperty("text").GetString()!;

    // ------------------------------------------------------------ Nachricht beim Absenden

    [Fact]
    public async Task Submit_SendsTheRequestWithButtonsAndKeepsTheMessageIds()
    {
        var dto = await SubmitVacationAsync();

        var call = Assert.Single(_telegram.Calls);
        Assert.Equal("sendMessage", call.Method);
        Assert.Equal(FakeTelegram.CorrectionChat, call.Body.GetProperty("chat_id").GetString());
        Assert.Equal(
            "🏖 Abwesenheitsantrag · Max Mustermann\nUrlaub: Mo 12.10. – Mi 14.10. (3 Tage)\nResturlaub 2026: 30 → 27 Tage\nKommentar: Familienfeier",
            call.Text);
        Assert.Equal(["✅ Genehmigen", "❌ Ablehnen"], call.ButtonTexts);
        Assert.Equal([Data(TelegramAbsenceAction.AskApprove, dto.Id), Data(TelegramAbsenceAction.AskReject, dto.Id)], call.ButtonData);
        var stored = _store.Find(dto.Id)!;
        Assert.Equal(FakeTelegram.CorrectionChatNumber, stored.TelegramChatId);
        Assert.Equal(FakeTelegram.FirstMessageId, stored.TelegramMessageId);
    }

    [Fact]
    public async Task Submit_SickDays_HasNoVacationRest()
    {
        await SubmitAsync("sick", null, D(10, 12));

        Assert.Equal("🏖 Abwesenheitsantrag · Max Mustermann\nKrank: Mo 12.10. (1 Tag)", Assert.Single(_telegram.Calls).Text);
    }

    [Fact]
    public async Task Submit_RestCountsAlreadyBookedVacation()
    {
        var approved = await SubmitAsync("vacation", null, D(10, 19), D(10, 20));
        await _service.ApproveAsync(approved.Id, "Admin");

        await SubmitAsync("vacation", null, D(11, 2));

        Assert.Contains("Resturlaub 2026: 28 → 27 Tage", _telegram.CallsTo("sendMessage")[1].Text);
    }

    [Fact]
    public async Task Submit_WithoutCorrectionChat_OrToken_SendsNothing()
    {
        _settings.Settings = Configured(correctionChat: null);
        var first = await SubmitVacationAsync();
        await _service.ApproveAsync(first.Id, "Admin");
        Assert.Empty(_telegram.Calls);

        _settings.Settings = new RuntimeSettings
        {
            BaseUrl = "http://kimai.test", TelegramCorrectionChatId = FakeTelegram.CorrectionChat, Employees = [Max()],
            AbsenceProjectId = Project, VacationActivityId = VacationActivity, SickActivityId = SickActivity, FlexDayActivityId = FlexActivity,
        };
        await SubmitAsync("sick", null, D(10, 8));
        Assert.Empty(_telegram.Calls);
    }

    [Fact]
    public async Task Decision_TelegramDown_DoesNotChangeTheBooking()
    {
        var dto = await SubmitVacationAsync();
        _telegram.Responder = _ => throw new HttpRequestException("connection refused");

        var result = await _service.ApproveAsync(dto.Id, "Admin");

        Assert.Equal(AbsenceStatus.Applied, result.Value!.Status);
        Assert.Equal(3, _kimai.Sheets.Count);
    }

    [Fact]
    public async Task Submit_TelegramRejects_StillStoresTheRequestAndLogsOnly()
    {
        _telegram.Responder = _ => FakeTelegram.Json(new { ok = false, description = "boom" }, System.Net.HttpStatusCode.InternalServerError);

        var dto = await SubmitVacationAsync();

        Assert.Equal(AbsenceStatus.Pending, _store.Find(dto.Id)!.Status);
        Assert.Null(_store.Find(dto.Id)!.TelegramMessageId);
        Assert.Contains(_notifierLog.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    // ------------------------------------------------------------ Entscheidung per Knopf

    [Fact]
    public async Task FirstTap_AsksForConfirmation_AndBackRestoresTheButtons()
    {
        var dto = await SubmitVacationAsync();

        await TapAsync(FakeTelegram.Callback(1, Data(TelegramAbsenceAction.AskApprove, dto.Id)));

        var markup = Assert.Single(_telegram.CallsTo("editMessageReplyMarkup"));
        Assert.Equal(["Ja, genehmigen", "Zurück"], markup.ButtonTexts);
        Assert.Equal([Data(TelegramAbsenceAction.Approve, dto.Id), Data(TelegramAbsenceAction.Back, dto.Id)], markup.ButtonData);
        Assert.Equal("Wirklich genehmigen?", AnswerText());
        Assert.Equal(AbsenceStatus.Pending, _store.Find(dto.Id)!.Status);
        Assert.Empty(_kimai.Sheets);

        await TapAsync(FakeTelegram.Callback(2, Data(TelegramAbsenceAction.Back, dto.Id)));

        Assert.Equal(["✅ Genehmigen", "❌ Ablehnen"], _telegram.CallsTo("editMessageReplyMarkup")[1].ButtonTexts);
    }

    [Fact]
    public async Task ApproveFlow_BooksInKimaiAndShowsTheResultWithTheTelegramName()
    {
        var dto = await SubmitVacationAsync();

        await TapAsync(
            FakeTelegram.Callback(1, Data(TelegramAbsenceAction.AskApprove, dto.Id)),
            FakeTelegram.Callback(2, Data(TelegramAbsenceAction.Approve, dto.Id), firstName: "Chefin"));

        var stored = _store.Find(dto.Id)!;
        Assert.Equal(AbsenceStatus.Applied, stored.Status);
        Assert.Equal("Chefin", stored.DecidedBy);
        Assert.Equal(3, _kimai.Sheets.Count);
        var edit = Assert.Single(_telegram.CallsTo("editMessageText"));
        Assert.True(edit.KeyboardIsEmpty);
        Assert.StartsWith("🏖 Abwesenheitsantrag · Max Mustermann\nUrlaub: Mo 12.10. – Mi 14.10. (3 Tage)", edit.Text);
        Assert.Contains("✅ Genehmigt von Chefin · ", edit.Text);
        Assert.EndsWith("– in Kimai eingetragen", edit.Text);
        Assert.Equal("Genehmigt", AnswerText(1));
    }

    [Fact]
    public async Task RejectFlow_RejectsWithoutBookingAndShowsTheResult()
    {
        var dto = await SubmitVacationAsync();

        await TapAsync(
            FakeTelegram.Callback(1, Data(TelegramAbsenceAction.AskReject, dto.Id)),
            FakeTelegram.Callback(2, Data(TelegramAbsenceAction.Reject, dto.Id), firstName: "Chefin"));

        var stored = _store.Find(dto.Id)!;
        Assert.Equal(AbsenceStatus.Rejected, stored.Status);
        Assert.Equal("Chefin", stored.DecidedBy);
        Assert.Empty(_kimai.Sheets);
        var edit = Assert.Single(_telegram.CallsTo("editMessageText"));
        Assert.Contains("❌ Abgelehnt von Chefin", edit.Text);
        Assert.Equal("Abgelehnt", AnswerText(1));
    }

    [Fact]
    public async Task DoubleTap_AndAdminAfterwards_BookOnce()
    {
        var dto = await SubmitVacationAsync();

        await TapAsync(
            FakeTelegram.Callback(1, Data(TelegramAbsenceAction.Approve, dto.Id)),
            FakeTelegram.Callback(2, Data(TelegramAbsenceAction.Approve, dto.Id)));
        await _service.ApproveAsync(dto.Id, "Admin");

        Assert.Equal(3, _kimai.Sheets.Count);
        Assert.StartsWith("Bereits entschieden: Genehmigt von", AnswerText(1));
    }

    [Fact]
    public async Task AdminDecision_ReplacesTheMessage_AndALaterTapOnlyReportsIt()
    {
        var dto = await SubmitVacationAsync();

        await _service.RejectAsync(dto.Id, "Zu viele Urlauber", "Admin");

        var edit = Assert.Single(_telegram.CallsTo("editMessageText"));
        Assert.True(edit.KeyboardIsEmpty);
        Assert.EndsWith("\nGrund: Zu viele Urlauber", edit.Text);
        Assert.Contains("❌ Abgelehnt von Admin", edit.Text);

        await TapAsync(FakeTelegram.Callback(1, Data(TelegramAbsenceAction.Approve, dto.Id)));

        Assert.Equal("Bereits entschieden: Abgelehnt von Admin", AnswerText());
        Assert.Empty(_kimai.Sheets);
    }

    [Fact]
    public async Task Withdrawal_ReplacesTheMessageWithWithdrawn()
    {
        var dto = await SubmitVacationAsync();

        await _service.WithdrawAsync(new CorrectionAuthRequest("max", "1234", null), dto.Id);
        await _service.WhenNotificationsCompleteAsync();

        var edit = Assert.Single(_telegram.CallsTo("editMessageText"));
        Assert.Contains("↩️ Zurückgezogen", edit.Text);
        Assert.True(edit.KeyboardIsEmpty);
    }

    [Fact]
    public async Task InterruptedApply_OffersOnlyResuming_AndTheConfirmationContinues()
    {
        var dto = await SubmitVacationAsync();
        // Prozessabbruch mitten im Anwenden: Pending mit ApplyStartedAt.
        _store.Update(_store.Find(dto.Id)! with { ApplyStartedAt = TimeCorrection.Now, DecidedBy = "Admin" });

        await TapAsync(FakeTelegram.Callback(1, Data(TelegramAbsenceAction.AskReject, dto.Id)));

        Assert.Equal("Genehmigung bereits begonnen – nur noch Fortsetzen möglich", AnswerText());
        Assert.Equal(["▶️ Genehmigung fortsetzen"], Assert.Single(_telegram.CallsTo("editMessageReplyMarkup")).ButtonTexts);
        Assert.Equal(AbsenceStatus.Pending, _store.Find(dto.Id)!.Status);

        await TapAsync(FakeTelegram.Callback(2, Data(TelegramAbsenceAction.Reject, dto.Id)));

        Assert.Equal(AbsenceStatus.Pending, _store.Find(dto.Id)!.Status);

        await TapAsync(FakeTelegram.Callback(3, Data(TelegramAbsenceAction.AskApprove, dto.Id)));

        var confirm = _telegram.CallsTo("editMessageReplyMarkup")[^1];
        Assert.Equal(["Ja, genehmigen", "Zurück"], confirm.ButtonTexts);
        Assert.Equal("Wirklich genehmigen?", AnswerText(2));

        await TapAsync(FakeTelegram.Callback(4, Data(TelegramAbsenceAction.Back, dto.Id)));

        Assert.Equal(["▶️ Genehmigung fortsetzen"], _telegram.CallsTo("editMessageReplyMarkup")[^1].ButtonTexts);

        await TapAsync(FakeTelegram.Callback(5, Data(TelegramAbsenceAction.Approve, dto.Id)));

        Assert.Equal(AbsenceStatus.Applied, _store.Find(dto.Id)!.Status);
        Assert.Equal(3, _kimai.Sheets.Count);
    }

    [Fact]
    public async Task ResultMessage_AfterCancelOrReject_ShowsTheReleasedRestWithoutDeduction()
    {
        var cancelled = await SubmitVacationAsync();
        await _service.ApproveAsync(cancelled.Id, "Admin");
        Assert.Contains("Resturlaub 2026: 30 → 27 Tage", _telegram.CallsTo("editMessageText")[^1].Text);

        await _service.CancelAsync(cancelled.Id, "Admin");

        var edit = _telegram.CallsTo("editMessageText")[^1];
        Assert.Contains("🚫 Storniert von Admin", edit.Text);
        Assert.Contains("Resturlaub 2026: 30 Tage", edit.Text);
        Assert.DoesNotContain("→", edit.Text);

        var rejected = await SubmitAsync("vacation", null, D(11, 2));
        await _service.RejectAsync(rejected.Id, null, "Admin");

        edit = _telegram.CallsTo("editMessageText")[^1];
        Assert.Contains("❌ Abgelehnt von Admin", edit.Text);
        Assert.Contains("Resturlaub 2026: 30 Tage", edit.Text);
        Assert.DoesNotContain("→", edit.Text);
    }

    // ------------------------------------------------------------ Berechtigung

    [Fact]
    public async Task TapInAnotherChat_DoesNothing()
    {
        var dto = await SubmitVacationAsync();

        await TapAsync(FakeTelegram.Callback(1, Data(TelegramAbsenceAction.Approve, dto.Id), chatId: -999));

        Assert.Equal(AbsenceStatus.Pending, _store.Find(dto.Id)!.Status);
        Assert.Empty(_kimai.Sheets);
        Assert.Equal("Keine Berechtigung", AnswerText());
        Assert.Empty(_telegram.CallsTo("editMessageText"));
    }

    [Fact]
    public async Task TapByAnUnauthorizedUser_DoesNothing()
    {
        _settings.Settings = Configured(approvers: 7);
        var dto = await SubmitVacationAsync();

        await TapAsync(FakeTelegram.Callback(1, Data(TelegramAbsenceAction.Approve, dto.Id), userId: 99));

        Assert.Equal(AbsenceStatus.Pending, _store.Find(dto.Id)!.Status);
        Assert.Empty(_kimai.Sheets);
        Assert.Equal("Keine Berechtigung", AnswerText());

        await TapAsync(FakeTelegram.Callback(2, Data(TelegramAbsenceAction.Approve, dto.Id), userId: 7));

        Assert.Equal(AbsenceStatus.Applied, _store.Find(dto.Id)!.Status);
    }

    [Fact]
    public async Task TapOnAnUnknownRequest_IsAnsweredAndHarmless()
    {
        await TapAsync(FakeTelegram.Callback(1, Data(TelegramAbsenceAction.Approve, "gibtsnicht")));

        Assert.Equal("Antrag nicht gefunden", AnswerText());
        Assert.Empty(_kimai.Sheets);
    }

    // ------------------------------------------------------------ Admin-Erfassung

    [Fact]
    public async Task AdminCreated_SendsAnInfoWithoutButtons_AndCancelUpdatesIt()
    {
        var created = await _service.CreateForEmployeeAsync(
            new AdminCreateAbsenceRequest("max", "vacation", [Format(D(10, 12)), Format(D(10, 13))], "nachgetragen"), "Admin");
        await _service.WhenNotificationsCompleteAsync();

        Assert.Equal(AbsenceStatus.Applied, created.Value!.Status);
        var call = Assert.Single(_telegram.Calls);
        Assert.Equal("sendMessage", call.Method);
        Assert.Empty(call.ButtonData);
        Assert.False(call.Body.TryGetProperty("reply_markup", out var markup) && markup.ValueKind == System.Text.Json.JsonValueKind.Object);
        Assert.Equal(
            "🗂 Abwesenheit erfasst · Max Mustermann\nAdmin hat für Max Mustermann erfasst:\nUrlaub: Mo 12.10. – Di 13.10. (2 Tage)\nResturlaub 2026: 30 → 28 Tage\nKommentar: nachgetragen\n\n✅ In Kimai eingetragen",
            call.Text);
        Assert.Equal(FakeTelegram.FirstMessageId, _store.Find(created.Value.Id)!.TelegramMessageId);

        await _service.CancelAsync(created.Value.Id, "Admin");

        var edit = Assert.Single(_telegram.CallsTo("editMessageText"));
        Assert.Contains("🚫 Storniert von Admin", edit.Text);
    }

    // ------------------------------------------------------------ Knöpfe der Korrekturen

    [Fact]
    public void Callback_RoundTripsAndStaysWithinTelegramsLimit()
    {
        var id = Guid.NewGuid().ToString("N");
        foreach (var action in Enum.GetValues<TelegramAbsenceAction>())
        {
            var data = TelegramAbsenceCallback.Format(action, id);

            Assert.True(System.Text.Encoding.UTF8.GetByteCount(data) <= 64, data);
            Assert.True(TelegramAbsenceCallback.TryParse(data, out var parsed, out var parsedId));
            Assert.Equal((action, id), (parsed, parsedId));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a:approve")]
    [InlineData("a:approve:")]
    [InlineData("a:delete:abc")]
    [InlineData("a:approve:abc:def")]
    [InlineData("c:approve:abc")]
    [InlineData("x:approve:abc")]
    public void Callback_RejectsForeignOrBrokenData(string? data)
    {
        Assert.False(TelegramAbsenceCallback.TryParse(data, out _, out _));
    }

    [Fact]
    public void Callbacks_DoNotCollideWithTheCorrectionPrefix()
    {
        var id = Guid.NewGuid().ToString("N");

        Assert.False(TelegramCorrectionCallback.TryParse(TelegramAbsenceCallback.Format(TelegramAbsenceAction.Approve, id), out _, out _));
        Assert.False(TelegramAbsenceCallback.TryParse(TelegramCorrectionCallback.Format(TelegramCorrectionAction.Approve, id), out _, out _));
    }

    [Fact]
    public async Task CorrectionButtons_StillWorkAlongsideAbsenceButtons()
    {
        var correction = TimeCorrection.Request(TimeCorrectionKind.AddPause);
        _correctionStore.Add(correction);

        await TapAsync(FakeTelegram.Callback(1, TelegramCorrectionCallback.Format(TelegramCorrectionAction.AskApprove, correction.Id)));

        // Wie bisher gehandhabt: der Antrag ist bekannt, die Antwort kommt vom Korrektur-Zweig.
        Assert.Equal("Wirklich genehmigen?", AnswerText());
        Assert.Equal(["Ja, genehmigen", "Zurück"], Assert.Single(_telegram.CallsTo("editMessageReplyMarkup")).ButtonTexts);
    }

    [Fact]
    public void Message_GroupsConsecutiveDaysAndNamesTheYearsWhenTheySpanTwo()
    {
        var request = Request(AbsenceKind.FlexDay, AbsenceStatus.Pending, D(10, 12), D(10, 13), D(10, 23), D(1, 4, 2027));

        var text = TelegramMessageFactory.BuildAbsenceRequest(request);

        Assert.Equal(
            "🏖 Abwesenheitsantrag · Max Mustermann\nGleittag: Mo 12.10.2026 – Di 13.10.2026, Fr 23.10.2026, Mo 04.01.2027 (4 Tage)",
            text);
    }

    private static string Format(DateOnly day) => day.ToString("yyyy-MM-dd");
}
