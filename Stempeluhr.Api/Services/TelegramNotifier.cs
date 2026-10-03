using System.Net.Http.Json;
using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

/// <summary>
/// Postet Stempel-Benachrichtigungen über die Telegram Bot API
/// (sendMessage) in den konfigurierten Chat. Nur Outbound-HTTPS, kein
/// Webhook. Singleton-registriert: die Notify-Task läuft bewusst nach dem
/// Request-Ende (fire-and-forget), daher darf kein scope-gebundener
/// HttpClient hängen - der Client wird pro Sendung über die (singleton)
/// IHttpClientFactory erzeugt, die die Handler selbst verwaltet.
/// </summary>
public sealed class TelegramNotifier(
    IRuntimeSettingsStore settingsStore,
    IHttpClientFactory httpClientFactory,
    ILogger<TelegramNotifier>? logger = null) : ITelegramNotifier
{
    /// <summary>Name des konfigurierten HttpClient (siehe Program.cs).</summary>
    public const string ClientName = "Telegram";

    public async Task SendStampNotificationAsync(
        string employeeName,
        string action,
        DateTimeOffset stampUtc,
        TimeZoneInfo timeZone,
        string? taskLabel = null)
    {
        try
        {
            var text = TelegramMessageFactory.Build(employeeName, action, stampUtc, timeZone, taskLabel);
            await SendMessageAsync(text);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Telegram notification could not be sent");
        }
    }

    public Task<bool> SendMessageAsync(string text) => SendAsync(text, settings => settings.TelegramChatId);

    public Task<bool> SendAlertAsync(string text) => SendAsync(text, settings => settings.TelegramAlertChat);

    private async Task<bool> SendAsync(string text, Func<RuntimeSettings, string?> chat)
    {
        try
        {
            var settings = settingsStore.Load();
            var chatId = chat(settings);
            if (string.IsNullOrWhiteSpace(settings.TelegramBotToken) || string.IsNullOrWhiteSpace(chatId))
            {
                return false;
            }

            // using: Client nach dem Send zurückgeben; die gepoolten Handler
            // gehören der Factory und überleben den Dispose.
            using var client = httpClientFactory.CreateClient(ClientName);

            // Token steht im URL-Pfad (Bot-API-Konvention); die Zeichen
            // ([0-9A-Za-z:_-]) sind URL-sicher.
            var response = await client.PostAsJsonAsync(
                $"/bot{settings.TelegramBotToken}/sendMessage",
                new { chat_id = chatId, text });

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                logger?.LogWarning(
                    "Telegram sendMessage failed ({StatusCode}): {Body}",
                    (int)response.StatusCode,
                    body);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            // Best effort: nie werfen, damit der Stempelvorgang nicht an
            // Telegram hängt. Der Offline-Warner kann false später erneut
            // versuchen; Live-Stempel bleiben ohne Retry.
            logger?.LogWarning(ex, "Telegram notification could not be sent");
            return false;
        }
    }
}
