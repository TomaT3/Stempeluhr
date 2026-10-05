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
public sealed class TelegramTimeCorrectionNotifier(
    IRuntimeSettingsStore settingsStore,
    TelegramBotApi api,
    TimeCorrectionStore store,
    ILogger<TelegramTimeCorrectionNotifier>? logger = null) : ITimeCorrectionNotifier
{
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
            await EditAsync(store.AttachTelegramMessage(request.Id, chatId, messageId), settings, cancellationToken);
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
            var settings = settingsStore.Load();
            if (!settings.TelegramCorrectionsEnabled)
            {
                return;
            }

            // Der frische Stand: die Nachricht kann erst nach der Entscheidung
            // angehängt worden sein, und ein neuerer Schritt (Retry) zählt.
            await EditAsync(store.Find(request.Id) ?? request, settings, cancellationToken);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Telegram correction message for {Id} could not be updated", request.Id);
        }
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
