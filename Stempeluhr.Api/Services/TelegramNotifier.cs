using System.Net.Http.Json;
using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

/// <summary>
/// Postet Stempel-Benachrichtigungen über die Telegram Bot API
/// (sendMessage) in den konfigurierten Chat. Nur Outbound-HTTPS, kein
/// Webhook. Basisadresse kommt aus der DI (Program.cs), damit Tests die
/// Requests über einen Stub-Handler abfangen können.
/// </summary>
public sealed class TelegramNotifier(
    IRuntimeSettingsStore settingsStore,
    HttpClient httpClient,
    ILogger<TelegramNotifier>? logger = null) : ITelegramNotifier
{
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

            // Token steht im URL-Pfad (Bot-API-Konvention); die Zeichen
            // ([0-9A-Za-z:_-]) sind URL-sicher.
            var response = await httpClient.PostAsJsonAsync(
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
