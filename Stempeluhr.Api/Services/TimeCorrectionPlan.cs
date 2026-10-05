using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

public enum PlannedStepKind
{
    /// <summary>PATCH von Beginn/Ende des vorhandenen Timesheets.</summary>
    Patch,

    /// <summary>Neues Arbeits-Timesheet (Tätigkeit je nach Art des Antrags).</summary>
    CreateWork,

    /// <summary>Neues Pausen-Timesheet.</summary>
    CreatePause,
}

/// <summary>
/// Ein Schritt in Kimai. <see cref="Name"/> steht in
/// <see cref="TimeCorrectionRequest.AppliedSteps"/>, sobald er erledigt ist.
/// Patch-Schritte setzen nur die gefüllten Zeiten; Anlege-Schritte haben immer
/// Beginn und Ende.
/// </summary>
public sealed record PlannedStep(string Name, PlannedStepKind Kind, DateTimeOffset? Begin, DateTimeOffset? End);

/// <summary>
/// Übersetzt einen Antrag in die Schritte, die ihn in Kimai umsetzen (Tabelle
/// „Arten und Schritte in Kimai“ im Epic). Pur und ohne I/O: Validator und
/// Service benutzen dieselbe Reihenfolge.
/// </summary>
public static class TimeCorrectionPlan
{
    public const string StepShorten = "shorten";
    public const string StepEnd = "end";
    public const string StepTimes = "times";
    public const string StepPause = "pause";
    public const string StepRest = "rest";
    public const string StepWork = "work";
    public const string StepWorkBeforePause = "work1";
    public const string StepWorkAfterPause = "work2";

    public const string AddedShiftDescription = "Nachgetragen (Korrekturantrag)";

    public static IReadOnlyList<PlannedStep> Steps(TimeCorrectionRequest request)
    {
        switch (request.Kind)
        {
            case TimeCorrectionKind.AddPause:
            {
                var steps = new List<PlannedStep>
                {
                    new(StepShorten, PlannedStepKind.Patch, null, request.PauseBegin),
                    new(StepPause, PlannedStepKind.CreatePause, request.PauseBegin, request.PauseEnd),
                };
                // Die Rest-Arbeit entfällt, wenn die Pause am alten Ende endet.
                if (request.Original?.End is { } oldEnd && request.PauseEnd < oldEnd)
                {
                    steps.Add(new(StepRest, PlannedStepKind.CreateWork, request.PauseEnd, oldEnd));
                }
                return steps;
            }
            case TimeCorrectionKind.SetEnd:
                return [new(StepEnd, PlannedStepKind.Patch, null, request.End)];
            case TimeCorrectionKind.ChangeTimes:
                return [new(StepTimes, PlannedStepKind.Patch, request.Begin, request.End)];
            case TimeCorrectionKind.AddShift when request.PauseBegin is not null && request.PauseEnd is not null:
                return
                [
                    new(StepWorkBeforePause, PlannedStepKind.CreateWork, request.Begin, request.PauseBegin),
                    new(StepPause, PlannedStepKind.CreatePause, request.PauseBegin, request.PauseEnd),
                    new(StepWorkAfterPause, PlannedStepKind.CreateWork, request.PauseEnd, request.End),
                ];
            case TimeCorrectionKind.AddShift:
                return [new(StepWork, PlannedStepKind.CreateWork, request.Begin, request.End)];
            default:
                return [];
        }
    }
}

/// <summary>Anlege-Schritte: Zielkriterien, an denen ein schon vorhandener Eintrag erkannt wird.</summary>
public static class TimeCorrectionTargets
{
    /// <summary>Projekt/Aktivität und Beschreibung, auf die ein Anlege-Schritt bucht; null, wenn sie nicht (mehr) eingerichtet sind.</summary>
    public static (KimaiTimesheetTarget Target, string? Description)? Resolve(
        PlannedStep step, TimeCorrectionRequest request, RuntimeSettings settings, EmployeeSettings employee)
    {
        if (step.Kind == PlannedStepKind.CreatePause)
        {
            return WorkTargetResolver.ResolvePause(settings, employee) is { } pause ? (pause, pause.Description) : null;
        }

        if (request.Kind == TimeCorrectionKind.AddPause)
        {
            // Rest-Arbeit: wie das ursprüngliche Timesheet.
            var original = request.Original!;
            return (new KimaiTimesheetTarget(original.ProjectId, original.ActivityId, original.Description ?? "", original.Billable, null, null),
                original.Description);
        }

        return WorkTargetResolver.Resolve(settings, employee, request.TaskId) is { } work
            ? (work, TimeCorrectionPlan.AddedShiftDescription)
            : null;
    }

    /// <summary>
    /// Ist <paramref name="entry"/> genau das, was <paramref name="step"/> anlegt (Beginn, Ende, Aktivität)?
    /// Ein bloß zeitgleicher Eintrag auf anderer Aktivität gehört nicht dazu.
    /// </summary>
    public static bool IsCreatedBy(KimaiTimesheetEntryDto entry, PlannedStep step, int activityId)
        => entry.Begin == step.Begin && entry.End == step.End && entry.ActivityId == activityId;
}

/// <summary>
/// Gruppiert Einträge zu Schichten: Eine Schicht endet, wenn bis zum nächsten
/// Eintrag mindestens <see cref="WorkTimeLimitCalculator.ShiftRest"/> vergehen
/// (dieselbe Lücke wie bei der Arbeitszeit-Warnung). Nachtschichten über
/// Mitternacht bleiben deshalb eine Schicht.
/// </summary>
public static class ShiftGrouper
{
    /// <summary>Schichten in zeitlicher Reihenfolge; ein laufender Eintrag reicht bis <paramref name="now"/>.</summary>
    public static IReadOnlyList<IReadOnlyList<T>> Group<T>(
        IEnumerable<T> items,
        Func<T, DateTimeOffset> begin,
        Func<T, DateTimeOffset?> end,
        DateTimeOffset now)
    {
        var shifts = new List<IReadOnlyList<T>>();
        List<T>? current = null;
        var shiftEnd = DateTimeOffset.MinValue;

        foreach (var item in items.OrderBy(begin))
        {
            var itemBegin = begin(item);
            if (current is null || itemBegin - shiftEnd >= WorkTimeLimitCalculator.ShiftRest)
            {
                current = [];
                shifts.Add(current);
                shiftEnd = itemBegin;
            }

            current.Add(item);
            var itemEnd = end(item) ?? now;
            if (itemEnd > shiftEnd)
            {
                shiftEnd = itemEnd;
            }
        }

        return shifts;
    }
}
