using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

public interface IKimaiClient
{
    Task<ClockStatusDto> GetStatusAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        CancellationToken cancellationToken = default);

    /// <summary>Starts a timesheet on <paramref name="target"/> now (work, task or pause).</summary>
    Task StartAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        KimaiTimesheetTarget target,
        CancellationToken cancellationToken = default);

    /// <summary>Starts a timesheet on <paramref name="target"/> that begins at <paramref name="startedAt"/> (backdating for offline replays).</summary>
    Task StartAtAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        KimaiTimesheetTarget target,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Returns the <paramref name="count"/> most recently STOPPED timesheets
    /// of the employee, latest end first (empty if none exist). Used by the
    /// offline replay to verify that a "nothing is running" state really
    /// comes from an interrupted two-step transaction (pauseEnd, task
    /// switch), and by pauseEnd to resume the task that ran before the pause.
    /// </summary>
    Task<IReadOnlyList<KimaiRecentTimesheetDto>> GetRecentStoppedTimesheetsAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        int count,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the IANA timezone of the token owner (Kimai interprets naive
    /// HTML5 datetimes in this timezone), or null if the lookup fails - the
    /// caller then falls back to the server's local timezone.
    /// </summary>
    Task<string?> GetCurrentUserTimezoneAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
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

    /// <summary>Alle Timesheets des Token-Inhabers, deren begin im Zeitraum liegt (HTML5-lokale Grenzen).</summary>
    Task<IReadOnlyCollection<KimaiTimesheetEntryDto>> GetTimesheetsAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        DateTime begin,
        DateTime end,
        CancellationToken cancellationToken = default);
}

/// <summary>Minimal view of a finished timesheet, for replay verification and pause resume.</summary>
public sealed record KimaiRecentTimesheetDto(int? ActivityId, DateTimeOffset? EndedAt, int? ProjectId = null);
