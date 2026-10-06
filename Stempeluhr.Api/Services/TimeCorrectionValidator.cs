using System.Globalization;
using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

/// <summary>
/// Regeln für Korrekturanträge (Epic „Korrekturanträge“). Statisch und ohne
/// I/O wie <see cref="HoursOverviewCalculator"/>; geprüft wird beim Absenden
/// und noch einmal beim Anwenden. Gibt eine deutsche Fehlermeldung zurück oder
/// null, wenn der Antrag zulässig ist.
/// </summary>
public static class TimeCorrectionValidator
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(31);
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaxShift = TimeSpan.FromHours(16);
    public static readonly TimeSpan MaxPause = TimeSpan.FromHours(4);

    /// <summary>Wie viele Zeichen ein Kommentar höchstens hat.</summary>
    public const int MaxCommentLength = 300;

    /// <param name="request">Der Antrag; für Arten mit Timesheet mit <see cref="TimeCorrectionRequest.Original"/>.</param>
    /// <param name="timesheets">
    /// Timesheets des Mitarbeiters rund um den Zeitraum des Antrags. Das
    /// betroffene Timesheet zählt nicht als Überlappung, beim Anwenden auch
    /// keine Einträge, die genau dem entsprechen, was der Antrag selbst anlegt
    /// (gleicher Beginn, gleiches Ende, gleiche Aktivität; Fortsetzung nach
    /// einem Teilfehler).
    /// </param>
    /// <param name="openRequests">Offene Anträge; der Antrag selbst wird ignoriert.</param>
    /// <param name="applying">
    /// True beim Anwenden: Die 31 Tage zählen ab dem Absenden (eine späte
    /// Genehmigung soll den Antrag nicht verfallen lassen), und schon
    /// angelegte eigene Schritte sind keine Überlappung. Beim Absenden hat der
    /// Antrag noch nichts angelegt.
    /// </param>
    public static string? Validate(
        TimeCorrectionRequest request,
        IReadOnlyCollection<KimaiTimesheetEntryDto> timesheets,
        IReadOnlyCollection<TimeCorrectionRequest> openRequests,
        DateTimeOffset now,
        TimeZoneInfo timeZone,
        RuntimeSettings settings,
        EmployeeSettings employee,
        bool applying = false)
    {
        if (request.Comment is { Length: > MaxCommentLength })
        {
            return $"Der Kommentar darf höchstens {MaxCommentLength} Zeichen lang sein.";
        }

        var error = request.Kind switch
        {
            TimeCorrectionKind.AddPause => ValidateAddPause(request, settings, timeZone),
            TimeCorrectionKind.SetEnd => ValidateSetEnd(request, timeZone),
            TimeCorrectionKind.AddShift => ValidateAddShift(request, settings),
            TimeCorrectionKind.ChangeTimes => ValidateChangeTimes(request),
            _ => "Unbekannte Art der Korrektur.",
        };
        if (error is not null)
        {
            return error;
        }

        if (TouchedTimes(request).Any(time => time > now + FutureTolerance))
        {
            return "Zeiten in der Zukunft sind nicht erlaubt.";
        }

        var ageReference = applying ? request.CreatedAt : now;
        if (TouchedTimes(request).Append(request.Original?.Begin ?? ageReference).Any(time => time < ageReference - MaxAge))
        {
            return "Korrekturen sind nur für die letzten 31 Tage möglich.";
        }

        if (request.TimesheetId is { } timesheetId
            && openRequests.Any(other => other.IsOpen && other.Id != request.Id && other.TimesheetId == timesheetId))
        {
            return "Für diesen Eintrag gibt es schon einen offenen Antrag.";
        }

        return FindOverlap(request, timesheets, openRequests, now, timeZone, settings, employee, applying);
    }

    private static string? ValidateAddPause(TimeCorrectionRequest request, RuntimeSettings settings, TimeZoneInfo timeZone)
    {
        // Anders als die übrigen Arten auch für einen laufenden Eintrag.
        if (request.TimesheetId is null || request.Original is null)
        {
            return "Der Eintrag fehlt.";
        }

        var original = request.Original!;
        if (settings.PauseActivityId is null)
        {
            return "Die Pausen-Aktivität ist nicht eingerichtet.";
        }

        if (original.ActivityId == settings.PauseActivityId)
        {
            return "Eine Pause kann nur in einen Arbeitseintrag eingetragen werden.";
        }

        if (request.PauseBegin is not { } pauseBegin || request.PauseEnd is not { } pauseEnd)
        {
            return "Beginn und Ende der Pause fehlen.";
        }

        if (PauseError(pauseBegin, pauseEnd) is { } pauseError)
        {
            return pauseError;
        }

        // Der Eintrag muss vor der Pause noch Arbeit behalten: Beginn gleich
        // Pausenbeginn würde ihn auf Länge 0 kürzen.
        if (original.End is not { } originalEnd)
        {
            // Läuft der Eintrag noch, begrenzt nur "nicht in der Zukunft" das
            // Pausenende (gemeinsame Prüfung in Validate).
            return pauseBegin <= original.Begin
                ? $"Die Pause muss nach dem Beginn des Eintrags ({Format(original.Begin, timeZone)}) liegen."
                : null;
        }

        if (pauseBegin <= original.Begin || pauseEnd > originalEnd)
        {
            return $"Die Pause muss innerhalb des Eintrags ({Format(original.Begin, timeZone)}–{Format(originalEnd, timeZone)}) liegen.";
        }

        return null;
    }

    private static string? ValidateSetEnd(TimeCorrectionRequest request, TimeZoneInfo timeZone)
    {
        if (RequireStoppedTimesheet(request) is { } missing)
        {
            return missing;
        }

        var original = request.Original!;
        if (request.End is not { } end)
        {
            return "Das Ende fehlt.";
        }

        if (end <= original.Begin)
        {
            return "Das Ende muss nach dem Beginn liegen.";
        }

        return end < original.End
            ? null
            : $"Das neue Ende muss vor dem bisherigen Ende ({Format(original.End!.Value, timeZone)}) liegen.";
    }

    private static string? ValidateAddShift(TimeCorrectionRequest request, RuntimeSettings settings)
    {
        if (request.Begin is not { } begin || request.End is not { } end)
        {
            return "Beginn und Ende der Schicht fehlen.";
        }

        if (end <= begin)
        {
            return "Das Ende muss nach dem Beginn liegen.";
        }

        if (end - begin > MaxShift)
        {
            return "Eine Schicht darf höchstens 16 Stunden dauern.";
        }

        if (request.PauseBegin is null && request.PauseEnd is null)
        {
            return null;
        }

        if (request.PauseBegin is not { } pauseBegin || request.PauseEnd is not { } pauseEnd)
        {
            return "Beginn und Ende der Pause gehören zusammen.";
        }

        if (settings.PauseActivityId is null)
        {
            return "Die Pausen-Aktivität ist nicht eingerichtet.";
        }

        if (PauseError(pauseBegin, pauseEnd) is { } pauseError)
        {
            return pauseError;
        }

        return pauseBegin > begin && pauseEnd < end
            ? null
            : "Die Pause muss innerhalb der Schicht liegen.";
    }

    private static string? ValidateChangeTimes(TimeCorrectionRequest request)
    {
        if (RequireStoppedTimesheet(request) is { } missing)
        {
            return missing;
        }

        if (request.Begin is null && request.End is null)
        {
            return "Es wurde keine Änderung angegeben.";
        }

        var original = request.Original!;
        var begin = request.Begin ?? original.Begin;
        var end = request.End ?? original.End!.Value;
        if (end <= begin)
        {
            return "Das Ende muss nach dem Beginn liegen.";
        }

        return end - begin > MaxShift ? "Ein Eintrag darf höchstens 16 Stunden dauern." : null;
    }

    private static string? RequireStoppedTimesheet(TimeCorrectionRequest request)
    {
        if (request.TimesheetId is null || request.Original is null)
        {
            return "Der Eintrag fehlt.";
        }

        return request.Original.End is null
            ? "Der Eintrag läuft noch. Bitte erst ausstempeln."
            : null;
    }

    private static string? PauseError(DateTimeOffset pauseBegin, DateTimeOffset pauseEnd)
    {
        if (pauseEnd <= pauseBegin)
        {
            return "Das Ende der Pause muss nach ihrem Beginn liegen.";
        }

        return pauseEnd - pauseBegin > MaxPause ? "Eine Pause darf höchstens 4 Stunden dauern." : null;
    }

    /// <summary>Alle Zeiten, die der Antrag selbst vorgibt (ohne die des Originals).</summary>
    private static IEnumerable<DateTimeOffset> TouchedTimes(TimeCorrectionRequest request)
    {
        foreach (var time in new[] { request.Begin, request.End, request.PauseBegin, request.PauseEnd })
        {
            if (time is { } value)
            {
                yield return value;
            }
        }
    }

    /// <summary>
    /// Zeitraum, den der Antrag neu belegt: Pause samt Rest-Arbeit bis zum alten
    /// Ende (der Eintrag selbst wird nur gekürzt; bei einem laufenden Eintrag
    /// reicht die Rest-Arbeit bis jetzt), die ganze nachgetragene
    /// Schicht, der geänderte Eintrag. Nur Kürzen (Ende setzen) belegt nichts Neues.
    /// </summary>
    private static (DateTimeOffset Begin, DateTimeOffset End)? ClaimedRange(TimeCorrectionRequest request, DateTimeOffset now)
    {
        return request.Kind switch
        {
            TimeCorrectionKind.AddPause when request.PauseBegin is { } b && request.PauseEnd is { } pauseEnd
                    && TimeCorrectionPlan.RestEnd(request, now) is { } restEnd
                => (b, restEnd > pauseEnd ? restEnd : pauseEnd),
            TimeCorrectionKind.AddShift when request.Begin is { } b && request.End is { } e => (b, e),
            TimeCorrectionKind.ChangeTimes when request.Original is { End: { } oldEnd } original
                => (request.Begin ?? original.Begin, request.End ?? oldEnd),
            _ => null,
        };
    }

    private static string? FindOverlap(
        TimeCorrectionRequest request,
        IReadOnlyCollection<KimaiTimesheetEntryDto> timesheets,
        IReadOnlyCollection<TimeCorrectionRequest> openRequests,
        DateTimeOffset now,
        TimeZoneInfo timeZone,
        RuntimeSettings settings,
        EmployeeSettings employee,
        bool applying)
    {
        if (ClaimedRange(request, now) is not { } claimed)
        {
            return null;
        }

        // Was der Antrag selbst schon angelegt hat, mit denselben Zielkriterien
        // wie beim Anwenden. Lässt sich ein Schritt nicht auflösen, gilt nichts
        // als sein Werk. Beim Absenden ist ein gleicher Eintrag fremd: sonst
        // ginge derselbe Nachtrag ein zweites Mal durch.
        var created = !applying
            ? []
            : TimeCorrectionPlan.Steps(request)
                .Where(step => step.Kind != PlannedStepKind.Patch)
                .Select(step => (Step: step, Target: TimeCorrectionTargets.Resolve(step, request, settings, employee)?.Target))
                .Where(planned => planned.Target is not null)
                .ToArray();

        // Eine schon laufend gestartete Rest-Arbeit belegt nur bis zu ihrem
        // tatsächlichen Ende: Was der Mitarbeiter danach stempelt, gehört
        // nicht mehr zum Antrag.
        if (created
                .Where(planned => planned.Step.Kind == PlannedStepKind.StartWork)
                .Select(planned => timesheets.FirstOrDefault(entry =>
                    TimeCorrectionTargets.IsCreatedBy(entry, planned.Step, planned.Target!.ActivityId)))
                .FirstOrDefault(entry => entry is not null) is { } startedRest)
        {
            claimed = (claimed.Begin, startedRest.End ?? now);
        }

        foreach (var entry in timesheets)
        {
            if (entry.Id == request.TimesheetId || entry.Begin is not { } entryBegin)
            {
                continue;
            }

            var entryEnd = entry.End ?? now;
            if (entryEnd <= entryBegin
                || created.Any(planned => TimeCorrectionTargets.IsCreatedBy(entry, planned.Step, planned.Target!.ActivityId)))
            {
                continue;
            }

            if (Overlaps(claimed.Begin, claimed.End, entryBegin, entryEnd))
            {
                return $"Der Zeitraum überschneidet sich mit einem anderen Eintrag ({Format(entryBegin, timeZone)}–{(entry.End is { } end ? Format(end, timeZone) : "läuft noch")}).";
            }
        }

        // Ein nachgetragener Eintrag existiert in Kimai erst nach der
        // Genehmigung - zwei offene Anträge für dieselbe Zeit würden sonst
        // doppelt gebucht.
        if (request.Kind == TimeCorrectionKind.AddShift)
        {
            foreach (var other in openRequests)
            {
                if (other.IsOpen && other.Id != request.Id && other.EmployeeId == request.EmployeeId
                    && other is { Kind: TimeCorrectionKind.AddShift, Begin: { } otherBegin, End: { } otherEnd }
                    && Overlaps(claimed.Begin, claimed.End, otherBegin, otherEnd))
                {
                    return "Für diesen Zeitraum gibt es schon einen offenen Antrag.";
                }
            }
        }

        return null;
    }

    /// <summary>Direkt angrenzende Zeiträume überlappen nicht.</summary>
    private static bool Overlaps(DateTimeOffset beginA, DateTimeOffset endA, DateTimeOffset beginB, DateTimeOffset endB)
        => beginA < endB && beginB < endA;

    internal static string Format(DateTimeOffset value, TimeZoneInfo timeZone)
        => TimeZoneInfo.ConvertTime(value, timeZone).ToString("dd.MM. HH:mm", CultureInfo.InvariantCulture);
}
