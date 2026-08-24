namespace Stempeluhr.Api.Models;

public sealed class RuntimeSettings
{
    public string BaseUrl { get; init; } = string.Empty;
    public string? AdminPassword { get; init; }
    public string? AdminApiToken { get; init; }
    public int? DefaultProjectId { get; init; }
    public int? DefaultActivityId { get; init; }
    public int? PauseActivityId { get; init; }

    /// <summary>
    /// Maximaler Abstand zwischen dem Ende des letzten gestoppten
    /// Pause-Timesheets und dem Zeitstempel eines replayten pauseEnd-Events,
    /// damit dieser als „unterbrochene Transaktion" erkannt wird. Kleiner
    /// Wert = wenig Phantom-Resume-Fenster; Standard 30 s absorbieren nur
    /// Timestamp-Rounding.
    /// </summary>
    public int PauseEndRecoveryToleranceSeconds { get; init; } = 30;
    public List<EmployeeSettings> Employees { get; init; } = [];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && Employees.Any(employee => employee.IsEnabled);
}
