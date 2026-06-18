using Stempeluhr.Api.Services;

namespace Stempeluhr.Api.Api;

public static class EmployeeEndpoints
{
    public static IEndpointRouteBuilder MapEmployeeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/employees", (
            HttpRequest request,
            IRuntimeSettingsStore settingsStore,
            IAdminAuthorizationService authorization,
            IEmployeeService employees) =>
        {
            if (!authorization.IsAdmin(request))
            {
                return Results.Unauthorized();
            }

            var settings = settingsStore.Load();
            return Results.Ok(employees.GetEnabledEmployees(settings));
        });

        return app;
    }
}
