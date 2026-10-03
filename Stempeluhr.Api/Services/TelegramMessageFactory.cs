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

    /// <summary>One message per terminal and check: new alarms and all-clears.</summary>
    public static string BuildTerminalAlert(
        string terminalId, IEnumerable<TerminalAlertChange> changes, TerminalHealthReport? report,
        DateTimeOffset now, TimeZoneInfo timeZone)
    {
        var lines = changes.Select(change => change.Recovered
            ? $"✅ {DescribeTerminalRecovery(change.Condition, change.Since, report, now, timeZone)}"
            : $"🔴 {DescribeTerminalProblem(change.Condition, change.Since, report, now, timeZone)}");
        return string.Join('\n', lines.Prepend($"🖥️ Terminal {terminalId}"));
    }

    /// <summary>Also shown on the admin status page. A null report: never reported since the start.</summary>
    public static string DescribeTerminalProblem(
        TerminalCondition condition, DateTimeOffset since, TerminalHealthReport? report,
        DateTimeOffset now, TimeZoneInfo timeZone)
    {
        var at = TerminalTime(since, now, timeZone);
        return condition switch
        {
            TerminalCondition.Unreachable when report is null => $"Hat sich seit dem Serverstart ({at}) nicht gemeldet.",
            TerminalCondition.Unreachable => $"Meldet sich nicht (letzter Bericht {at}). Pi hängt, ohne Strom oder ohne Netz.",
            TerminalCondition.PageHung => $"Kiosk-Seite hängt seit {at}, der Agent läuft.",
            TerminalCondition.QueueBacklog => $"Offline-Stempel seit {at} nicht übertragen ({report?.Pending:0} offen).",
            TerminalCondition.Power => $"Stromversorgung: {string.Join(", ", TerminalHealthEvaluator.DescribeThrottling(report?.ThrottledFlags).Where(flag => flag.EndsWith("(jetzt)")))} seit {at}. Netzteil prüfen.",
            TerminalCondition.Temperature => $"Temperatur {report?.TemperatureC:0} °C seit {at}.",
            TerminalCondition.Memory => $"Nur {report?.AvailableMemoryKb / 1024:0} MB Arbeitsspeicher frei (seit {at}).",
            TerminalCondition.Cpu => $"CPU-Last {report?.CpuPercent:0} % seit {at}.",
            TerminalCondition.Disk => $"Nur {report?.DiskFreeMb:0} MB Speicherplatz frei.",
            _ => throw new ArgumentOutOfRangeException(nameof(condition), condition, null),
        };
    }

    private static string DescribeTerminalRecovery(
        TerminalCondition condition, DateTimeOffset since, TerminalHealthReport? report,
        DateTimeOffset now, TimeZoneInfo timeZone) => condition switch
    {
        TerminalCondition.Unreachable =>
            $"Wieder erreichbar (ohne Bericht {TerminalTime(since, now, timeZone)}–{TerminalTime(now, now, timeZone)}"
            + report?.UptimeSeconds switch
            {
                null => ").",
                // Booted after the last report: the Pi was restarted.
                var uptime when uptime < (now - since).TotalSeconds => ", Pi wurde neu gestartet).",
                _ => ", Pi lief durch: Netz oder Tailscale prüfen).",
            },
        TerminalCondition.PageHung => "Kiosk-Seite reagiert wieder.",
        TerminalCondition.QueueBacklog => "Offline-Stempel übertragen.",
        TerminalCondition.Power => "Stromversorgung wieder normal.",
        TerminalCondition.Temperature => $"Temperatur wieder normal ({report?.TemperatureC:0} °C).",
        TerminalCondition.Memory => "Arbeitsspeicher wieder ausreichend.",
        TerminalCondition.Cpu => "CPU-Last wieder normal.",
        TerminalCondition.Disk => "Speicherplatz wieder ausreichend.",
        _ => throw new ArgumentOutOfRangeException(nameof(condition), condition, null),
    };

    /// <summary>With the date when it is not today: outages can span midnight.</summary>
    private static string TerminalTime(DateTimeOffset value, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        var local = TimeZoneInfo.ConvertTime(value, timeZone);
        return local.Date == TimeZoneInfo.ConvertTime(now, timeZone).Date ? $"{local:HH:mm}" : $"{local:dd.MM. HH:mm}";
    }
}
