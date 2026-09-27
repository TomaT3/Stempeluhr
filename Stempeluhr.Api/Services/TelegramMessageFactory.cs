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
    /// <paramref name="taskLabel"/> gilt für "start" (Tätigkeit, null = ohne
    /// Angabe) und "switch" (Ziel, null = zurück zur Standard-Tätigkeit).
    /// </summary>
    public static string Build(
        string employeeName,
        string action,
        DateTimeOffset stampUtc,
        TimeZoneInfo timeZone,
        string? taskLabel = null)
    {
        var (emoji, label) = action.ToLowerInvariant() switch
        {
            "start" => ("🟢", string.IsNullOrWhiteSpace(taskLabel)
                ? "eingestempelt"
                : $"eingestempelt auf {taskLabel}"),
            "stop" => ("🔴", "ausgestempelt"),
            "pausestart" => ("🟡", "Pause"),
            "pauseend" => ("🟢", "Pause beendet"),
            "switch" => ("🔄", string.IsNullOrWhiteSpace(taskLabel)
                ? "zurück zur Standard-Tätigkeit"
                : $"wechselt zu {taskLabel}"),
            _ => throw new ArgumentException($"Unbekannte Stempelaktion: {action}", nameof(action))
        };

        var localTime = TimeZoneInfo.ConvertTime(stampUtc, timeZone).ToString("HH:mm");
        return $"{emoji} {employeeName} · {label} um {localTime}";
    }
}
