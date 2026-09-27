using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

public interface IAdminService
{
    Task<IReadOnlyCollection<AdminEmployeeStatusDto>> GetEmployeeStatusesAsync(CancellationToken cancellationToken = default);

    bool HasDuplicatePins(IEnumerable<EmployeeSettings> employees);

    bool HasDuplicateNfcCardIds(IEnumerable<EmployeeSettings> employees);

    /// <summary>
    /// Checks the employees' additional tasks; returns an error message for
    /// the admin or null when everything is valid.
    /// </summary>
    string? ValidateTasks(RuntimeSettings settings);
}
