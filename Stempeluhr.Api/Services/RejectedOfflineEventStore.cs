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
    DateTimeOffset? ResolvedAt = null,
    bool TelegramEligible = false,
    DateTimeOffset? TelegramNotifiedAt = null);

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

    public bool Record(RejectedOfflineEvent entry)
    {
        lock (_gate)
        {
            var entries = Load();
            if (entries.Any(existing => existing.EventId == entry.EventId))
            {
                return false;
            }
            var updated = TrimResolved(new List<RejectedOfflineEvent>(entries) { entry });
            Persist(updated);
            _entries = updated;
            return true;
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

    /// <summary>Persist delivery only after Telegram confirmed the batch.</summary>
    public void MarkTelegramNotified(IReadOnlyCollection<string> eventIds, DateTimeOffset at)
    {
        lock (_gate)
        {
            var ids = eventIds.ToHashSet(StringComparer.Ordinal);
            var updated = Load().Select(entry => ids.Contains(entry.EventId) && entry.TelegramNotifiedAt is null
                ? entry with { TelegramNotifiedAt = at }
                : entry).ToList();
            updated = TrimResolved(updated);
            Persist(updated);
            _entries = updated;
        }
    }

    private List<RejectedOfflineEvent> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }
        string json;
        try
        {
            // File.Exists returns false for some access errors. Read directly
            // so an inaccessible existing journal is never mistaken for empty.
            json = File.ReadAllText(filePath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _entries = [];
            return _entries;
        }

        try
        {
            _entries = JsonSerializer.Deserialize<List<RejectedOfflineEvent>>(json, JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            // Only malformed JSON is corruption. IO/ACL failures leave the
            // original file untouched and _entries unset for a later retry.
            var quarantinePath = $"{filePath}.corrupt-{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}";
            File.Move(filePath, quarantinePath);
            logger?.LogWarning(ex, "Rejected offline event journal at {Path} was corrupt and moved to {QuarantinePath}",
                filePath, quarantinePath);
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
        return entries.Where(entry => entry.ResolvedAt is null
            || (entry.TelegramEligible && entry.TelegramNotifiedAt is null)
            || keptResolvedIds.Contains(entry.EventId)).ToList();
    }
}
