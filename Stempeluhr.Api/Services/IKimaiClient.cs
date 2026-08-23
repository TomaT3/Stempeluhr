using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

public interface IKimaiClient
{
    Task<ClockStatusDto> GetStatusAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        CancellationToken cancellationToken = default);

    Task StartAsync(RuntimeSettings settings, EmployeeSettings employee, CancellationToken cancellationToken = default);

    /// <summary>Starts a timesheet that begins at <paramref name="startedAt"/> (backdating for offline replays).</summary>
    Task StartAtAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        int projectId,
        int activityId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default);

    Task StartPauseAsync(RuntimeSettings settings, EmployeeSettings employee, CancellationToken cancellationToken = default);

    Task StopAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        int timesheetId,
        CancellationToken cancellationToken = default);

    /// <summary>Stops a timesheet and backdates its end to <paramref name="stoppedAt"/>.</summary>
    Task StopAtAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        int timesheetId,
        DateTimeOffset stoppedAt,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<KimaiUserDto>> GetUsersAsync(
        string baseUrl,
        string apiToken,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<KimaiActivityDto>> GetActivitiesAsync(
        string baseUrl,
        string apiToken,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<KimaiProjectDto>> GetProjectsAsync(
        string baseUrl,
        string apiToken,
        CancellationToken cancellationToken = default);
}
