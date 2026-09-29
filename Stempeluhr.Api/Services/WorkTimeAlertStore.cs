using System.Text.Json;

namespace Stempeluhr.Api.Services;

public sealed record WorkTimeAlert(string EmployeeId, string Key, DateTimeOffset SentAt);

/// <summary>
/// Remembers which work time warnings Telegram already accepted, so a check
/// every few minutes - and after a restart - sends each warning only once.
/// </summary>
public sealed class WorkTimeAlertStore(string filePath, ILogger<WorkTimeAlertStore>? logger = null)
{
    /// <summary>Longer than <see cref="WorkTimeLimitCalculator.Lookback"/>: no warning can come back.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromDays(3);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private List<WorkTimeAlert>? _entries;

    public bool HasSent(string employeeId, string key)
    {
        lock (_gate)
        {
            return Load().Any(entry => entry.Key == key
                && string.Equals(entry.EmployeeId, employeeId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public IReadOnlyList<WorkTimeAlert> Snapshot()
    {
        lock (_gate)
        {
            return Load().ToArray();
        }
    }

    /// <summary>
    /// Takes effect in memory even if the file cannot be written (the error is
    /// still thrown), so a full disk cannot repeat the warning every check.
    /// </summary>
    public void MarkSent(string employeeId, string key, DateTimeOffset at)
    {
        lock (_gate)
        {
            var updated = Load()
                .Where(entry => at - entry.SentAt <= Retention)
                .Append(new WorkTimeAlert(employeeId, key, at))
                .ToList();
            _entries = updated;
            Persist(updated);
        }
    }

    private List<WorkTimeAlert> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }
        string json;
        try
        {
            // Read directly: an inaccessible file must not be mistaken for empty.
            json = File.ReadAllText(filePath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _entries = [];
            return _entries;
        }

        try
        {
            _entries = JsonSerializer.Deserialize<List<WorkTimeAlert>>(json, JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            var quarantinePath = $"{filePath}.corrupt-{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}";
            File.Move(filePath, quarantinePath);
            logger?.LogWarning(ex, "Work time alert file at {Path} was corrupt and moved to {QuarantinePath}",
                filePath, quarantinePath);
            _entries = [];
        }
        return _entries;
    }

    private void Persist(List<WorkTimeAlert> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var temporaryPath = filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entries, JsonOptions));
        File.Move(temporaryPath, filePath, overwrite: true);
    }
}
