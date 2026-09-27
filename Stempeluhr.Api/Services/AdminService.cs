using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

public sealed class AdminService(
    IRuntimeSettingsStore settingsStore,
    IKimaiClient kimai) : IAdminService
{
    public async Task<IReadOnlyCollection<AdminEmployeeStatusDto>> GetEmployeeStatusesAsync(CancellationToken cancellationToken = default)
    {
        var settings = settingsStore.Load();
        var statusTasks = settings.Employees.Select(employee => GetEmployeeStatusAsync(settings, employee, cancellationToken));
        return await Task.WhenAll(statusTasks);
    }

    public bool HasDuplicatePins(IEnumerable<EmployeeSettings> employees)
    {
        return employees
            .Where(employee => !string.IsNullOrWhiteSpace(employee.Pin))
            .GroupBy(employee => employee.Pin!.Trim(), StringComparer.Ordinal)
            .Any(group => group.Count() > 1);
    }

    public bool HasDuplicateNfcCardIds(IEnumerable<EmployeeSettings> employees)
    {
        return employees
            .Select(employee => NfcCardIdNormalizer.Normalize(employee.NfcCardId))
            .Where(cardId => cardId is not null)
            .GroupBy(cardId => cardId, StringComparer.Ordinal)
            .Any(group => group.Count() > 1);
    }

    public string? ValidateTasks(RuntimeSettings settings)
    {
        foreach (var employee in settings.Employees)
        {
            var tasks = employee.Tasks ?? [];
            var name = string.IsNullOrWhiteSpace(employee.DisplayName) ? "Mitarbeiter" : employee.DisplayName;
            if (tasks.Any(task => string.IsNullOrWhiteSpace(task.Label)))
            {
                return $"{name}: Jede weitere Taetigkeit braucht eine Bezeichnung.";
            }

            if (tasks.Any(task => task.ProjectId is null || task.ActivityId is null))
            {
                return $"{name}: Jede weitere Taetigkeit braucht Projekt und Aktivitaet.";
            }

            if (settings.PauseActivityId is not null && tasks.Any(task => task.ActivityId == settings.PauseActivityId))
            {
                return $"{name}: Die Pausen-Aktivitaet kann keine weitere Taetigkeit sein.";
            }

            // The status maps the running sheet back to a task by
            // project + activity - duplicates would make that ambiguous.
            if (tasks.GroupBy(task => (task.ProjectId, task.ActivityId)).Any(group => group.Count() > 1)
                || tasks.Select(task => task.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != tasks.Length)
            {
                return $"{name}: Weitere Taetigkeiten muessen eindeutig sein.";
            }

            var standard = WorkTargetResolver.ResolveDefault(settings, employee);
            if (standard is not null
                && tasks.Any(task => task.ProjectId == standard.ProjectId && task.ActivityId == standard.ActivityId))
            {
                return $"{name}: Eine weitere Taetigkeit entspricht der Standard-Taetigkeit.";
            }
        }

        return null;
    }

    private async Task<AdminEmployeeStatusDto> GetEmployeeStatusAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        CancellationToken cancellationToken)
    {
        if (!employee.IsEnabled)
        {
            return new AdminEmployeeStatusDto(employee.Id, employee.DisplayName, false, null, 0, "clockedOut", "Inaktiv", false);
        }

        if (string.IsNullOrWhiteSpace(employee.ApiToken))
        {
            return new AdminEmployeeStatusDto(employee.Id, employee.DisplayName, false, null, 0, "clockedOut", "API-Token fehlt", false);
        }

        try
        {
            var status = await kimai.GetStatusAsync(settings, employee, cancellationToken);
            return new AdminEmployeeStatusDto(
                employee.Id,
                employee.DisplayName,
                status.IsRunning,
                status.StartedAt,
                status.DurationSeconds,
                status.State,
                status.StateText,
                true,
                status.ActiveTaskLabel);
        }
        catch
        {
            return new AdminEmployeeStatusDto(employee.Id, employee.DisplayName, false, null, 0, "clockedOut", "Status nicht verfuegbar", false);
        }
    }
}
