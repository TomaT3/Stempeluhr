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
public sealed class RejectedOfflineEventStore(string filePath, ILogger<RejectedOfflineEventStore>? logger = null)
{
    private const int MaxResolvedEntries = 1000;
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

    public RejectedOfflineEvent? Find(string eventId)
    {
        lock (_gate)
        {
            return Load().FirstOrDefault(entry => entry.EventId == eventId);
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
            var updated = TrimResolved(new List<RejectedOfflineEvent>(entries) { entry });
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
                updated = TrimResolved(updated);
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
        try
        {
            _entries = File.Exists(filePath)
                ? JsonSerializer.Deserialize<List<RejectedOfflineEvent>>(File.ReadAllText(filePath), JsonOptions) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Move(filePath, filePath + ".corrupt", overwrite: true);
            }
            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
            {
                // A read-only path cannot be quarantined; keep replay usable.
            }
            logger?.LogWarning(ex, "Rejected offline event journal at {Path} was unreadable and has been reset", filePath);
            _entries = [];
        }
        return _entries;
    }

    private void Persist(List<RejectedOfflineEvent> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var temporaryPath = filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entries, JsonOptions));
        File.Move(temporaryPath, filePath, overwrite: true);
    }

    private static List<RejectedOfflineEvent> TrimResolved(List<RejectedOfflineEvent> entries)
    {
        var keptResolvedIds = entries.Where(entry => entry.ResolvedAt is not null)
            .OrderByDescending(entry => entry.ResolvedAt)
            .Take(MaxResolvedEntries)
            .Select(entry => entry.EventId)
            .ToHashSet(StringComparer.Ordinal);
        return entries.Where(entry => entry.ResolvedAt is null || keptResolvedIds.Contains(entry.EventId)).ToList();
    }
}
