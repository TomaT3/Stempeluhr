using System.Text.Json;
using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

/// <summary>
/// Persistente Liste der Korrekturanträge (<c>time-corrections.json</c>), nach
/// dem Muster von <see cref="RejectedOfflineEventStore"/>: ein Lock, Cache im
/// Speicher, Schreiben über tmp + Move, kaputte Datei wandert nach
/// <c>.corrupt-…</c>. Offene Anträge (<see cref="TimeCorrectionRequest.IsOpen"/>)
/// bleiben immer, von den abgeschlossenen die letzten 1000.
/// </summary>
public sealed class TimeCorrectionStore(string filePath, ILogger<TimeCorrectionStore>? logger = null)
{
    private const int MaxCompletedEntries = 1000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private List<TimeCorrectionRequest>? _entries;

    /// <summary>Alle Anträge, neueste zuerst.</summary>
    public IReadOnlyList<TimeCorrectionRequest> List()
    {
        lock (_gate)
        {
            return Load().OrderByDescending(entry => entry.CreatedAt).ToArray();
        }
    }

    public TimeCorrectionRequest? Find(string id)
    {
        lock (_gate)
        {
            return Load().FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
        }
    }

    /// <summary>False, wenn die ID schon vergeben ist.</summary>
    public bool Add(TimeCorrectionRequest request)
    {
        lock (_gate)
        {
            var entries = Load();
            if (entries.Any(entry => entry.Id == request.Id))
            {
                return false;
            }
            Save(TrimCompleted(new List<TimeCorrectionRequest>(entries) { request }));
            return true;
        }
    }

    /// <summary>
    /// Ersetzt den Antrag mit derselben ID; false, wenn es ihn nicht gibt. Die
    /// Telegram-Nachricht bleibt erhalten, wenn der neue Stand keine kennt: der
    /// Service hält seinen Stand unter dem Antrags-Lock, der Notifier hängt die
    /// Nachricht dagegen unabhängig davon an (<see cref="AttachTelegramMessage"/>).
    /// </summary>
    public bool Update(TimeCorrectionRequest request)
    {
        lock (_gate)
        {
            var entries = Load();
            var index = entries.FindIndex(entry => entry.Id == request.Id);
            if (index < 0)
            {
                return false;
            }
            if (request.TelegramMessageId is null && entries[index].TelegramMessageId is { } messageId)
            {
                request = request with { TelegramChatId = entries[index].TelegramChatId, TelegramMessageId = messageId };
            }
            var updated = new List<TimeCorrectionRequest>(entries) { [index] = request };
            Save(TrimCompleted(updated));
            return true;
        }
    }

    /// <summary>
    /// Hält die Telegram-Nachricht des Antrags fest und gibt den dabei
    /// aktuellen Stand zurück (null, wenn es den Antrag nicht gibt). Läuft
    /// allein unter dem Store-Lock und ändert sonst nichts, damit eine
    /// gleichzeitige Entscheidung nie überschrieben wird.
    /// </summary>
    public TimeCorrectionRequest? AttachTelegramMessage(string id, long chatId, long messageId)
    {
        lock (_gate)
        {
            var entries = Load();
            var index = entries.FindIndex(entry => entry.Id == id);
            if (index < 0)
            {
                return null;
            }
            var attached = entries[index] with { TelegramChatId = chatId, TelegramMessageId = messageId };
            Save(TrimCompleted(new List<TimeCorrectionRequest>(entries) { [index] = attached }));
            return attached;
        }
    }

    private void Save(List<TimeCorrectionRequest> entries)
    {
        Persist(entries);
        _entries = entries;
    }

    private List<TimeCorrectionRequest> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }
        string json;
        try
        {
            // File.Exists returns false for some access errors. Read directly
            // so an inaccessible existing file is never mistaken for empty.
            json = File.ReadAllText(filePath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _entries = [];
            return _entries;
        }

        try
        {
            _entries = JsonSerializer.Deserialize<List<TimeCorrectionRequest>>(json, JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            // Only malformed JSON is corruption. IO/ACL failures leave the
            // original file untouched and _entries unset for a later retry.
            var quarantinePath = $"{filePath}.corrupt-{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}";
            File.Move(filePath, quarantinePath);
            logger?.LogWarning(ex, "Time correction store at {Path} was corrupt and moved to {QuarantinePath}",
                filePath, quarantinePath);
            _entries = [];
        }
        return _entries;
    }

    private void Persist(List<TimeCorrectionRequest> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var temporaryPath = filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entries, JsonOptions));
        File.Move(temporaryPath, filePath, overwrite: true);
    }

    private static List<TimeCorrectionRequest> TrimCompleted(List<TimeCorrectionRequest> entries)
    {
        var keptCompletedIds = entries.Where(entry => !entry.IsOpen)
            .OrderByDescending(entry => entry.DecidedAt ?? entry.CreatedAt)
            .Take(MaxCompletedEntries)
            .Select(entry => entry.Id)
            .ToHashSet(StringComparer.Ordinal);
        return entries.Where(entry => entry.IsOpen || keptCompletedIds.Contains(entry.Id)).ToList();
    }
}
