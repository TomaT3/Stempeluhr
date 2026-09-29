namespace Stempeluhr.Api.Services;

/// <summary>
/// Baut den Text für Telegram-Stempel-Benachrichtigungen. Bewusst eine pure
/// static Factory (wie <see cref="HoursOverviewCalculator"/>): ohne I/O,
/// vollständig unit-testbar. Einzige Stelle für Text/Emoji der Nachrichten.
/// </summary>
public static class TelegramMessageFactory
{
    /// <summary>Haupttätigkeit ohne eigene Bezeichnung.</summary>
    public const string DefaultTaskName = "Standard-Tätigkeit";

    public static string BuildOfflineRejectionSummary(
        RejectedOfflineEvent first, TimeZoneInfo firstZone,
        RejectedOfflineEvent last, TimeZoneInfo lastZone, int count)
    {
        static string Short(string value) => value.Length <= 180 ? value : value[..180] + "…";
        static string Describe(RejectedOfflineEvent entry, TimeZoneInfo zone)
        {
            var action = (entry.Action ?? string.Empty).ToLowerInvariant() switch
            {
                "start" => "Einstempeln", "stop" => "Ausstempeln",
                "pausestart" => "Pausenbeginn", "pauseend" => "Pausenende",
                "switch" => "Tätigkeitswechsel", _ => "Stempeln"
            };
            var local = TimeZoneInfo.ConvertTime(entry.PerformedAt, zone);
            return $"{Short(entry.EmployeeName)} · {action} · {local:dd.MM. HH:mm} {zone.Id}\nGrund: {Short(entry.Message)}";
        }

        return count == 1
            ? $"⚠️ Offline-Stempel nicht übernommen\n{Describe(first, firstZone)}\nBitte in Kimai nachtragen."
            : $"⚠️ {count} Offline-Stempel nicht übernommen\nErster Fall: {Describe(first, firstZone)}\nLetzter Fall: {Describe(last, lastZone)}\nBitte in Kimai nachtragen.";
    }

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
                ? $"zurück zur {DefaultTaskName}"
                : $"wechselt zu {taskLabel}"),
            _ => throw new ArgumentException($"Unbekannte Stempelaktion: {action}", nameof(action))
        };

        var localTime = TimeZoneInfo.ConvertTime(stampUtc, timeZone).ToString("HH:mm");
        return $"{emoji} {employeeName} · {label} um {localTime}";
    }
}
