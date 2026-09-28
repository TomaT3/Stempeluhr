using System.Text.Json;

namespace Stempeluhr.Api.Services;

public sealed record RejectedOfflineEvent(
    string EventId,
    string EmployeeId,
    string EmployeeName,
    string Action,
    DateTimeOffset PerformedAt,
    DateTimeOffset RejectedAt,
    string Message,
    DateTimeOffset? ResolvedAt = null);

/// <summary>Persistent admin journal. Credentials and card IDs are never stored here.</summary>
public sealed class RejectedOfflineEventStore(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private List<RejectedOfflineEvent>? _entries;

    public IReadOnlyList<RejectedOfflineEvent> List()
    {
        lock (_gate)
        {
            return Load().OrderByDescending(entry => entry.RejectedAt).ToArray();
        }
    }

    public void Record(RejectedOfflineEvent entry)
    {
        lock (_gate)
        {
            var entries = Load();
            if (entries.Any(existing => existing.EventId == entry.EventId))
            {
                return;
            }
            var updated = new List<RejectedOfflineEvent>(entries) { entry };
            Persist(updated);
            _entries = updated;
        }
    }

    public bool Resolve(string eventId)
    {
        lock (_gate)
        {
            var entries = Load();
            var index = entries.FindIndex(entry => entry.EventId == eventId);
            if (index < 0)
            {
                return false;
            }
            if (entries[index].ResolvedAt is null)
            {
                var updated = new List<RejectedOfflineEvent>(entries);
                updated[index] = updated[index] with { ResolvedAt = DateTimeOffset.UtcNow };
                Persist(updated);
                _entries = updated;
            }
            return true;
        }
    }

    private List<RejectedOfflineEvent> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }
        _entries = File.Exists(filePath)
            ? JsonSerializer.Deserialize<List<RejectedOfflineEvent>>(File.ReadAllText(filePath), JsonOptions) ?? []
            : [];
        return _entries;
    }

    private void Persist(List<RejectedOfflineEvent> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var temporaryPath = filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entries, JsonOptions));
        File.Move(temporaryPath, filePath, overwrite: true);
    }
}
