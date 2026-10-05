using System.Globalization;
using System.Text.Json;
using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

/// <summary>
/// Empfängt die Knopf-Drücke der Korrekturanträge über Long-Polling
/// (<c>getUpdates</c>, kein Webhook). Läuft nur mit Bot-Token und
/// Korrektur-Chat; die Settings werden bei jedem Zyklus neu geladen, eine
/// Änderung wirkt also ohne Neustart. Der Offset liegt im Speicher: nach einem
/// Neustart kommen unbestätigte Updates noch einmal an, was der Status-Lock im
/// <see cref="ITimeCorrectionService"/> zum No-op macht. Entschieden wird erst
/// nach einer Bestätigung ("Ja, genehmigen"/"Ja, ablehnen"), und nur im
/// Korrektur-Chat von erlaubten Personen.
/// </summary>
public sealed class TelegramUpdatePoller(
    IRuntimeSettingsStore settingsStore,
    TelegramBotApi api,
    ITimeCorrectionService corrections,
    TimeCorrectionStore store,
    TelegramTimeCorrectionNotifier messages,
    ILogger<TelegramUpdatePoller> logger,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : BackgroundService
{
    /// <summary>Wartezeit von Telegram pro Abruf; der HttpClient-Timeout liegt darüber (siehe Program.cs).</summary>
    public const int PollTimeoutSeconds = 50;

    /// <summary>Wie oft ohne Token oder Korrektur-Chat geprüft wird, ob die Settings es nun erlauben.</summary>
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    private const int MaxDecidedByLength = 64;

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    private long? _offset;
    private string? _offsetToken;
    private int _failures;
    private string? _failureKind;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                wait = await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Eine Ausnahme würde sonst den Host stoppen.
                logger.LogError(ex, "Telegram update polling failed unexpectedly");
                wait = NextBackoff();
            }

            if (wait <= TimeSpan.Zero)
            {
                continue;
            }

            try
            {
                await _delay(wait, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Ein Abruf samt Verarbeitung. Gibt die Wartezeit bis zum nächsten Abruf
    /// zurück: keine nach Erfolg (Telegram hat schon bis zu
    /// <see cref="PollTimeoutSeconds"/> gewartet), sonst Leerlauf oder Backoff.
    /// </summary>
    public async Task<TimeSpan> RunCycleAsync(CancellationToken cancellationToken)
    {
        RuntimeSettings settings;
        try
        {
            settings = settingsStore.Load();
        }
        catch (Exception ex)
        {
            return Fail("settings", $"Settings could not be loaded for Telegram polling: {ex.Message}");
        }

        if (!settings.TelegramCorrectionsEnabled)
        {
            _failures = 0;
            _failureKind = null;
            return IdleDelay;
        }

        var token = settings.TelegramBotToken!;
        if (_offsetToken != token)
        {
            // Update-IDs gelten pro Bot.
            _offset = null;
            _offsetToken = token;
        }

        TelegramApiResponse response;
        try
        {
            response = await api.GetUpdatesAsync(token, _offset, PollTimeoutSeconds, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Nur die Meldung: eine Ausnahme pro Versuch wäre eine Flut im Log.
            return Fail("network", $"Telegram getUpdates failed: {ex.Message}");
        }

        if (response.StatusCode == 409)
        {
            return Fail("conflict",
                $"Telegram getUpdates answered 409 ({response.Description}). A webhook is set for this bot or a second poller uses its token. Correction buttons do not work until that is resolved.");
        }

        if (!response.Ok || response.Result is not { ValueKind: JsonValueKind.Array } updates)
        {
            return Fail($"http-{response.StatusCode}",
                $"Telegram getUpdates failed ({response.StatusCode}): {response.Description}");
        }

        if (_failureKind is not null)
        {
            logger.LogInformation("Telegram update polling works again");
        }
        _failures = 0;
        _failureKind = null;

        foreach (var update in updates.EnumerateArray())
        {
            try
            {
                if (update.TryGetProperty("callback_query", out var query))
                {
                    await HandleCallbackAsync(settings, query, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Telegram update could not be processed");
            }

            // Auch nach einem Fehler bestätigen, sonst käme dasselbe Update ewig wieder.
            if (update.TryGetProperty("update_id", out var updateId) && updateId.TryGetInt64(out var id))
            {
                _offset = id + 1;
            }
        }

        return TimeSpan.Zero;
    }

    /// <summary>Loggt nur den Anfang einer Störung (pro Art) und wartet länger, bis zu <see cref="MaxBackoff"/>.</summary>
    private TimeSpan Fail(string kind, string message)
    {
        if (_failureKind != kind)
        {
            _failureKind = kind;
            logger.LogWarning("{Message}", message);
        }
        else
        {
            logger.LogDebug("{Message}", message);
        }
        return NextBackoff();
    }

    private TimeSpan NextBackoff()
    {
        _failures++;
        var seconds = InitialBackoff.TotalSeconds * Math.Pow(2, Math.Min(_failures - 1, 10));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
    }

    // ------------------------------------------------------------ Knöpfe

    /// <summary>Beantwortet jede <c>callback_query</c>, auch bei Fehlern.</summary>
    private async Task HandleCallbackAsync(RuntimeSettings settings, JsonElement query, CancellationToken cancellationToken)
    {
        if (!query.TryGetProperty("id", out var idValue) || idValue.GetString() is not { } callbackQueryId)
        {
            return;
        }

        string? answer;
        try
        {
            answer = await ProcessCallbackAsync(settings, query, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Telegram correction callback failed");
            answer = "Fehler – bitte die Admin-Seite nutzen.";
        }

        try
        {
            var response = await api.AnswerCallbackQueryAsync(settings.TelegramBotToken!, callbackQueryId, answer, cancellationToken);
            if (!response.Ok)
            {
                // Meist "query is too old": der Tap ist schon verfallen.
                logger.LogDebug("answerCallbackQuery failed ({StatusCode}): {Description}", response.StatusCode, response.Description);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("answerCallbackQuery failed: {Message}", ex.Message);
        }
    }

    /// <returns>Der Text für <c>answerCallbackQuery</c> (null: ohne Hinweis).</returns>
    private async Task<string?> ProcessCallbackAsync(RuntimeSettings settings, JsonElement query, CancellationToken cancellationToken)
    {
        const string Denied = "Keine Berechtigung";

        long? messageChatId = null;
        long? messageNumber = null;
        if (query.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
        {
            messageChatId = message.TryGetProperty("chat", out var chat) ? ReadInt64(chat, "id") : null;
            messageNumber = ReadInt64(message, "message_id");
        }

        // Falscher Chat (der Bot kann in mehreren Gruppen sein) oder eine
        // Nachricht, die Telegram nicht mehr ausliefert: nichts passiert.
        if (messageChatId is not { } chatId
            || messageNumber is not { } messageId
            || !string.Equals(chatId.ToString(CultureInfo.InvariantCulture), settings.TelegramCorrectionChatId!.Trim(), StringComparison.Ordinal))
        {
            return Denied;
        }

        var from = query.TryGetProperty("from", out var sender) ? sender : default;
        var userId = from.ValueKind == JsonValueKind.Object ? ReadInt64(from, "id") : null;
        if (settings.TelegramApproverUserIds.Count > 0
            && (userId is not { } user || !settings.TelegramApproverUserIds.Contains(user)))
        {
            return Denied;
        }

        var data = query.TryGetProperty("data", out var dataValue) && dataValue.ValueKind == JsonValueKind.String ? dataValue.GetString() : null;
        if (!TelegramCorrectionCallback.TryParse(data, out var action, out var id))
        {
            return "Unbekannte Aktion";
        }

        if (store.Find(id) is not { } found)
        {
            return "Antrag nicht gefunden";
        }

        // Ging die Antwort auf sendMessage verloren, kennt der Antrag seine
        // Nachricht nicht: die des (geprüften) Knopf-Drucks übernehmen.
        var request = await messages.AdoptMessageAsync(id, chatId, messageId, cancellationToken) ?? found;

        if (request.Status != TimeCorrectionStatus.Pending)
        {
            return await AlreadyDecidedAsync(id, request, cancellationToken);
        }

        switch (action)
        {
            case TelegramCorrectionAction.AskApprove:
            case TelegramCorrectionAction.AskReject:
            case TelegramCorrectionAction.Back:
                // Über den Notifier: die Knöpfe wechseln nur, solange der Antrag
                // dabei noch offen ist, und nie quer zu einem Ergebnis-Edit.
                var approve = action == TelegramCorrectionAction.AskApprove;
                var keyboard = action == TelegramCorrectionAction.Back
                    ? TelegramMessageFactory.BuildCorrectionKeyboard(id)
                    : TelegramMessageFactory.BuildCorrectionConfirmKeyboard(id, approve);
                var shown = await messages.ShowButtonsAsync(id, keyboard, cancellationToken) ?? request;
                if (shown.Status != TimeCorrectionStatus.Pending)
                {
                    return Truncate(TelegramMessageFactory.BuildCorrectionAlreadyDecided(shown), 190);
                }
                return action switch
                {
                    TelegramCorrectionAction.AskApprove => "Wirklich genehmigen?",
                    TelegramCorrectionAction.AskReject => "Wirklich ablehnen?",
                    _ => null,
                };
        }

        // Die Antwort der Nachricht (Ergebnis statt Knöpfe) schickt der
        // Notifier des Service; hier nur der Hinweis für den Tap.
        var decidedBy = DecidedBy(from);
        var result = action == TelegramCorrectionAction.Approve
            ? await corrections.ApproveAsync(id, decidedBy, cancellationToken)
            : await corrections.RejectAsync(id, null, decidedBy, cancellationToken);
        if (result.Outcome != CorrectionOutcome.Ok)
        {
            return result.Message is { Length: > 0 } text ? Truncate(text, 190) : "Fehler – bitte die Admin-Seite nutzen.";
        }

        var current = store.Find(id) ?? request;
        return current.Status switch
        {
            TimeCorrectionStatus.Applied => "Genehmigt",
            TimeCorrectionStatus.Rejected => "Abgelehnt",
            TimeCorrectionStatus.Failed => "Genehmigt, aber nicht in Kimai eingetragen – siehe Nachricht",
            _ => TelegramMessageFactory.BuildCorrectionAlreadyDecided(current),
        };
    }

    /// <summary>
    /// "Bereits entschieden: …" und die Nachricht auf den Stand bringen (falls
    /// sie noch Knöpfe zeigt) - in derselben Reihenfolge wie alle anderen Edits.
    /// </summary>
    private async Task<string> AlreadyDecidedAsync(string id, TimeCorrectionRequest request, CancellationToken cancellationToken)
    {
        var current = await messages.RefreshAsync(id, cancellationToken) ?? request;
        return Truncate(TelegramMessageFactory.BuildCorrectionAlreadyDecided(current), 190);
    }

    /// <summary>Vorname, sonst Benutzername, so steht es als Entscheider am Antrag.</summary>
    private static string DecidedBy(JsonElement from)
    {
        string? Read(string name) => from.ValueKind == JsonValueKind.Object
            && from.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : null;

        return Truncate(Read("first_name") ?? Read("username") ?? "Telegram", MaxDecidedByLength);
    }

    private static long? ReadInt64(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];
}
