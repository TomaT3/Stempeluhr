using System.Collections.Concurrent;
using System.Text.Json;
using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

/// <summary>
/// Zeigt Korrekturanträge im Korrektur-Chat mit den Knöpfen Genehmigen und
/// Ablehnen und ersetzt die Nachricht nach jeder Entscheidung durch das
/// Ergebnis ohne Knöpfe. Ohne <see cref="RuntimeSettings.TelegramCorrectionChatId"/>
/// fließt kein Telegram-Verkehr. Fehler werden nur geloggt: der Service
/// ruft den Notifier erst nach der Entscheidung auf und fängt jede Ausnahme.
/// </summary>
/// <remarks>
/// Alle Änderungen an der Nachricht eines Antrags (Ergebnis, Knöpfe) laufen
/// nacheinander unter einer eigenen Sperre pro Antrag, und der Stand wird erst
/// darin gelesen. Der Service ruft den Notifier außerhalb seines Antrags-Locks
/// auf - ohne diese Reihenfolge könnte ein langsamer, älterer Edit ("Nicht in
/// Kimai eingetragen") nach einem neueren ("eingetragen") bei Telegram ankommen
/// und ihn überschreiben.
/// </remarks>
public sealed class TelegramTimeCorrectionNotifier(
    IRuntimeSettingsStore settingsStore,
    TelegramBotApi api,
    TimeCorrectionStore store,
    ILogger<TelegramTimeCorrectionNotifier>? logger = null) : ITimeCorrectionNotifier
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _messageGates = new(StringComparer.Ordinal);

    public async Task OnSubmitted(TimeCorrectionRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = settingsStore.Load();
            if (!settings.TelegramCorrectionsEnabled)
            {
                return;
            }

            var response = await api.SendMessageAsync(
                settings.TelegramBotToken!,
                settings.TelegramCorrectionChatId!.Trim(),
                Render(request, settings, decided: false),
                TelegramMessageFactory.BuildCorrectionKeyboard(request.Id),
                cancellationToken);
            if (!response.Ok || response.Result is not { } message
                || !TryReadMessage(message, out var chatId, out var messageId))
            {
                logger?.LogWarning(
                    "Telegram correction message for {Id} was not sent ({StatusCode}): {Description}",
                    request.Id, response.StatusCode, response.Description);
                return;
            }

            // Unter dem Store-Lock angehängt, damit eine gleichzeitige
            // Entscheidung nicht überschrieben wird. Wurde der Antrag schon
            // entschieden, bevor die Nachricht stand (OnDecided fand noch
            // keine Nachricht), trägt dieser Aufruf das Ergebnis nach.
            using (await EnterAsync(request.Id, cancellationToken))
            {
                await EditAsync(store.AttachTelegramMessage(request.Id, chatId, messageId), settings, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Telegram correction message for {Id} could not be sent", request.Id);
        }
    }

    public async Task OnDecided(TimeCorrectionRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            await RefreshAsync(request.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Telegram correction message for {Id} could not be updated", request.Id);
        }
    }

    /// <summary>
    /// Bringt die Nachricht auf den aktuellen Stand des Antrags (Ergebnis statt
    /// Knöpfe); ein offener Antrag bleibt unverändert. Gibt den gelesenen Stand
    /// zurück (null: unbekannter Antrag).
    /// </summary>
    public async Task<TimeCorrectionRequest?> RefreshAsync(string id, CancellationToken cancellationToken = default)
    {
        var settings = settingsStore.Load();
        if (!settings.TelegramCorrectionsEnabled)
        {
            return store.Find(id);
        }

        using (await EnterAsync(id, cancellationToken))
        {
            // Der frische Stand: die Nachricht kann erst nach der Entscheidung
            // angehängt worden sein, und ein neuerer Schritt (Retry) zählt.
            var current = store.Find(id);
            await EditAsync(current, settings, cancellationToken);
            return current;
        }
    }

    /// <summary>
    /// Tauscht die Knöpfe eines offenen Antrags (Bestätigung oder zurück zu
    /// Genehmigen/Ablehnen). Ist er inzwischen entschieden, zeigt die Nachricht
    /// stattdessen das Ergebnis. Gibt den gelesenen Stand zurück.
    /// </summary>
    public async Task<TimeCorrectionRequest?> ShowButtonsAsync(string id, object keyboard, CancellationToken cancellationToken = default)
    {
        var settings = settingsStore.Load();
        if (!settings.TelegramCorrectionsEnabled)
        {
            return store.Find(id);
        }

        using (await EnterAsync(id, cancellationToken))
        {
            var current = store.Find(id);
            if (current is not { Status: TimeCorrectionStatus.Pending, TelegramChatId: { } chatId, TelegramMessageId: { } messageId })
            {
                await EditAsync(current, settings, cancellationToken);
                return current;
            }

            var response = await api.EditMessageReplyMarkupAsync(settings.TelegramBotToken!, chatId, messageId, keyboard, cancellationToken);
            if (!response.Ok && !response.IsNotModified)
            {
                logger?.LogWarning(
                    "Telegram correction buttons for {Id} were not updated ({StatusCode}): {Description}",
                    id, response.StatusCode, response.Description);
            }
            return current;
        }
    }

    private async Task<IDisposable> EnterAsync(string id, CancellationToken cancellationToken)
    {
        var gate = _messageGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    /// <summary>Ersetzt die Nachricht durch den Stand des Antrags; offene Anträge behalten ihre Knöpfe.</summary>
    private async Task EditAsync(TimeCorrectionRequest? request, RuntimeSettings settings, CancellationToken cancellationToken)
    {
        if (request is not { Status: not TimeCorrectionStatus.Pending, TelegramChatId: { } chatId, TelegramMessageId: { } messageId })
        {
            return;
        }

        var response = await api.EditMessageTextAsync(
            settings.TelegramBotToken!, chatId, messageId, Render(request, settings, decided: true), cancellationToken);
        if (!response.Ok && !response.IsNotModified)
        {
            logger?.LogWarning(
                "Telegram correction message for {Id} was not updated ({StatusCode}): {Description}",
                request.Id, response.StatusCode, response.Description);
        }
    }

    internal static string Render(TimeCorrectionRequest request, RuntimeSettings settings, bool decided)
    {
        var timeZone = ClockService.ResolveTimezone(request.TimeZoneId);
        var employee = settings.Employees.FirstOrDefault(candidate => candidate.Id == request.EmployeeId);
        var taskLabel = request.TaskId is { } taskId && employee is not null
            ? WorkTargetResolver.ResolveTask(employee, taskId)?.Label
            : null;
        var originalIsPause = settings.PauseActivityId is { } pause && request.Original?.ActivityId == pause;
        return decided
            ? TelegramMessageFactory.BuildCorrectionDecision(request, timeZone, taskLabel, originalIsPause)
            : TelegramMessageFactory.BuildCorrectionRequest(request, timeZone, taskLabel, originalIsPause);
    }

    private static bool TryReadMessage(JsonElement message, out long chatId, out long messageId)
    {
        chatId = 0;
        messageId = 0;
        return message.TryGetProperty("message_id", out var id) && id.TryGetInt64(out messageId)
            && message.TryGetProperty("chat", out var chat) && chat.TryGetProperty("id", out var chatIdValue)
            && chatIdValue.TryGetInt64(out chatId);
    }
}
