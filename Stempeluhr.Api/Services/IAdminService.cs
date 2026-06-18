using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

public interface IAdminService
{
    Task<IReadOnlyCollection<AdminEmployeeStatusDto>> GetEmployeeStatusesAsync(CancellationToken cancellationToken = default);

    bool HasDuplicatePins(IEnumerable<EmployeeSettings> employees);
}
