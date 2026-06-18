using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

public interface IEmployeeService
{
    IReadOnlyCollection<EmployeeDto> GetEnabledEmployees(RuntimeSettings settings);

    EmployeeSettings? FindEmployee(RuntimeSettings settings, ClockRequest request);

    EmployeeSettings? FindEmployeeByPin(RuntimeSettings settings, string? pin);

    EmployeeDto ToEmployeeDto(EmployeeSettings employee);
}
