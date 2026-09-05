namespace Stempeluhr.Api.Services;

/// <summary>
/// Baut den Text für Telegram-Stempel-Benachrichtigungen. Bewusst eine pure
/// static Factory (wie <see cref="HoursOverviewCalculator"/>): ohne I/O,
/// vollständig unit-testbar. Einzige Stelle für Text/Emoji der Nachrichten.
/// </summary>
public static class TelegramMessageFactory
{
    /// <summary>
    /// Aktionen entsprechen den Clock-Aktionen aus <c>KioskClockRequest.Action</c>.
    /// </summary>
    public static string Build(string employeeName, string action, DateTimeOffset stampUtc, TimeZoneInfo timeZone)
    {
        var (emoji, label) = action.ToLowerInvariant() switch
        {
            "start" => ("🟢", "eingestempelt"),
            "stop" => ("🔴", "ausgestempelt"),
            "pauseStart" => ("🟡", "Pause"),
            "pauseEnd" => ("🟢", "Pause beendet"),
            _ => throw new ArgumentException($"Unbekannte Stempelaktion: {action}", nameof(action))
        };

        var localTime = TimeZoneInfo.ConvertTime(stampUtc, timeZone).ToString("HH:mm");
        return $"{emoji} {employeeName} · {label} um {localTime}";
    }
}
