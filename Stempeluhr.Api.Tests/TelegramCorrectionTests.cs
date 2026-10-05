using System.Net;
using Microsoft.Extensions.Logging;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;
using static Stempeluhr.Api.Tests.TimeCorrection;

namespace Stempeluhr.Api.Tests;

/// <summary>
/// Korrekturanträge über Telegram: Nachricht mit Knöpfen, Bestätigung,
/// Entscheidung per Callback und Aktualisierung nach Entscheidungen anderswo.
/// Service, Store und Kimai sind echt (Fake-Kimai), nur die Bot API ist ein
/// HttpMessageHandler-Fake.
/// </summary>
public sealed class TelegramCorrectionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"stempeluhr-telegram-corrections-{Guid.NewGuid():N}");
    private readonly FakeTelegram _telegram = new();
    private readonly FakeKimai _kimai = new();
    private readonly MutableSettingsStore _settings;
    private readonly TimeCorrectionStore _store;
    private readonly TimeCorrectionService _service;
    private readonly TelegramTimeCorrectionNotifier _notifier;
    private readonly TelegramUpdatePoller _poller;
    private readonly CapturingLogger<TelegramUpdatePoller> _pollerLog = new();
    private readonly CapturingLogger<TelegramTimeCorrectionNotifier> _notifierLog = new();

    public TelegramCorrectionTests()
    {
        _settings = new MutableSettingsStore(Configured());
        _store = new TimeCorrectionStore(Path.Combine(_directory, "time-corrections.json"));
        var api = new TelegramBotApi(new FakeTelegramClientFactory(_telegram));
        _notifier = new TelegramTimeCorrectionNotifier(_settings, api, _store, _notifierLog);
        _service = new TimeCorrectionService(
            _settings, new EmployeeService(), _kimai, _store, _notifier, new PinAttemptGuard(clock: new ManualClock(Now)), clock: new ManualClock(Now));
        _poller = new TelegramUpdatePoller(_settings, api, _service, _store, _notifier, _pollerLog);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static RuntimeSettings Configured(string? correctionChat = FakeTelegram.CorrectionChat, params long[] approvers) => new()
    {
        BaseUrl = "http://kimai.test",
        DefaultProjectId = WorkProject,
        DefaultActivityId = WorkActivity,
        PauseActivityId = PauseActivity,
        TelegramBotToken = FakeTelegram.Token,
        TelegramChatId = "-555",
        TelegramCorrectionChatId = correctionChat,
        TelegramApproverUserIds = [.. approvers],
        Employees = [Max()],
    };

    /// <summary>Submits without waiting for the (background) Telegram message.</summary>
    private async Task<TimeCorrectionDto> SubmitPauseNoWaitAsync()
    {
        var sheet = _kimai.Add(At(6), At(11));
        var result = await _service.SubmitAsync(new SubmitCorrectionRequest(
            "max", "1234", null, "addPause", sheet.Id, null, null, Text(At(8)), Text(At(8, 30)), null, "Pause vergessen", "terminal-1"))
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Outcome == CorrectionOutcome.Ok, result.Message);
        return result.Value!;
    }

    private async Task<TimeCorrectionDto> SubmitPauseAsync()
    {
        var dto = await SubmitPauseNoWaitAsync();
        // The message is sent in the background: wait so the tests stay deterministic.
        await _service.WhenNotificationsCompleteAsync();
        return dto;
    }

    private async Task<TimeSpan> TapAsync(params object[] updates)
    {
        _telegram.Updates.Enqueue(FakeTelegram.UpdatesOf(updates));
        return await _poller.RunCycleAsync(CancellationToken.None);
    }

    private static string Data(TelegramCorrectionAction action, string id) => TelegramCorrectionCallback.Format(action, id);

    private string AnswerText(int index = 0) => _telegram.CallsTo("answerCallbackQuery")[index].Body.GetProperty("text").GetString()!;

    // ------------------------------------------------------------ Nachricht beim Absenden

    [Fact]
    public async Task Submit_SendsTheRequestWithButtonsToTheCorrectionChatAndKeepsTheMessageIds()
    {
        var dto = await SubmitPauseAsync();

        var call = Assert.Single(_telegram.Calls);
        Assert.Equal("sendMessage", call.Method);
        Assert.Equal(FakeTelegram.Token, call.Token);
        Assert.Equal(FakeTelegram.CorrectionChat, call.Body.GetProperty("chat_id").GetString());
        Assert.Equal(
            "📝 Korrekturantrag · Max Mustermann\nPause nachtragen\nSchicht: Mo 05.10. 06:00 – 11:00\nPause: Mo 05.10. 08:00 – 08:30\nKommentar: Pause vergessen",
            call.Text);
        Assert.Equal(["✅ Genehmigen", "❌ Ablehnen"], call.ButtonTexts);
        Assert.Equal([Data(TelegramCorrectionAction.AskApprove, dto.Id), Data(TelegramCorrectionAction.AskReject, dto.Id)], call.ButtonData);
        var stored = _store.Find(dto.Id)!;
        Assert.Equal(FakeTelegram.CorrectionChatNumber, stored.TelegramChatId);
        Assert.Equal(FakeTelegram.FirstMessageId, stored.TelegramMessageId);
    }

    [Fact]
    public async Task Submit_NightShift_NamesWeekdayAndDateOnBothEnds()
    {
        var sheet = _kimai.Add(Day(4, 22), At(6, 10));
        var result = await _service.SubmitAsync(new SubmitCorrectionRequest(
            "max", "1234", null, "setEnd", sheet.Id, null, Text(At(5, 40)), null, null, null, null, null));
        await _service.WhenNotificationsCompleteAsync();
        Assert.True(result.Outcome == CorrectionOutcome.Ok, result.Message);

        var text = Assert.Single(_telegram.Calls).Text;

        Assert.Contains("Ausstempeln nachtragen\nSchicht: So 04.10. 22:00 – Mo 05.10. 06:10\nEnde: Mo 05.10. 06:10 → Mo 05.10. 05:40", text);
    }

    [Fact]
    public async Task Submit_WithoutCorrectionChat_SendsNothing()
    {
        _settings.Settings = Configured(correctionChat: null);

        var dto = await SubmitPauseAsync();
        await _service.ApproveAsync(dto.Id, "Admin");
        await _service.RejectAsync(dto.Id, null, "Admin");

        Assert.Empty(_telegram.Calls);
    }

    [Fact]
    public async Task Submit_WithoutBotToken_SendsNothing()
    {
        _settings.Settings = new RuntimeSettings
        {
            BaseUrl = "http://kimai.test", DefaultProjectId = WorkProject, DefaultActivityId = WorkActivity, PauseActivityId = PauseActivity,
            TelegramCorrectionChatId = FakeTelegram.CorrectionChat, Employees = [Max()],
        };

        await SubmitPauseAsync();

        Assert.Empty(_telegram.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Submit_TelegramRejects_StillStoresTheRequestAndLogsOnly(HttpStatusCode status)
    {
        _telegram.Responder = _ => FakeTelegram.Json(new { ok = false, description = "boom" }, status);

        var dto = await SubmitPauseAsync();

        Assert.Equal(TimeCorrectionStatus.Pending, _store.Find(dto.Id)!.Status);
        Assert.Null(_store.Find(dto.Id)!.TelegramMessageId);
        Assert.Contains(_notifierLog.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Decisions_TelegramDown_DoNotChangeTheBooking()
    {
        var dto = await SubmitPauseAsync();
        _telegram.Responder = _ => throw new HttpRequestException("connection refused");

        var result = await _service.ApproveAsync(dto.Id, "Admin");

        Assert.Equal(TimeCorrectionStatus.Applied, result.Value!.Status);
        Assert.Equal(3, _kimai.Writes.Count);
    }

    // ------------------------------------------------------------ Telegram hängt

    [Fact]
    public async Task Submit_ReturnsWhileTelegramHangs_AndTheMessageFollowsOnceItAnswers()
    {
        var hold = new TaskCompletionSource();
        _telegram.Holds["sendMessage"] = hold;

        var dto = await SubmitPauseNoWaitAsync();

        // Der Antrag ist angelegt und der Mitarbeiter hat seine Antwort, die Nachricht fehlt noch.
        Assert.Equal(TimeCorrectionStatus.Pending, _store.Find(dto.Id)!.Status);
        Assert.Null(_store.Find(dto.Id)!.TelegramMessageId);

        hold.SetResult();
        await _service.WhenNotificationsCompleteAsync();

        Assert.Equal(FakeTelegram.FirstMessageId, _store.Find(dto.Id)!.TelegramMessageId);
        Assert.Single(_telegram.CallsTo("sendMessage"));
    }

    [Fact]
    public async Task Withdraw_ReturnsWhileTelegramHangs_AndTheMessageIsUpdatedAfterwards()
    {
        var dto = await SubmitPauseAsync();
        var hold = new TaskCompletionSource();
        _telegram.Holds["editMessageText"] = hold;

        var result = await _service.WithdrawAsync(new CorrectionAuthRequest("max", "1234", null), dto.Id)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(TimeCorrectionStatus.Withdrawn, result.Value!.Status);

        hold.SetResult();
        await _service.WhenNotificationsCompleteAsync();

        Assert.EndsWith("↩️ Zurückgezogen · 05.10. 12:00", Assert.Single(_telegram.CallsTo("editMessageText")).Text);
    }

    [Fact]
    public async Task Approval_WhileTheMessageIsStillBeingSent_IsNotBlockedAndTheMessageShowsTheResultLater()
    {
        var hold = new TaskCompletionSource();
        _telegram.Holds["sendMessage"] = hold;
        var dto = await SubmitPauseNoWaitAsync();

        // Entscheidung vor der Nachricht: OnDecided findet noch keine und tut nichts.
        var result = await _service.ApproveAsync(dto.Id, "Admin").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeCorrectionStatus.Applied, result.Value!.Status);
        Assert.Empty(_telegram.CallsTo("editMessageText"));

        hold.SetResult();
        await _service.WhenNotificationsCompleteAsync();

        var edit = Assert.Single(_telegram.CallsTo("editMessageText"));
        Assert.EndsWith("✅ Genehmigt von Admin · 05.10. 12:00 – in Kimai eingetragen", edit.Text);
        Assert.True(edit.KeyboardIsEmpty);
    }

    // ------------------------------------------------------------ Nachricht nach Entscheidungen

    [Fact]
    public async Task AdminApproval_ReplacesTheMessageWithTheResultAndRemovesTheButtons()
    {
        var dto = await SubmitPauseAsync();

        await _service.ApproveAsync(dto.Id, "Admin");

        var edit = Assert.Single(_telegram.CallsTo("editMessageText"));
        Assert.Equal(FakeTelegram.CorrectionChatNumber, edit.Body.GetProperty("chat_id").GetInt64());
        Assert.Equal(FakeTelegram.FirstMessageId, edit.Body.GetProperty("message_id").GetInt64());
        Assert.True(edit.KeyboardIsEmpty);
        Assert.StartsWith("📝 Korrekturantrag · Max Mustermann\nPause nachtragen", edit.Text);
        Assert.EndsWith("✅ Genehmigt von Admin · 05.10. 12:00 – in Kimai eingetragen", edit.Text);
    }

    [Fact]
    public async Task AdminRejection_ShowsTheNote()
    {
        var dto = await SubmitPauseAsync();

        await _service.RejectAsync(dto.Id, "Bitte mit Chef klären", "Admin");

        Assert.EndsWith("❌ Abgelehnt von Admin · 05.10. 12:00\nGrund: Bitte mit Chef klären", Assert.Single(_telegram.CallsTo("editMessageText")).Text);
    }

    [Fact]
    public async Task Withdrawal_ReplacesTheMessageWithWithdrawn()
    {
        var dto = await SubmitPauseAsync();

        var result = await _service.WithdrawAsync(new CorrectionAuthRequest("max", "1234", null), dto.Id);
        await _service.WhenNotificationsCompleteAsync();

        Assert.Equal(CorrectionOutcome.Ok, result.Outcome);
        var edit = Assert.Single(_telegram.CallsTo("editMessageText"));
        Assert.EndsWith("↩️ Zurückgezogen · 05.10. 12:00", edit.Text);
        Assert.True(edit.KeyboardIsEmpty);
    }

    [Fact]
    public async Task KimaiFailureAndManualResolution_AreShownInTheMessage()
    {
        var dto = await SubmitPauseAsync();
        // Der Eintrag ändert sich nach dem Antrag: das Anwenden scheitert.
        _kimai.Sheets[0].End = At(10);

        await _service.ApproveAsync(dto.Id, "Admin");
        await _service.ResolveManuallyAsync(dto.Id, "Admin");

        var edits = _telegram.CallsTo("editMessageText");
        Assert.Equal(2, edits.Count);
        Assert.EndsWith(
            "⚠️ Nicht in Kimai eingetragen: Eintrag wurde inzwischen geändert – bitte in Kimai nachtragen (genehmigt von Admin · 05.10. 12:00)",
            edits[0].Text);
        Assert.EndsWith("☑️ Von Admin · 05.10. 12:00 manuell in Kimai nachgetragen", edits[1].Text);
    }

    [Fact]
    public async Task SlowFailedEdit_DoesNotOverwriteTheResultOfALaterRetry()
    {
        // Antrags-Lock und Notifier-Aufruf sind getrennt: ein langsamer Edit
        // "Nicht eingetragen" darf nicht nach dem Edit des erfolgreichen
        // Retry bei Telegram ankommen.
        var dto = await SubmitPauseAsync();
        var finished = new List<string>();
        _telegram.Responder = call =>
        {
            if (call.Method == "editMessageText") lock (finished) finished.Add(call.Text);
            return null;
        };
        var hold = new TaskCompletionSource();
        _telegram.Holds["editMessageText"] = hold;
        _kimai.Sheets[0].End = At(10);

        var approve = _service.ApproveAsync(dto.Id, "Admin");
        Assert.True(SpinWait.SpinUntil(() => _telegram.CallsTo("editMessageText").Count == 1, TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeCorrectionStatus.Failed, _store.Find(dto.Id)!.Status);

        // Nur der erste Edit hängt; der Retry bucht und will danach editieren.
        _telegram.Holds.TryRemove("editMessageText", out _);
        _kimai.Sheets[0].End = At(11);
        var retry = _service.RetryAsync(dto.Id, "Admin");
        await Task.WhenAny(retry, Task.Delay(TimeSpan.FromMilliseconds(300)));

        hold.SetResult();
        await Task.WhenAll(approve, retry).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(TimeCorrectionStatus.Applied, _store.Find(dto.Id)!.Status);
        Assert.EndsWith("– in Kimai eingetragen", finished[^1]);
    }

    [Fact]
    public async Task ShowButtons_AfterADecisionMeanwhile_ShowsTheResultInsteadOfButtons()
    {
        // Der Poller hat "offen" gelesen, entschieden wurde, bevor er die Knöpfe
        // tauscht: die Bestätigungsknöpfe dürfen nicht wieder erscheinen.
        var dto = await SubmitPauseAsync();
        await _service.RejectAsync(dto.Id, null, "Admin");

        var shown = await _notifier.ShowButtonsAsync(dto.Id, TelegramMessageFactory.BuildCorrectionConfirmKeyboard(dto.Id, approve: true));

        Assert.Equal(TimeCorrectionStatus.Rejected, shown!.Status);
        Assert.Empty(_telegram.CallsTo("editMessageReplyMarkup"));
        Assert.All(_telegram.CallsTo("editMessageText"), edit => Assert.Contains("❌ Abgelehnt von Admin", edit.Text));
    }

    [Fact]
    public async Task DecisionWhileTheMessageIsStillBeingSent_UpdatesTheMessageOnceItExists()
    {
        // Entscheidung, während sendMessage noch läuft: OnDecided findet noch keine
        // Nachricht - der Notifier trägt das Ergebnis nach, sobald sie angehängt ist.
        string? requestId = null;
        _telegram.Responder = call =>
        {
            if (call.Method == "sendMessage")
            {
                requestId = TelegramCorrectionCallback.TryParse(call.ButtonData[0], out _, out var id) ? id : null;
                _service.ApproveAsync(requestId!, "Admin").GetAwaiter().GetResult();
            }
            return null;
        };

        var dto = await SubmitPauseAsync();

        Assert.Equal(dto.Id, requestId);
        var edit = Assert.Single(_telegram.CallsTo("editMessageText"));
        Assert.Contains("✅ Genehmigt von Admin", edit.Text);
        Assert.Equal(FakeTelegram.FirstMessageId, _store.Find(dto.Id)!.TelegramMessageId);
        Assert.Equal(TimeCorrectionStatus.Applied, _store.Find(dto.Id)!.Status);
    }

    [Fact]
    public async Task Store_KeepsTheTelegramMessageWhenTheServiceUpdatesWithAnOlderState()
    {
        var dto = await SubmitPauseAsync();
        var stale = _store.Find(dto.Id)! with { TelegramChatId = null, TelegramMessageId = null, Status = TimeCorrectionStatus.Rejected };

        _store.Update(stale);

        var stored = _store.Find(dto.Id)!;
        Assert.Equal(TimeCorrectionStatus.Rejected, stored.Status);
        Assert.Equal(FakeTelegram.FirstMessageId, stored.TelegramMessageId);
        Assert.Equal(FakeTelegram.CorrectionChatNumber, stored.TelegramChatId);
    }

    // ------------------------------------------------------------ Knöpfe

    [Fact]
    public async Task Tap_Approve_AsksForConfirmationFirstAndDecidesNothing()
    {
        var dto = await SubmitPauseAsync();

        await TapAsync(FakeTelegram.Callback(1, Data(TelegramCorrectionAction.AskApprove, dto.Id)));

        var markup = Assert.Single(_telegram.CallsTo("editMessageReplyMarkup"));
        Assert.Equal(["Ja, genehmigen", "Zurück"], markup.ButtonTexts);
        Assert.Equal([Data(TelegramCorrectionAction.Approve, dto.Id), Data(TelegramCorrectionAction.Back, dto.Id)], markup.ButtonData);
        Assert.Equal(FakeTelegram.FirstMessageId, markup.Body.GetProperty("message_id").GetInt64());
        Assert.Equal(TimeCorrectionStatus.Pending, _store.Find(dto.Id)!.Status);
        Assert.Empty(_kimai.Writes);
        Assert.Single(_telegram.CallsTo("answerCallbackQuery"));
        Assert.Empty(_telegram.CallsTo("editMessageText"));
    }

    [Fact]
    public async Task Tap_Reject_AsksForConfirmationAndBackRestoresTheFirstButtons()
    {
        var dto = await SubmitPauseAsync();

        await TapAsync(
            FakeTelegram.Callback(1, Data(TelegramCorrectionAction.AskReject, dto.Id)),
            FakeTelegram.Callback(2, Data(TelegramCorrectionAction.Back, dto.Id)));

        var markups = _telegram.CallsTo("editMessageReplyMarkup");
        Assert.Equal(["Ja, ablehnen", "Zurück"], markups[0].ButtonTexts);
        Assert.Equal(["✅ Genehmigen", "❌ Ablehnen"], markups[1].ButtonTexts);
        Assert.Equal(TimeCorrectionStatus.Pending, _store.Find(dto.Id)!.Status);
        Assert.Equal(2, _telegram.CallsTo("answerCallbackQuery").Count);
    }

    [Fact]
    public async Task Tap_ConfirmApprove_BooksInKimaiAsTheTelegramNameAndUpdatesTheMessage()
    {
        var dto = await SubmitPauseAsync();

        await TapAsync(FakeTelegram.Callback(1, Data(TelegramCorrectionAction.Approve, dto.Id), firstName: "Max", username: "chef"));

        var stored = _store.Find(dto.Id)!;
        Assert.Equal(TimeCorrectionStatus.Applied, stored.Status);
        Assert.Equal("Max", stored.DecidedBy);
        Assert.Equal(3, _kimai.Writes.Count);
        Assert.Equal("Genehmigt", AnswerText());
        Assert.Contains("✅ Genehmigt von Max", Assert.Single(_telegram.CallsTo("editMessageText")).Text);
    }

    [Fact]
    public async Task Tap_ConfirmReject_RejectsWithoutReasonAndFallsBackToTheUsername()
    {
        var dto = await SubmitPauseAsync();

        await TapAsync(FakeTelegram.Callback(1, Data(TelegramCorrectionAction.Reject, dto.Id), firstName: " ", username: "chef"));

        var stored = _store.Find(dto.Id)!;
        Assert.Equal(TimeCorrectionStatus.Rejected, stored.Status);
        Assert.Equal("chef", stored.DecidedBy);
        Assert.Null(stored.DecisionNote);
        Assert.Empty(_kimai.Writes);
        Assert.Equal("Abgelehnt", AnswerText());
        Assert.Contains("❌ Abgelehnt von chef", Assert.Single(_telegram.CallsTo("editMessageText")).Text);
    }

    [Fact]
    public async Task Tap_ConfirmApprove_KimaiRefuses_TellsTheApproverToBookByHand()
    {
        var dto = await SubmitPauseAsync();
        _kimai.Sheets[0].End = At(10);

        await TapAsync(FakeTelegram.Callback(1, Data(TelegramCorrectionAction.Approve, dto.Id)));

        Assert.Equal(TimeCorrectionStatus.Failed, _store.Find(dto.Id)!.Status);
        Assert.Contains("nicht in Kimai eingetragen", AnswerText());
        Assert.Contains("⚠️ Nicht in Kimai eingetragen", Assert.Single(_telegram.CallsTo("editMessageText")).Text);
    }

    [Fact]
    public async Task Tap_InAnotherChat_IsDeniedAndDoesNothing()
    {
        var dto = await SubmitPauseAsync();

        await TapAsync(FakeTelegram.Callback(1, Data(TelegramCorrectionAction.Approve, dto.Id), chatId: -999));

        Assert.Equal("Keine Berechtigung", AnswerText());
        Assert.Equal(TimeCorrectionStatus.Pending, _store.Find(dto.Id)!.Status);
        Assert.Empty(_kimai.Writes);
        Assert.Empty(_telegram.CallsTo("editMessageReplyMarkup"));
        Assert.Empty(_telegram.CallsTo("editMessageText"));
    }

    [Fact]
    public async Task Tap_WithoutMessage_IsDenied()
    {
        var dto = await SubmitPauseAsync();

        await TapAsync(FakeTelegram.Callback(1, Data(TelegramCorrectionAction.Approve, dto.Id), chatId: null));

        Assert.Equal("Keine Berechtigung", AnswerText());
        Assert.Equal(TimeCorrectionStatus.Pending, _store.Find(dto.Id)!.Status);
    }

    [Fact]
    public async Task Tap_ByAUserOutsideTheApproverList_IsDeniedInBothSteps()
    {
        _settings.Settings = Configured(FakeTelegram.CorrectionChat, 7, 8);
        var dto = await SubmitPauseAsync();

        await TapAsync(
            FakeTelegram.Callback(1, Data(TelegramCorrectionAction.AskApprove, dto.Id), userId: 99),
            FakeTelegram.Callback(2, Data(TelegramCorrectionAction.Approve, dto.Id), userId: 99));

        Assert.Equal("Keine Berechtigung", AnswerText(0));
        Assert.Equal("Keine Berechtigung", AnswerText(1));
        Assert.Equal(TimeCorrectionStatus.Pending, _store.Find(dto.Id)!.Status);
        Assert.Empty(_telegram.CallsTo("editMessageReplyMarkup"));
        Assert.Empty(_kimai.Writes);
    }

    [Fact]
    public async Task Tap_ByAnApprover_Decides()
    {
        _settings.Settings = Configured(FakeTelegram.CorrectionChat, 7, 8);
        var dto = await SubmitPauseAsync();

        await TapAsync(FakeTelegram.Callback(1, Data(TelegramCorrectionAction.Approve, dto.Id), userId: 8));

        Assert.Equal(TimeCorrectionStatus.Applied, _store.Find(dto.Id)!.Status);
    }

    [Fact]
    public async Task Tap_WithoutApproverList_AnyChatMemberDecides()
    {
        var dto = await SubmitPauseAsync();

        await TapAsync(FakeTelegram.Callback(1, Data(TelegramCorrectionAction.Reject, dto.Id), userId: 12345));

        Assert.Equal(TimeCorrectionStatus.Rejected, _store.Find(dto.Id)!.Status);
    }

    [Fact]
    public async Task Tap_Twice_SecondConfirmationIsANoOpAndShowsTheResult()
    {
        var dto = await SubmitPauseAsync();
        var confirm = Data(TelegramCorrectionAction.Approve, dto.Id);

        await TapAsync(FakeTelegram.Callback(1, confirm, firstName: "Max"));
        await TapAsync(FakeTelegram.Callback(2, confirm, firstName: "Erika"));

        Assert.Equal(3, _kimai.Writes.Count);
        Assert.Equal("Max", _store.Find(dto.Id)!.DecidedBy);
        Assert.Equal("Bereits entschieden: Genehmigt von Max", AnswerText(1));
        Assert.Equal(2, _telegram.CallsTo("answerCallbackQuery").Count);
        // Die Nachricht wird beim zweiten Tap noch einmal auf den Stand gebracht.
        Assert.Equal(2, _telegram.CallsTo("editMessageText").Count);
    }

    [Fact]
    public async Task Tap_AfterTheAdminDecided_AnswersAlreadyDecidedAndUpdatesTheMessage()
    {
        var dto = await SubmitPauseAsync();
        await _service.RejectAsync(dto.Id, null, "Admin");
        // Der Admin-Entscheid hat die Nachricht schon aktualisiert; der Tap kommt von einem alten Stand.

        await TapAsync(FakeTelegram.Callback(1, Data(TelegramCorrectionAction.Approve, dto.Id)));

        Assert.Equal("Bereits entschieden: Abgelehnt von Admin", AnswerText());
        Assert.Equal(TimeCorrectionStatus.Rejected, _store.Find(dto.Id)!.Status);
        Assert.Empty(_kimai.Writes);
        Assert.Equal(2, _telegram.CallsTo("editMessageText").Count);
        Assert.Contains("❌ Abgelehnt von Admin", _telegram.CallsTo("editMessageText")[1].Text);
    }

    [Fact]
    public async Task Tap_OnAWithdrawnRequest_AnswersAlreadyDecided()
    {
        var dto = await SubmitPauseAsync();
        await _service.WithdrawAsync(new CorrectionAuthRequest("max", "1234", null), dto.Id);
        await _service.WhenNotificationsCompleteAsync();

        await TapAsync(FakeTelegram.Callback(1, Data(TelegramCorrectionAction.AskApprove, dto.Id)));

        Assert.Equal("Bereits entschieden: Zurückgezogen", AnswerText());
        Assert.Empty(_telegram.CallsTo("editMessageReplyMarkup"));
    }

    [Theory]
    [InlineData("unsinn")]
    [InlineData("c:approve")]
    [InlineData("c:explode:abc")]
    public async Task Tap_WithUnknownData_IsAnsweredAnyway(string data)
    {
        await TapAsync(FakeTelegram.Callback(1, data));

        Assert.Equal("Unbekannte Aktion", AnswerText());
    }

    [Fact]
    public async Task Tap_OnAnUnknownRequest_IsAnswered()
    {
        await TapAsync(FakeTelegram.Callback(1, Data(TelegramCorrectionAction.Approve, "0123456789abcdef0123456789abcdef")));

        Assert.Equal("Antrag nicht gefunden", AnswerText());
    }

    [Fact]
    public async Task Tap_WhenTheServiceThrows_IsAnsweredAndTheNextUpdatesStillRun()
    {
        var dto = await SubmitPauseAsync();
        var failing = new TelegramUpdatePoller(
            _settings, new TelegramBotApi(new FakeTelegramClientFactory(_telegram)), new ThrowingApproveService(), _store, _notifier, _pollerLog);

        _telegram.Updates.Enqueue(FakeTelegram.UpdatesOf(
            FakeTelegram.Callback(1, Data(TelegramCorrectionAction.Approve, dto.Id)),
            FakeTelegram.Callback(2, Data(TelegramCorrectionAction.AskApprove, dto.Id))));
        var wait = await failing.RunCycleAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.Zero, wait);
        Assert.Equal("Fehler – bitte die Admin-Seite nutzen.", AnswerText(0));
        Assert.Equal("Wirklich genehmigen?", AnswerText(1));
        Assert.Equal(TimeCorrectionStatus.Pending, _store.Find(dto.Id)!.Status);
    }

    [Theory]
    [InlineData(TelegramCorrectionAction.AskApprove)]
    [InlineData(TelegramCorrectionAction.AskReject)]
    [InlineData(TelegramCorrectionAction.Approve)]
    [InlineData(TelegramCorrectionAction.Reject)]
    [InlineData(TelegramCorrectionAction.Back)]
    public void CallbackData_FitsTelegramsLimitAndRoundTrips(TelegramCorrectionAction action)
    {
        var id = Guid.NewGuid().ToString("N");

        var data = TelegramCorrectionCallback.Format(action, id);

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(data) <= 64);
        Assert.StartsWith("c:", data);
        Assert.True(TelegramCorrectionCallback.TryParse(data, out var parsed, out var parsedId));
        Assert.Equal(action, parsed);
        Assert.Equal(id, parsedId);
    }

    // ------------------------------------------------------------ Polling

    [Fact]
    public async Task Cycle_PollsForButtonTapsOnlyAndConfirmsTheOffset()
    {
        await TapAsync(FakeTelegram.Callback(10, "unsinn"), FakeTelegram.Callback(11, "unsinn"));
        await _poller.RunCycleAsync(CancellationToken.None);

        var polls = _telegram.CallsTo("getUpdates");
        Assert.Equal(2, polls.Count);
        Assert.All(polls, poll => Assert.Equal("TelegramPoll", poll.ClientName));
        Assert.Equal(50, polls[0].Body.GetProperty("timeout").GetInt32());
        Assert.Equal(["callback_query"], polls[0].Body.GetProperty("allowed_updates").EnumerateArray().Select(value => value.GetString()));
        Assert.False(polls[0].Body.TryGetProperty("offset", out _));
        Assert.Equal(12, polls[1].Body.GetProperty("offset").GetInt64());
    }

    [Fact]
    public async Task Cycle_WithoutCorrectionChat_MakesNoRequestAndWaitsInIdle()
    {
        _settings.Settings = Configured(correctionChat: null);

        var wait = await _poller.RunCycleAsync(CancellationToken.None);

        Assert.Empty(_telegram.Calls);
        Assert.Equal(TimeSpan.FromSeconds(15), wait);
    }

    [Fact]
    public async Task Cycle_ReloadsTheSettingsEveryTime()
    {
        _settings.Settings = Configured(correctionChat: null);
        await _poller.RunCycleAsync(CancellationToken.None);
        Assert.Empty(_telegram.Calls);

        _settings.Settings = Configured();
        await _poller.RunCycleAsync(CancellationToken.None);
        Assert.Single(_telegram.CallsTo("getUpdates"));

        _settings.Settings = Configured(correctionChat: " ");
        await _poller.RunCycleAsync(CancellationToken.None);
        Assert.Single(_telegram.CallsTo("getUpdates"));
    }

    [Fact]
    public async Task Cycle_Conflict409_LogsOnceAndBacksOff()
    {
        _telegram.Responder = call => call.Method == "getUpdates"
            ? FakeTelegram.Json(new { ok = false, error_code = 409, description = "Conflict: can't use getUpdates method while webhook is active" }, HttpStatusCode.Conflict)
            : null;

        var waits = new List<TimeSpan>();
        for (var i = 0; i < 4; i++)
        {
            waits.Add(await _poller.RunCycleAsync(CancellationToken.None));
        }

        Assert.Equal([5, 10, 20, 40], waits.Select(wait => (int)wait.TotalSeconds));
        Assert.Equal(1, _pollerLog.Count(LogLevel.Warning));
        Assert.Contains("409", _pollerLog.Entries.Single(entry => entry.Level == LogLevel.Warning).Message);
        Assert.Equal(0, _pollerLog.Count(LogLevel.Error));
    }

    [Fact]
    public async Task Cycle_NetworkErrors_BackOffUpToAMinuteWithoutFloodingTheLog()
    {
        _telegram.Responder = call => call.Method == "getUpdates" ? throw new HttpRequestException("Network is unreachable") : null;

        var waits = new List<TimeSpan>();
        for (var i = 0; i < 8; i++)
        {
            waits.Add(await _poller.RunCycleAsync(CancellationToken.None));
        }

        Assert.Equal([5, 10, 20, 40, 60, 60, 60, 60], waits.Select(wait => (int)wait.TotalSeconds));
        Assert.Equal(1, _pollerLog.Count(LogLevel.Warning));
        Assert.All(_pollerLog.Entries, entry => Assert.Null(entry.Exception));
    }

    [Fact]
    public async Task Cycle_ClientTimeout_CountsAsANetworkError()
    {
        _telegram.Responder = call => call.Method == "getUpdates" ? throw new TaskCanceledException("timeout") : null;

        var wait = await _poller.RunCycleAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(5), wait);
    }

    [Fact]
    public async Task Cycle_AfterAFailure_RecoversAndStartsTheBackoffOver()
    {
        var failing = true;
        _telegram.Responder = call => failing && call.Method == "getUpdates" ? throw new HttpRequestException("down") : null;

        await _poller.RunCycleAsync(CancellationToken.None);
        await _poller.RunCycleAsync(CancellationToken.None);
        failing = false;
        var recovered = await _poller.RunCycleAsync(CancellationToken.None);
        failing = true;
        var again = await _poller.RunCycleAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.Zero, recovered);
        Assert.Equal(TimeSpan.FromSeconds(5), again);
        // Eine neue Störung wird wieder gemeldet.
        Assert.Equal(2, _pollerLog.Count(LogLevel.Warning));
        Assert.Equal(1, _pollerLog.Count(LogLevel.Information));
    }

    [Fact]
    public async Task Cycle_OtherHttpErrors_BackOffToo()
    {
        _telegram.Responder = call => call.Method == "getUpdates"
            ? FakeTelegram.Json(new { ok = false, description = "Unauthorized" }, HttpStatusCode.Unauthorized)
            : null;

        var wait = await _poller.RunCycleAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(5), wait);
        Assert.Equal(1, _pollerLog.Count(LogLevel.Warning));
    }

    [Fact]
    public async Task Run_WaitsTheBackoffBetweenCyclesAndStopsOnCancellation()
    {
        _telegram.Responder = call => call.Method == "getUpdates" ? throw new HttpRequestException("down") : null;
        var waits = new List<TimeSpan>();
        using var cancellation = new CancellationTokenSource();
        var poller = new TelegramUpdatePoller(
            _settings, new TelegramBotApi(new FakeTelegramClientFactory(_telegram)), _service, _store, _notifier, _pollerLog,
            delay: (span, _) =>
            {
                waits.Add(span);
                if (waits.Count == 7) cancellation.Cancel();
                return Task.CompletedTask;
            });

        await poller.StartAsync(cancellation.Token);
        await poller.ExecuteTask!;

        Assert.Equal([5, 10, 20, 40, 60, 60, 60], waits.Select(wait => (int)wait.TotalSeconds));
        Assert.Equal(7, _telegram.CallsTo("getUpdates").Count);
        Assert.Equal(1, _pollerLog.Count(LogLevel.Warning));
    }

    [Fact]
    public async Task Run_SettingsThatFail_DoNotStopTheHost()
    {
        var waits = new List<TimeSpan>();
        using var cancellation = new CancellationTokenSource();
        var poller = new TelegramUpdatePoller(
            new ThrowingSettingsStore(), new TelegramBotApi(new FakeTelegramClientFactory(_telegram)), _service, _store, _notifier, _pollerLog,
            delay: (span, _) =>
            {
                waits.Add(span);
                if (waits.Count == 3) cancellation.Cancel();
                return Task.CompletedTask;
            });

        await poller.StartAsync(cancellation.Token);
        await poller.ExecuteTask!;

        Assert.Equal([5, 10, 20], waits.Select(wait => (int)wait.TotalSeconds));
        Assert.Empty(_telegram.Calls);
    }

    private sealed class ThrowingSettingsStore : IRuntimeSettingsStore
    {
        public RuntimeSettings Load() => throw new IOException("settings.json is locked");

        public Task SaveAsync(RuntimeSettings settings, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>Only <see cref="ApproveAsync"/> is used: it fails like an unexpected store error.</summary>
    private sealed class ThrowingApproveService : ITimeCorrectionService
    {
        public Task<CorrectionResult<TimeCorrectionDto>> ApproveAsync(string id, string decidedBy, CancellationToken cancellationToken = default)
            => throw new IOException("disk full");

        public Task<CorrectionResult<CorrectionTimesheetsDto>> GetSelectableShiftsAsync(CorrectionAuthRequest auth, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CorrectionResult<TimeCorrectionDto>> SubmitAsync(SubmitCorrectionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CorrectionResult<IReadOnlyList<TimeCorrectionDto>>> ListOwnAsync(CorrectionAuthRequest auth, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CorrectionResult<TimeCorrectionDto>> WithdrawAsync(CorrectionAuthRequest auth, string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IReadOnlyList<TimeCorrectionDto> List(bool openOnly) => throw new NotSupportedException();
        public Task<CorrectionResult<TimeCorrectionDto>> RejectAsync(string id, string? note, string decidedBy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CorrectionResult<TimeCorrectionDto>> RetryAsync(string id, string decidedBy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CorrectionResult<TimeCorrectionDto>> ResolveManuallyAsync(string id, string decidedBy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
