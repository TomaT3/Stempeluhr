using System.Text.Json;
using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

/// <summary>
/// Persistente Liste der Abwesenheitsanträge (<c>absences.json</c>), nach dem
/// Muster von <see cref="TimeCorrectionStore"/>: ein Lock, Cache im Speicher,
/// Schreiben über tmp + Move, kaputte Datei wandert nach <c>.corrupt-…</c>.
/// Anders als dort wird kaum gekürzt: Urlaubskonto und Monatsexport brauchen
/// die angewandten, stornierten und von Hand erledigten Anträge dauerhaft.
/// Nur abgelehnte und zurückgezogene fallen nach drei Jahren heraus.
/// </summary>
public sealed class AbsenceStore(string filePath, ILogger<AbsenceStore>? logger = null, TimeProvider? clock = null)
{
    public static readonly TimeSpan DiscardedRetention = TimeSpan.FromDays(3 * 365);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private List<AbsenceRequest>? _entries;

    /// <summary>Alle Anträge, neueste zuerst.</summary>
    public IReadOnlyList<AbsenceRequest> List()
    {
        lock (_gate)
        {
            return Load().OrderByDescending(entry => entry.CreatedAt).ToArray();
        }
    }

    public AbsenceRequest? Find(string id)
    {
        lock (_gate)
        {
            return Load().FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
        }
    }

    /// <summary>False, wenn die ID schon vergeben ist.</summary>
    public bool Add(AbsenceRequest request)
    {
        lock (_gate)
        {
            var entries = Load();
            if (entries.Any(entry => entry.Id == request.Id))
            {
                return false;
            }
            Save(Trim(new List<AbsenceRequest>(entries) { request }));
            return true;
        }
    }

    /// <summary>
    /// Ersetzt den Antrag mit derselben ID; false, wenn es ihn nicht gibt. Die
    /// Telegram-Nachricht bleibt erhalten, wenn der neue Stand keine kennt
    /// (wie bei <see cref="TimeCorrectionStore.Update"/>).
    /// </summary>
    public bool Update(AbsenceRequest request)
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
            Save(Trim(new List<AbsenceRequest>(entries) { [index] = request }));
            return true;
        }
    }

    /// <summary>Hält die Telegram-Nachricht fest, ohne sonst etwas zu ändern (siehe <see cref="TimeCorrectionStore.AttachTelegramMessage"/>).</summary>
    public AbsenceRequest? AttachTelegramMessage(string id, long chatId, long messageId)
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
            Save(Trim(new List<AbsenceRequest>(entries) { [index] = attached }));
            return attached;
        }
    }

    private void Save(List<AbsenceRequest> entries)
    {
        Persist(entries);
        _entries = entries;
    }

    private List<AbsenceRequest> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }
        string json;
        try
        {
            json = File.ReadAllText(filePath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _entries = [];
            return _entries;
        }

        try
        {
            _entries = JsonSerializer.Deserialize<List<AbsenceRequest>>(json, JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            var quarantinePath = $"{filePath}.corrupt-{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}";
            File.Move(filePath, quarantinePath);
            logger?.LogWarning(ex, "Absence store at {Path} was corrupt and moved to {QuarantinePath}",
                filePath, quarantinePath);
            _entries = [];
        }
        return _entries;
    }

    private void Persist(List<AbsenceRequest> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var temporaryPath = filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entries, JsonOptions));
        File.Move(temporaryPath, filePath, overwrite: true);
    }

    private List<AbsenceRequest> Trim(List<AbsenceRequest> entries)
    {
        var cutoff = _clock.GetUtcNow() - DiscardedRetention;
        return entries
            .Where(entry => entry.Status is not (AbsenceStatus.Rejected or AbsenceStatus.Withdrawn)
                || (entry.DecidedAt ?? entry.CreatedAt) >= cutoff)
            .ToList();
    }
}
