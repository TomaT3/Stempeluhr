namespace Stempeluhr.Api.Services;

public interface ITelegramNotifier
{
    /// <summary>
    /// Sendet eine Stempel-Benachrichtigung in den konfigurierten Telegram-Chat.
    /// Wird fire-and-forget aus dem ClockService aufgerufen: wirft nie, Fehler
    /// werden nur geloggt (Stempeln darf nie an Telegram hängen). Ohne
    /// Konfiguration (Token/Chat-ID) ist der Aufruf ein No-op.
    /// <paramref name="taskLabel"/> nennt beim Einstempeln die Tätigkeit
    /// (null = ohne Angabe) und beim Tätigkeitswechsel das Ziel (null =
    /// zurück zur Standard-Tätigkeit).
    /// </summary>
    Task SendStampNotificationAsync(
        string employeeName,
        string action,
        DateTimeOffset stampUtc,
        TimeZoneInfo timeZone,
        string? taskLabel = null);
}
