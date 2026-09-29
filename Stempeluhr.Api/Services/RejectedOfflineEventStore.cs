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
    private static readonly TimeSpan SendHistoryRetention = TimeSpan.FromDays(1);
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

    /// <summary>Unsorted copy for callers that filter the whole journal anyway.</summary>
    public IReadOnlyList<RejectedOfflineEvent> Snapshot()
    {
        lock (_gate)
        {
            return Load().ToArray();
        }
    }

    /// <summary>Cheap check for the periodic notifier before it loads settings.</summary>
    public bool HasPendingTelegram()
    {
        lock (_gate)
        {
            return Load().Any(AwaitsTelegram);
        }
    }

    /// <summary>
    /// An entry an admin already resolved must never ask for a manual Kimai
    /// entry again - that could lead to a duplicate timesheet.
    /// </summary>
    public static bool AwaitsTelegram(RejectedOfflineEvent entry) =>
        entry.TelegramEligible && entry.TelegramNotifiedAt is null && entry.ResolvedAt is null;

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
        // The notifier derives its daily Telegram limit from TelegramNotifiedAt.
        // Keep one entry per send of the last day (covers the whole current
        // UTC day), so trimming can never hand out that budget a second time.
        var sendCutoff = DateTimeOffset.UtcNow - SendHistoryRetention;
        keptResolvedIds.UnionWith(entries
            .Where(entry => entry.ResolvedAt is not null && entry.TelegramNotifiedAt >= sendCutoff)
            .GroupBy(entry => entry.TelegramNotifiedAt)
            .Select(send => send.First().EventId));
        // Resolved entries no longer await Telegram (see AwaitsTelegram), so
        // they are trimmed regardless of their delivery state.
        return entries.Where(entry => entry.ResolvedAt is null
            || keptResolvedIds.Contains(entry.EventId)).ToList();
    }
}
