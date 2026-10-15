using System.Reflection;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;

namespace Stempeluhr.Api.Api;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", (IRuntimeSettingsStore settingsStore) =>
        {
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            return Results.Ok(Describe(settingsStore.Load(), version));
        });

        return app;
    }

    /// <summary>
    /// Antwort von <c>/api/health</c>. Der Kiosk pollt sie ohnehin: daran
    /// erkennt er auch, ob er den Knopf „Abwesenheit“ zeigt. Fehlende
    /// Einstellungen eines einzelnen Mitarbeiters meldet erst der Assistent.
    /// </summary>
    public static HealthStatusDto Describe(RuntimeSettings settings, string? version) => new(
        Ok: true,
        Version: version,
        ConfiguredEmployees: settings.Employees.Count,
        SettingsConfigured: settings.IsConfigured,
        AbsencesConfigured: settings.AbsencesConfigured);
}

public sealed record HealthStatusDto(
    bool Ok,
    string? Version,
    int ConfiguredEmployees,
    bool SettingsConfigured,
    bool AbsencesConfigured);
