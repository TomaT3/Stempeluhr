namespace Stempeluhr.Api.Models;

public sealed class EmployeeSettings
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanClock => IsEnabled && !string.IsNullOrWhiteSpace(ApiToken);

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public int? KimaiUserId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? Pin { get; init; }
    public string? NfcCardId { get; init; }
    public string ApiToken { get; init; } = string.Empty;
    public int? ProjectId { get; init; }
    public int? ActivityId { get; init; }
    public string Color { get; init; } = "#2563eb";
    public string? ImageUrl { get; init; }
    public string? Description { get; init; }
    public string[] Tags { get; init; } = [];
    public bool Billable { get; init; } = true;
    public bool IsEnabled { get; init; } = true;

    /// <summary>
    /// Weitere Tätigkeiten (z. B. Arbeit für andere Kunden), auf die der
    /// Mitarbeiter während der Arbeitszeit wechseln kann, ohne auszustempeln.
    /// Die Standard-Tätigkeit ergibt sich weiter aus ProjectId/ActivityId.
    /// </summary>
    public EmployeeTaskSettings[] Tasks { get; init; } = [];

    /// <summary>
    /// Anzeigename der Haupttätigkeit (ProjectId/ActivityId) am Kiosk, z. B.
    /// „Büro“. Null = neutraler Text „Standard-Tätigkeit“.
    /// </summary>
    public string? DefaultTaskLabel { get; init; }
}

public sealed class EmployeeTaskSettings
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Label { get; init; } = string.Empty;
    public int? ProjectId { get; init; }
    public int? ActivityId { get; init; }
    public bool Billable { get; init; } = true;
}
