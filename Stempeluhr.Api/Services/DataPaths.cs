namespace Stempeluhr.Api.Services;

/// <summary>
/// Ordner der persistenten Dateien (<c>settings.json</c>, Event-ID-Store).
/// Standard ist <c>data/</c> im ContentRoot (Docker: <c>/app/data</c>).
/// <c>Stempeluhr:DataPath</c> verlegt ihn, etwa damit der E2E-Test neben
/// einer laufenden Dev-API eigene Dateien benutzt.
/// </summary>
public static class DataPaths
{
    public static string Directory(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["Stempeluhr:DataPath"];
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(environment.ContentRootPath, "data")
            : configured;
    }
}
