using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

public sealed class EmployeeService : IEmployeeService
{
    public IReadOnlyCollection<EmployeeDto> GetEnabledEmployees(RuntimeSettings settings)
    {
        return settings.Employees
            .Where(employee => employee.IsEnabled && !string.IsNullOrWhiteSpace(employee.ApiToken))
            .Select(ToEmployeeDto)
            .ToArray();
    }

    public EmployeeSettings? FindEmployee(RuntimeSettings settings, ClockRequest request)
    {
        var employee = settings.Employees.FirstOrDefault(candidate =>
            candidate.IsEnabled &&
            string.Equals(candidate.Id, request.EmployeeId, StringComparison.OrdinalIgnoreCase));

        if (employee is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(employee.Pin))
        {
            return employee;
        }

        return string.Equals(employee.Pin, request.Pin, StringComparison.Ordinal) ? employee : null;
    }

    public EmployeeSettings? FindEmployeeByPin(RuntimeSettings settings, string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin))
        {
            return null;
        }

        var matches = settings.Employees
            .Where(employee =>
                employee.IsEnabled &&
                !string.IsNullOrWhiteSpace(employee.ApiToken) &&
                string.Equals(employee.Pin, pin.Trim(), StringComparison.Ordinal))
            .Take(2)
            .ToArray();

        return matches.Length == 1 ? matches[0] : null;
    }

    public EmployeeDto ToEmployeeDto(EmployeeSettings employee)
    {
        return new EmployeeDto(
            employee.Id,
            employee.DisplayName,
            Initials(employee.DisplayName),
            employee.Color,
            employee.ImageUrl,
            !string.IsNullOrWhiteSpace(employee.Pin));
    }

    private static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Concat(parts.Take(2).Select(part => char.ToUpperInvariant(part[0])));
    }
}
