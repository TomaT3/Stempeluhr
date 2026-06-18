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

    private async Task<AdminEmployeeStatusDto> GetEmployeeStatusAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        CancellationToken cancellationToken)
    {
        if (!employee.IsEnabled)
        {
            return new AdminEmployeeStatusDto(employee.Id, false, null, 0, "Inaktiv", false);
        }

        if (string.IsNullOrWhiteSpace(employee.ApiToken))
        {
            return new AdminEmployeeStatusDto(employee.Id, false, null, 0, "API-Token fehlt", false);
        }

        try
        {
            var status = await kimai.GetStatusAsync(settings, employee, cancellationToken);
            return new AdminEmployeeStatusDto(
                employee.Id,
                status.IsRunning,
                status.StartedAt,
                status.DurationSeconds,
                status.StateText,
                true);
        }
        catch
        {
            return new AdminEmployeeStatusDto(employee.Id, false, null, 0, "Status nicht verfuegbar", false);
        }
    }
}
