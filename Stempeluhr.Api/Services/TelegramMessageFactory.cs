using Stempeluhr.Api.Models;

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

    /// <summary>
    /// Höchstlänge von <c>sendMessage</c>. Telegram zählt Zeichen, .NET
    /// UTF-16-Einheiten - ein Emoji zählt hier doppelt, die Grenze hält also.
    /// </summary>
    public const int MaxMessageLength = 4096;

    /// <summary>Höchstens so viele Stempelzeilen pro Nachtrags-Nachricht.</summary>
    public const int OfflineStampNoticeLineLimit = 15;

    /// <summary>Namen und Tätigkeiten sind im Admin nicht begrenzt.</summary>
    private const int OfflineStampNameLimit = 80;

    /// <summary>
    /// Nachgetragene Offline-Stempel eines Mitarbeiters aus einer Runde. Ein
    /// Stempel liest sich wie live, mehrere stehen unter einer Kopfzeile. Die
    /// Zeit ist die des Stempels am Terminal, mit Datum, wenn sie nicht auf
    /// den lokalen heutigen Tag fällt (Nachtschicht, langer Ausfall). Gekürzte
    /// Namen und Tätigkeiten halten jede Zeile unter etwa 130 Zeichen; mit
    /// höchstens <see cref="OfflineStampNoticeLineLimit"/> Zeilen, Kopfzeile
    /// und Restzähler bleibt die Nachricht weit unter <see cref="MaxMessageLength"/>.
    /// </summary>
    public static string BuildOfflineStampNotice(
        string employeeName, IReadOnlyList<AppliedOfflineStamp> stamps, TimeZoneInfo timeZone, DateTimeOffset now)
    {
        static string? Clip(string? value) =>
            value is null || value.Length <= OfflineStampNameLimit ? value : value[..(OfflineStampNameLimit - 1)] + "…";
        employeeName = Clip(employeeName)!;
        var today = TimeZoneInfo.ConvertTime(now, timeZone).Date;
        (string Emoji, string Text) Describe(AppliedOfflineStamp stamp)
        {
            // Der Nachtrag normalisiert die Aktion vorher; ein unbekannter
            // Wert darf die Meldung trotzdem nicht verhindern.
            var (emoji, label) = Actions.TryGetValue(stamp.Action, out var known)
                ? (known.Emoji, known.Label(Clip(stamp.TaskLabel)))
                : ("🕒", "gestempelt");
            var local = TimeZoneInfo.ConvertTime(stamp.PerformedAt, timeZone);
            var when = local.Date == today ? $"um {local:HH:mm}" : $"am {local:dd.MM.} um {local:HH:mm}";
            return (emoji, $"{label} {when}");
        }

        if (stamps.Count == 1)
        {
            var (emoji, text) = Describe(stamps[0]);
            return $"{emoji} {employeeName} · {text} (nachgetragen)";
        }

        var lines = stamps.Take(OfflineStampNoticeLineLimit).Select(Describe)
            .Select(line => $"{line.Emoji} {line.Text}")
            .Prepend($"📥 {employeeName} · {stamps.Count} Stempel nachgetragen");
        if (stamps.Count > OfflineStampNoticeLineLimit)
        {
            lines = lines.Append($"… und {stamps.Count - OfflineStampNoticeLineLimit} weitere");
        }
        return string.Join('\n', lines);
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

    // ---- Korrekturanträge ----

    private static readonly string[] WeekdayNames = ["So", "Mo", "Di", "Mi", "Do", "Fr", "Sa"];

    /// <summary>
    /// Neuer Antrag mit Art, Eintrag, Vorher → Nachher und Kommentar. Alle
    /// Zeiten in der Zeitzone des Mitarbeiters, mit Wochentag und Datum:
    /// Nachtschichten gehen über Mitternacht. <paramref name="originalIsPause"/>:
    /// der betroffene Eintrag ist eine Pause, keine Arbeit.
    /// </summary>
    public static string BuildCorrectionRequest(
        TimeCorrectionRequest request, TimeZoneInfo timeZone, string? taskLabel = null, bool originalIsPause = false)
    {
        string Zoned(DateTimeOffset value) => FormatCorrectionTime(value, timeZone);
        string Range(DateTimeOffset begin, DateTimeOffset? end) => FormatCorrectionRange(begin, end, timeZone);
        var entryLabel = originalIsPause ? "Pause" : "Schicht";

        var lines = new List<string> { $"📝 Korrekturantrag · {request.EmployeeName}", DescribeCorrectionKind(request.Kind) };
        var original = request.Original;
        switch (request.Kind)
        {
            case TimeCorrectionKind.AddPause:
                if (original is { End: null })
                {
                    // Pause im laufenden Eintrag: nachher steht der Eintrag bis zur Pause, die Pause, dann läuft die Arbeit weiter.
                    lines.Add($"{entryLabel}: {FormatCorrectionTime(original.Begin, timeZone)} – läuft");
                }
                else if (original is not null)
                {
                    lines.Add($"{entryLabel}: {Range(original.Begin, original.End)}");
                }
                if (request.PauseBegin is { } pauseBegin) lines.Add($"Pause: {Range(pauseBegin, request.PauseEnd)}");
                if (original is { End: null } && request.PauseBegin is { } runBegin && request.PauseEnd is { } runEnd)
                {
                    lines.Add($"Danach: {DescribeRunningPauseResult(request, original.Begin, runBegin, runEnd, timeZone)}");
                }
                break;
            case TimeCorrectionKind.SetEnd:
                if (original is not null) lines.Add($"{entryLabel}: {Range(original.Begin, original.End)}");
                lines.Add($"Ende: {(original?.End is { } oldEnd ? Zoned(oldEnd) : "offen")} → {(request.End is { } newEnd ? Zoned(newEnd) : "?")}");
                break;
            case TimeCorrectionKind.AddShift:
                if (request.Begin is { } shiftBegin) lines.Add($"Schicht: {Range(shiftBegin, request.End)}");
                lines.Add($"Tätigkeit: {(string.IsNullOrWhiteSpace(taskLabel) ? DefaultTaskName : taskLabel)}");
                if (request.PauseBegin is { } shiftPauseBegin) lines.Add($"Pause: {Range(shiftPauseBegin, request.PauseEnd)}");
                break;
            default:
                if (original is not null) lines.Add($"{entryLabel}: {Range(original.Begin, original.End)}");
                if (request.Begin is { } changedBegin)
                {
                    lines.Add($"Beginn: {(original is null ? "?" : Zoned(original.Begin))} → {Zoned(changedBegin)}");
                }
                if (request.End is { } changedEnd)
                {
                    lines.Add($"Ende: {(original?.End is { } previousEnd ? Zoned(previousEnd) : "offen")} → {Zoned(changedEnd)}");
                }
                break;
        }

        if (!string.IsNullOrWhiteSpace(request.Comment))
        {
            lines.Add($"Kommentar: {request.Comment}");
        }
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Der Antragstext mit dem Ergebnis statt der Knöpfe (für
    /// <c>editMessageText</c>). Ein offener Antrag bleibt ohne Ergebniszeile.
    /// </summary>
    public static string BuildCorrectionDecision(
        TimeCorrectionRequest request, TimeZoneInfo timeZone, string? taskLabel = null, bool originalIsPause = false)
    {
        var body = BuildCorrectionRequest(request, timeZone, taskLabel, originalIsPause);
        var at = request.DecidedAt is { } decidedAt ? TimeZoneInfo.ConvertTime(decidedAt, timeZone).ToString("dd.MM. HH:mm") : null;
        var by = string.IsNullOrWhiteSpace(request.DecidedBy) ? "Admin" : request.DecidedBy;
        var when = at is null ? string.Empty : $" · {at}";
        var result = request.Status switch
        {
            TimeCorrectionStatus.Applied => $"✅ Genehmigt von {by}{when} – in Kimai eingetragen",
            TimeCorrectionStatus.Rejected => $"❌ Abgelehnt von {by}{when}"
                + (string.IsNullOrWhiteSpace(request.DecisionNote) ? string.Empty : $"\nGrund: {request.DecisionNote}"),
            TimeCorrectionStatus.Failed => $"⚠️ Nicht in Kimai eingetragen: {ShortenCorrectionError(request.Error)} – bitte in Kimai nachtragen"
                + $" (genehmigt von {by}{when})",
            TimeCorrectionStatus.Withdrawn => $"↩️ Zurückgezogen{when}",
            TimeCorrectionStatus.ResolvedManually => $"☑️ Von {by}{when} manuell in Kimai nachgetragen",
            _ => null,
        };
        return result is null ? body : $"{body}\n\n{result}";
    }

    /// <summary>Kurzfassung für <c>answerCallbackQuery</c> bei einem schon entschiedenen Antrag.</summary>
    public static string BuildCorrectionAlreadyDecided(TimeCorrectionRequest request)
    {
        var by = string.IsNullOrWhiteSpace(request.DecidedBy) ? "Admin" : request.DecidedBy;
        var result = request.Status switch
        {
            TimeCorrectionStatus.Applied => $"Genehmigt von {by}",
            TimeCorrectionStatus.Rejected => $"Abgelehnt von {by}",
            TimeCorrectionStatus.Failed => $"Genehmigt von {by}, Kimai hat nicht gebucht",
            TimeCorrectionStatus.Withdrawn => "Zurückgezogen",
            TimeCorrectionStatus.ResolvedManually => $"Manuell erledigt von {by}",
            _ => "Offen",
        };
        return $"Bereits entschieden: {result}";
    }

    /// <summary>Inline-Tastatur eines offenen Antrags: Genehmigen und Ablehnen.</summary>
    public static object BuildCorrectionKeyboard(string id) => new
    {
        inline_keyboard = new[]
        {
            new[]
            {
                new { text = "✅ Genehmigen", callback_data = TelegramCorrectionCallback.Format(TelegramCorrectionAction.AskApprove, id) },
                new { text = "❌ Ablehnen", callback_data = TelegramCorrectionCallback.Format(TelegramCorrectionAction.AskReject, id) },
            },
        },
    };

    /// <summary>Zweiter Schritt: bestätigen oder zurück.</summary>
    public static object BuildCorrectionConfirmKeyboard(string id, bool approve) => new
    {
        inline_keyboard = new[]
        {
            new[]
            {
                new
                {
                    text = approve ? "Ja, genehmigen" : "Ja, ablehnen",
                    callback_data = TelegramCorrectionCallback.Format(
                        approve ? TelegramCorrectionAction.Approve : TelegramCorrectionAction.Reject, id),
                },
                new { text = "Zurück", callback_data = TelegramCorrectionCallback.Format(TelegramCorrectionAction.Back, id) },
            },
        },
    };

    private static string DescribeCorrectionKind(TimeCorrectionKind kind) => kind switch
    {
        TimeCorrectionKind.AddPause => "Pause nachtragen",
        TimeCorrectionKind.SetEnd => "Ausstempeln nachtragen",
        TimeCorrectionKind.AddShift => "Schicht nachtragen",
        _ => "Beginn/Ende ändern",
    };

    /// <summary>"Mo 06.10. 22:00": mit Wochentag, weil Schichten über Mitternacht gehen.</summary>
    private static string FormatCorrectionTime(DateTimeOffset value, TimeZoneInfo timeZone)
    {
        var local = TimeZoneInfo.ConvertTime(value, timeZone);
        return $"{WeekdayNames[(int)local.DayOfWeek]} {local:dd.MM. HH:mm}";
    }

    /// <summary>
    /// "Mo 05.10. 06:00–12:00 · Pause 12:00–12:30 · ab 12:30 (läuft)": der Stand
    /// nach der Genehmigung. Hat der Mitarbeiter inzwischen ausgestempelt
    /// (beobachtetes Ende), steht statt "(läuft)" die Rest-Arbeit bis dahin.
    /// Zeiten am selben Tag wie der Vorgänger ohne Wochentag und Datum.
    /// </summary>
    private static string DescribeRunningPauseResult(
        TimeCorrectionRequest request, DateTimeOffset begin, DateTimeOffset pauseBegin, DateTimeOffset pauseEnd, TimeZoneInfo timeZone)
    {
        string Next(DateTimeOffset previous, DateTimeOffset value)
        {
            var local = TimeZoneInfo.ConvertTime(value, timeZone);
            return TimeZoneInfo.ConvertTime(previous, timeZone).Date == local.Date
                ? local.ToString("HH:mm")
                : FormatCorrectionTime(value, timeZone);
        }

        var parts = new List<string>
        {
            $"{FormatCorrectionTime(begin, timeZone)}–{Next(begin, pauseBegin)}",
            $"Pause {Next(pauseBegin, pauseBegin)}–{Next(pauseBegin, pauseEnd)}",
        };
        parts.Add(TimeCorrectionPlan.EffectiveEnd(request) switch
        {
            null => $"ab {Next(pauseEnd, pauseEnd)} (läuft)",
            { } end when end > pauseEnd => $"ab {Next(pauseEnd, pauseEnd)} bis {Next(pauseEnd, end)}",
            _ => string.Empty,
        });
        return string.Join(" · ", parts.Where(part => part.Length > 0));
    }

    /// <summary>"Mo 06.10. 22:00 – Di 07.10. 06:10"; am selben Tag steht das Ende nur mit der Uhrzeit.</summary>
    private static string FormatCorrectionRange(DateTimeOffset begin, DateTimeOffset? end, TimeZoneInfo timeZone)
    {
        var from = FormatCorrectionTime(begin, timeZone);
        if (end is not { } until)
        {
            return $"{from} – offen";
        }

        var localBegin = TimeZoneInfo.ConvertTime(begin, timeZone);
        var localEnd = TimeZoneInfo.ConvertTime(until, timeZone);
        return localBegin.Date == localEnd.Date
            ? $"{from} – {localEnd:HH:mm}"
            : $"{from} – {FormatCorrectionTime(until, timeZone)}";
    }

    private static string ShortenCorrectionError(string? error)
    {
        var text = string.IsNullOrWhiteSpace(error) ? "unbekannter Fehler" : error.Trim();
        return text.Length <= 300 ? text : text[..300] + "…";
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
