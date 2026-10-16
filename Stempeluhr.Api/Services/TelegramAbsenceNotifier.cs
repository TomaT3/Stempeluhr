using System.Collections.Concurrent;
using System.Text.Json;
using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

/// <summary>
/// Zeigt Abwesenheitsanträge im Korrektur-Chat mit den Knöpfen Genehmigen und
/// Ablehnen und ersetzt die Nachricht nach jeder Entscheidung durch das
/// Ergebnis ohne Knöpfe. Vom Admin erfasste Abwesenheiten erscheinen nur als
/// Info ohne Knöpfe. Ohne Bot-Token und
/// <see cref="RuntimeSettings.TelegramCorrectionChatId"/> fließt kein
/// Telegram-Verkehr. Fehler werden nur geloggt: der Service fängt jede
/// Ausnahme, eine Entscheidung hängt nie von Telegram ab.
/// </summary>
/// <remarks>
/// Wie beim <see cref="TelegramTimeCorrectionNotifier"/> laufen alle Änderungen
/// an der Nachricht eines Antrags nacheinander unter einer eigenen Sperre pro
/// Antrag, und der Stand wird erst darin gelesen: der Service ruft den
/// Notifier außerhalb seines Antrags-Locks auf, ein langsamer älterer Edit
/// dürfte sonst einen neueren überschreiben.
/// </remarks>
public sealed class TelegramAbsenceNotifier(
    IRuntimeSettingsStore settingsStore,
    TelegramBotApi api,
    AbsenceStore store,
    ILogger<TelegramAbsenceNotifier>? logger = null) : IAbsenceNotifier
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _messageGates = new(StringComparer.Ordinal);

    public async Task OnSubmitted(AbsenceRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = settingsStore.Load();
            if (!settings.TelegramCorrectionsEnabled)
            {
                return;
            }

            // Der Stand aus dem Store: der Antrag kann schon entschieden sein.
            var current = store.Find(request.Id) ?? request;
            var response = await api.SendMessageAsync(
                settings.TelegramBotToken!,
                settings.TelegramCorrectionChatId!.Trim(),
                Render(current, settings, store),
                current.IsAdminEntry ? null : TelegramMessageFactory.BuildAbsenceKeyboard(request.Id),
                cancellationToken);
            if (!response.Ok || response.Result is not { } message
                || !TryReadMessage(message, out var chatId, out var messageId))
            {
                logger?.LogWarning(
                    "Telegram absence message for {Id} was not sent ({StatusCode}): {Description}{Hint}",
                    request.Id, response.StatusCode, response.Description,
                    response.MigrateToChatId is { } newChatId
                        ? $" - the chat was upgraded to a supergroup; set TelegramCorrectionChatId to {newChatId}"
                        : string.Empty);
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
            logger?.LogWarning(ex, "Telegram absence message for {Id} could not be sent", request.Id);
        }
    }

    public async Task OnDecided(AbsenceRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            await RefreshAsync(request.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Telegram absence message for {Id} could not be updated", request.Id);
        }
    }

    /// <summary>
    /// Bringt die Nachricht auf den aktuellen Stand des Antrags (Ergebnis statt
    /// Knöpfe); ein offener Antrag bleibt unverändert. Eine Admin-Erfassung
    /// ohne Nachricht bekommt hier ihre Info-Nachricht. Gibt den gelesenen
    /// Stand zurück (null: unbekannter Antrag).
    /// </summary>
    public async Task<AbsenceRequest?> RefreshAsync(string id, CancellationToken cancellationToken = default)
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
            if (current is { IsAdminEntry: true, TelegramMessageId: null } && current.Status != AbsenceStatus.Pending)
            {
                // Der Text ist schon der aktuelle Stand: kein Edit danach.
                return await SendInfoAsync(current, settings, cancellationToken);
            }
            await EditAsync(current, settings, cancellationToken);
            return current;
        }
    }

    /// <summary>
    /// Tauscht die Knöpfe eines offenen Antrags (Bestätigung oder zurück zu
    /// Genehmigen/Ablehnen). Ist er inzwischen entschieden, zeigt die Nachricht
    /// stattdessen das Ergebnis. Gibt den gelesenen Stand zurück.
    /// </summary>
    public async Task<AbsenceRequest?> ShowButtonsAsync(string id, object keyboard, CancellationToken cancellationToken = default)
    {
        var settings = settingsStore.Load();
        if (!settings.TelegramCorrectionsEnabled)
        {
            return store.Find(id);
        }

        using (await EnterAsync(id, cancellationToken))
        {
            var current = store.Find(id);
            if (current is not { IsUndecided: true, TelegramChatId: { } chatId, TelegramMessageId: { } messageId })
            {
                await EditAsync(current, settings, cancellationToken);
                return current;
            }

            var response = await api.EditMessageReplyMarkupAsync(settings.TelegramBotToken!, chatId, messageId, keyboard, cancellationToken);
            if (!response.Ok && !response.IsNotModified)
            {
                logger?.LogWarning(
                    "Telegram absence buttons for {Id} were not updated ({StatusCode}): {Description}",
                    id, response.StatusCode, response.Description);
            }
            return current;
        }
    }

    /// <summary>
    /// Übernimmt die Nachricht eines (bereits geprüften) Knopf-Drucks, wenn der
    /// Antrag noch keine kennt: Telegram kann die Nachricht zugestellt haben,
    /// während die Antwort auf <c>sendMessage</c> verloren ging (Timeout). Eine
    /// schon bekannte Nachricht bleibt unverändert.
    /// </summary>
    public async Task<AbsenceRequest?> AdoptMessageAsync(
        string id, long chatId, long messageId, CancellationToken cancellationToken = default)
    {
        using (await EnterAsync(id, cancellationToken))
        {
            var current = store.Find(id);
            return current is { TelegramMessageId: null }
                ? store.AttachTelegramMessage(id, chatId, messageId)
                : current;
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

    /// <summary>Sendet die Info einer Admin-Erfassung (ohne Knöpfe) und hält die Nachricht am Antrag fest. Unter der Sperre des Antrags.</summary>
    private async Task<AbsenceRequest> SendInfoAsync(AbsenceRequest request, RuntimeSettings settings, CancellationToken cancellationToken)
    {
        var response = await api.SendMessageAsync(
            settings.TelegramBotToken!,
            settings.TelegramCorrectionChatId!.Trim(),
            Render(request, settings, store, decided: true),
            replyMarkup: null,
            cancellationToken);
        if (!response.Ok || response.Result is not { } message || !TryReadMessage(message, out var chatId, out var messageId))
        {
            logger?.LogWarning(
                "Telegram absence info for {Id} was not sent ({StatusCode}): {Description}",
                request.Id, response.StatusCode, response.Description);
            return request;
        }
        return store.AttachTelegramMessage(request.Id, chatId, messageId) ?? request;
    }

    /// <summary>Ersetzt die Nachricht durch den Stand des Antrags; offene Anträge behalten ihre Knöpfe.</summary>
    private async Task EditAsync(AbsenceRequest? request, RuntimeSettings settings, CancellationToken cancellationToken)
    {
        if (request is not { Status: not AbsenceStatus.Pending, TelegramChatId: { } chatId, TelegramMessageId: { } messageId })
        {
            return;
        }

        var response = await api.EditMessageTextAsync(
            settings.TelegramBotToken!, chatId, messageId, Render(request, settings, store, decided: true), cancellationToken);
        if (!response.Ok && !response.IsNotModified)
        {
            logger?.LogWarning(
                "Telegram absence message for {Id} was not updated ({StatusCode}): {Description}",
                request.Id, response.StatusCode, response.Description);
        }
    }

    internal static string Render(AbsenceRequest request, RuntimeSettings settings, AbsenceStore store, bool decided = false)
    {
        var rest = VacationRest(request, settings, store);
        return decided
            ? TelegramMessageFactory.BuildAbsenceDecision(request, ClockService.ResolveTimezone(null), rest)
            : TelegramMessageFactory.BuildAbsenceRequest(request, rest);
    }

    /// <summary>
    /// Resturlaub je betroffenem Jahr vor und nach diesem Antrag, aus dem
    /// Urlaubskonto des Service (<see cref="VacationEntitlementCalculator"/>):
    /// "vorher" ohne diesen Antrag, "nachher" mit seinen Tagen als verbraucht.
    /// </summary>
    private static IReadOnlyList<TelegramMessageFactory.AbsenceRest>? VacationRest(
        AbsenceRequest request, RuntimeSettings settings, AbsenceStore store)
    {
        if (request.Kind != AbsenceKind.Vacation
            || settings.Employees.FirstOrDefault(candidate => candidate.Id == request.EmployeeId) is not { } employee)
        {
            return null;
        }

        var others = store.List().Where(other => other.Id != request.Id).ToArray();
        return request.Days
            .GroupBy(day => day.Year)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var before = VacationEntitlementCalculator.Calculate(employee, group.Key, others).Remaining;
                return new TelegramMessageFactory.AbsenceRest(group.Key, before, before - group.Count());
            })
            .ToArray();
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
