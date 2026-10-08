using System.Net.Http.Json;
using System.Text.Json;

namespace Stempeluhr.Api.Services;

/// <summary>Antwort der Bot API: <see cref="Result"/> ist nur bei <see cref="Ok"/> gesetzt.</summary>
public sealed record TelegramApiResponse(
    bool Ok, int StatusCode, JsonElement? Result, string? Description, long? MigrateToChatId = null)
{
    /// <summary>Telegram verweigert ein Edit ohne Änderung mit 400 - das Ziel ist dann erreicht.</summary>
    public bool IsNotModified => Description?.Contains("message is not modified", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>
/// Dünner Client für die Telegram Bot API (nur Outbound-HTTPS) für Senden,
/// Bearbeiten, Callback-Antworten und <c>getUpdates</c>. Wirft bei
/// Netzwerkfehlern; ein HTTP-Fehler der API kommt als
/// <see cref="TelegramApiResponse"/> mit <c>Ok = false</c>. Wie
/// <see cref="TelegramNotifier"/> singleton und über die IHttpClientFactory,
/// weil der Aufruf nach dem Request-Ende laufen darf.
/// </summary>
public sealed class TelegramBotApi(IHttpClientFactory httpClientFactory)
{
    /// <summary>Eigener Client für Long-Polling: sein Timeout liegt über dem Poll-Timeout.</summary>
    public const string PollClientName = "TelegramPoll";

    // Fehlende Angaben (kein Offset, keine Knöpfe) weglassen statt null senden.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public Task<TelegramApiResponse> SendMessageAsync(
        string token, string chatId, string text, object? replyMarkup, CancellationToken cancellationToken = default)
        => PostAsync(TelegramNotifier.ClientName, token, "sendMessage",
            new { chat_id = chatId, text, reply_markup = replyMarkup }, cancellationToken);

    /// <summary>Ersetzt den Text und entfernt dabei die Knöpfe.</summary>
    public Task<TelegramApiResponse> EditMessageTextAsync(
        string token, long chatId, long messageId, string text, CancellationToken cancellationToken = default)
        => PostAsync(TelegramNotifier.ClientName, token, "editMessageText",
            new { chat_id = chatId, message_id = messageId, text, reply_markup = new { inline_keyboard = Array.Empty<object>() } },
            cancellationToken);

    public Task<TelegramApiResponse> EditMessageReplyMarkupAsync(
        string token, long chatId, long messageId, object replyMarkup, CancellationToken cancellationToken = default)
        => PostAsync(TelegramNotifier.ClientName, token, "editMessageReplyMarkup",
            new { chat_id = chatId, message_id = messageId, reply_markup = replyMarkup }, cancellationToken);

    public Task<TelegramApiResponse> AnswerCallbackQueryAsync(
        string token, string callbackQueryId, string? text, CancellationToken cancellationToken = default)
        => PostAsync(TelegramNotifier.ClientName, token, "answerCallbackQuery",
            new { callback_query_id = callbackQueryId, text }, cancellationToken);

    /// <summary>Long-Polling nur für Knopf-Drücke; <paramref name="offset"/> bestätigt frühere Updates.</summary>
    public Task<TelegramApiResponse> GetUpdatesAsync(
        string token, long? offset, int timeoutSeconds, CancellationToken cancellationToken = default)
        => PostAsync(PollClientName, token, "getUpdates",
            new { offset, timeout = timeoutSeconds, allowed_updates = new[] { "callback_query" } }, cancellationToken);

    private async Task<TelegramApiResponse> PostAsync(
        string clientName, string token, string method, object payload, CancellationToken cancellationToken)
    {
        // using: Client nach dem Aufruf zurückgeben; die gepoolten Handler
        // gehören der Factory und überleben den Dispose.
        using var client = httpClientFactory.CreateClient(clientName);

        // Token steht im URL-Pfad (Bot-API-Konvention); die Zeichen
        // ([0-9A-Za-z:_-]) sind URL-sicher.
        using var response = await client.PostAsJsonAsync($"/bot{token}/{method}", payload, JsonOptions, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var description = root.TryGetProperty("description", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null;
            JsonElement? result = root.TryGetProperty("result", out var value) ? value.Clone() : null;
            // Wurde eine Gruppe zur Supergruppe, hat sie eine neue Chat-ID; Telegram nennt sie im Fehler.
            long? migrateToChatId = root.TryGetProperty("parameters", out var parameters)
                && parameters.ValueKind == JsonValueKind.Object
                && parameters.TryGetProperty("migrate_to_chat_id", out var migrated)
                && migrated.TryGetInt64(out var newChatId)
                    ? newChatId
                    : null;
            return new TelegramApiResponse(
                response.IsSuccessStatusCode && root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
                (int)response.StatusCode, result, description, migrateToChatId);
        }
        catch (JsonException)
        {
            // Kein JSON (Proxy-Fehlerseite): nur den Status melden.
            return new TelegramApiResponse(false, (int)response.StatusCode, null, body.Length <= 200 ? body : body[..200]);
        }
    }
}
