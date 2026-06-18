using System.Reflection;
using Stempeluhr.Api.Services;

namespace Stempeluhr.Api.Api;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", (IRuntimeSettingsStore settingsStore) =>
        {
            var settings = settingsStore.Load();
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            return Results.Ok(new
            {
                ok = true,
                version,
                configuredEmployees = settings.Employees.Count,
                settingsConfigured = settings.IsConfigured
            });
        });

        return app;
    }
}
