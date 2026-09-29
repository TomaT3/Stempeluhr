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

    /// <summary>
    /// Eine Tabelle für alle Nachrichten: Emoji und Live-Text (abhängig von der
    /// Tätigkeit) sowie das Substantiv für Offline-Warnungen.
    /// </summary>
    private static readonly Dictionary<string, (string Emoji, Func<string?, string> Label, string Noun)> Actions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["start"] = ("🟢", task => string.IsNullOrWhiteSpace(task)
                ? "eingestempelt"
                : $"eingestempelt auf {task}", "Einstempeln"),
            ["stop"] = ("🔴", _ => "ausgestempelt", "Ausstempeln"),
            ["pausestart"] = ("🟡", _ => "Pause", "Pausenbeginn"),
            ["pauseend"] = ("🟢", _ => "Pause beendet", "Pausenende"),
            ["switch"] = ("🔄", task => string.IsNullOrWhiteSpace(task)
                ? $"zurück zur {DefaultTaskName}"
                : $"wechselt zu {task}", "Tätigkeitswechsel"),
        };

    public static string BuildOfflineRejectionSummary(
        RejectedOfflineEvent first, TimeZoneInfo firstZone,
        RejectedOfflineEvent last, TimeZoneInfo lastZone, int count)
    {
        static string Short(string value) => value.Length <= 180 ? value : value[..180] + "…";
        static string Describe(RejectedOfflineEvent entry, TimeZoneInfo zone)
        {
            // Unbekannte Aktionen sind hier möglich (genau das kann der
            // Ablehnungsgrund sein) und dürfen die Warnung nicht verhindern.
            var action = Actions.TryGetValue(entry.Action ?? string.Empty, out var known) ? known.Noun : "Stempeln";
            var local = TimeZoneInfo.ConvertTime(entry.PerformedAt, zone);
            return $"{Short(entry.EmployeeName)} · {action} · {local:dd.MM. HH:mm} {zone.Id}\nGrund: {Short(entry.Message)}";
        }

        return count == 1
            ? $"⚠️ Offline-Stempel nicht übernommen\n{Describe(first, firstZone)}\nBitte in Kimai nachtragen."
            : $"⚠️ {count} Offline-Stempel nicht übernommen\nErster Fall: {Describe(first, firstZone)}\nLetzter Fall: {Describe(last, lastZone)}\nBitte in Kimai nachtragen.";
    }

    /// <summary>Warnung: länger als <see cref="WorkTimeLimitCalculator.ContinuousLimit"/> ohne Pause.</summary>
    public static string BuildContinuousWorkWarning(
        string employeeName, DateTimeOffset startUtc, int workedSeconds, TimeZoneInfo timeZone)
    {
        var start = TimeZoneInfo.ConvertTime(startUtc, timeZone);
        return $"⚠️ {employeeName} · über {WorkTimeLimitCalculator.ContinuousLimit.TotalHours:0} Std. ohne Pause "
            + $"(ab {start:HH:mm}, {FormatDuration(workedSeconds)} Std.)";
    }

    /// <summary>
    /// Warnung: länger als <see cref="WorkTimeLimitCalculator.ShiftLimit"/> in
    /// einer Schicht. Mit Datum, weil Nachtschichten über Mitternacht gehen.
    /// </summary>
    public static string BuildShiftWorkWarning(
        string employeeName, DateTimeOffset startUtc, int workedSeconds, TimeZoneInfo timeZone)
    {
        var start = TimeZoneInfo.ConvertTime(startUtc, timeZone);
        return $"⚠️ {employeeName} · über {WorkTimeLimitCalculator.ShiftLimit.TotalHours:0} Std. in der Schicht "
            + $"seit {start:dd.MM. HH:mm} ({FormatDuration(workedSeconds)} Std.)";
    }

    private static string FormatDuration(int seconds) => $"{seconds / 3600}:{seconds % 3600 / 60:00}";

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
        if (!Actions.TryGetValue(action, out var known))
        {
            throw new ArgumentException($"Unbekannte Stempelaktion: {action}", nameof(action));
        }

        var localTime = TimeZoneInfo.ConvertTime(stampUtc, timeZone).ToString("HH:mm");
        return $"{known.Emoji} {employeeName} · {known.Label(taskLabel)} um {localTime}";
    }
}
