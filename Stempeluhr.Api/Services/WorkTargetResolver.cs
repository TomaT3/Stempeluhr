using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

/// <summary>
/// Projekt/Aktivität, auf die ein Arbeits-Timesheet gebucht wird: die
/// Standard-Tätigkeit des Mitarbeiters (<see cref="TaskId"/> null) oder eine
/// seiner weiteren Tätigkeiten.
/// </summary>
public sealed record KimaiTimesheetTarget(
    int ProjectId,
    int ActivityId,
    string Description,
    bool Billable,
    string? TaskId,
    string? Label);

/// <summary>
/// Einzige Stelle, die Tätigkeiten auf Kimai-Projekt/Aktivität abbildet - und
/// zurück (Status: welche Tätigkeit läuft gerade?).
/// </summary>
public static class WorkTargetResolver
{
    /// <summary>
    /// Standard-Tätigkeit des Mitarbeiters (eigene Werte, sonst die globalen
    /// Standards) oder null, wenn Projekt/Aktivität nicht konfiguriert sind.
    /// </summary>
    public static KimaiTimesheetTarget? ResolveDefault(RuntimeSettings settings, EmployeeSettings employee)
    {
        var projectId = employee.ProjectId ?? settings.DefaultProjectId;
        var activityId = employee.ActivityId ?? settings.DefaultActivityId;
        if (projectId is null || activityId is null)
        {
            return null;
        }

        return new KimaiTimesheetTarget(
            projectId.Value,
            activityId.Value,
            string.IsNullOrWhiteSpace(employee.Description) ? "Stempeluhr" : employee.Description,
            employee.Billable,
            null,
            null);
    }

    /// <summary>
    /// Pausen-Timesheet: Projekt der Standard-Tätigkeit, Pausen-Aktivität,
    /// nie abrechenbar. Null, wenn Projekt oder Pausen-Aktivität fehlen.
    /// </summary>
    public static KimaiTimesheetTarget? ResolvePause(RuntimeSettings settings, EmployeeSettings employee)
    {
        var projectId = employee.ProjectId ?? settings.DefaultProjectId;
        if (projectId is null || settings.PauseActivityId is null)
        {
            return null;
        }

        return new KimaiTimesheetTarget(projectId.Value, settings.PauseActivityId.Value, "Pause", false, null, null);
    }

    /// <summary>Weitere Tätigkeit des Mitarbeiters oder null (unbekannt/unvollständig).</summary>
    public static KimaiTimesheetTarget? ResolveTask(EmployeeSettings employee, string taskId)
    {
        var task = FindTask(employee, taskId);
        return task is { ProjectId: int projectId, ActivityId: int activityId }
            ? new KimaiTimesheetTarget(projectId, activityId, task.Label, task.Billable, task.Id, task.Label)
            : null;
    }

    /// <summary>
    /// <paramref name="taskId"/> leer = Standard-Tätigkeit, sonst die
    /// genannte weitere Tätigkeit.
    /// </summary>
    public static KimaiTimesheetTarget? Resolve(RuntimeSettings settings, EmployeeSettings employee, string? taskId)
    {
        return string.IsNullOrWhiteSpace(taskId)
            ? ResolveDefault(settings, employee)
            : ResolveTask(employee, taskId);
    }

    /// <summary>
    /// Umkehrung für den Status: die weitere Tätigkeit, auf die ein
    /// Timesheet mit diesem Projekt/dieser Aktivität gebucht ist, oder null
    /// (Standard-Tätigkeit oder fremde Buchung).
    /// </summary>
    public static EmployeeTaskSettings? MatchTask(EmployeeSettings employee, int? projectId, int? activityId)
    {
        if (projectId is null || activityId is null)
        {
            return null;
        }

        return (employee.Tasks ?? []).FirstOrDefault(task =>
            task.ProjectId == projectId && task.ActivityId == activityId);
    }

    /// <summary>
    /// Ziel für das Fortsetzen der Arbeit nach einer Pause: die Tätigkeit des
    /// Timesheets vor der Pause, wenn es eine weitere Tätigkeit war, sonst
    /// die Standard-Tätigkeit.
    /// </summary>
    public static KimaiTimesheetTarget? ResolveResume(
        RuntimeSettings settings,
        EmployeeSettings employee,
        KimaiRecentTimesheetDto? beforePause)
    {
        var task = beforePause is null ? null : MatchTask(employee, beforePause.ProjectId, beforePause.ActivityId);
        return (task is null ? null : ResolveTask(employee, task.Id)) ?? ResolveDefault(settings, employee);
    }

    /// <summary>
    /// True when the running WORK sheet already books on <paramref name="target"/>
    /// (both null = default task). Callers rule out pause/clocked out first.
    /// </summary>
    public static bool IsRunningOn(ClockStatusDto running, KimaiTimesheetTarget target)
    {
        return string.Equals(running.ActiveTaskId, target.TaskId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Name für Meldungen: Label der Tätigkeit oder „Standard-Taetigkeit“.</summary>
    public static string DisplayName(KimaiTimesheetTarget target)
    {
        return target.TaskId is null ? "Standard-Taetigkeit" : target.Label ?? "Taetigkeit";
    }

    private static EmployeeTaskSettings? FindTask(EmployeeSettings employee, string taskId)
    {
        return (employee.Tasks ?? []).FirstOrDefault(task =>
            string.Equals(task.Id, taskId, StringComparison.OrdinalIgnoreCase));
    }
}
