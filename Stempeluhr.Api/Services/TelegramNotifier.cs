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
        TimeZoneInfo timeZone)
    {
        try
        {
            var settings = settingsStore.Load();
            if (!settings.TelegramEnabled)
            {
                return;
            }

            var text = TelegramMessageFactory.Build(employeeName, action, stampUtc, timeZone);

            // using: Client nach dem Send zurückgeben; die gepoolten Handler
            // gehören der Factory und überleben den Dispose.
            using var client = httpClientFactory.CreateClient(ClientName);

            // Token steht im URL-Pfad (Bot-API-Konvention); die Zeichen
            // ([0-9A-Za-z:_-]) sind URL-sicher.
            var response = await client.PostAsJsonAsync(
                $"/bot{settings.TelegramBotToken}/sendMessage",
                new { chat_id = settings.TelegramChatId, text });

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                logger?.LogWarning(
                    "Telegram sendMessage failed ({StatusCode}): {Body}",
                    (int)response.StatusCode,
                    body);
            }
        }
        catch (Exception ex)
        {
            // Best effort: nie werfen, damit der Stempelvorgang nicht an
            // Telegram hängt. Kein Retry -> kein Doppelversand.
            logger?.LogWarning(ex, "Telegram notification could not be sent");
        }
    }
}
