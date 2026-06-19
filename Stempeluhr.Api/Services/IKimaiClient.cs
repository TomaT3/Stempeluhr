using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

public interface IKimaiClient
{
    Task<ClockStatusDto> GetStatusAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        CancellationToken cancellationToken = default);

    Task StartAsync(RuntimeSettings settings, EmployeeSettings employee, CancellationToken cancellationToken = default);

    Task StartPauseAsync(RuntimeSettings settings, EmployeeSettings employee, CancellationToken cancellationToken = default);

    Task StopAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        int timesheetId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<KimaiUserDto>> GetUsersAsync(
        string baseUrl,
        string apiToken,
        CancellationToken cancellationToken = default);
}
