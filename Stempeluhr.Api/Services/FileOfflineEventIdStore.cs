using System.Collections.Concurrent;
using System.Text.Json;

namespace Stempeluhr.Api.Services;

/// <summary>
/// JSON-file-backed event-ID store. Writes are atomic (tmp + replace) and the
/// store is trimmed to the most recent entries so it cannot grow unbounded.
/// </summary>
public sealed class FileOfflineEventIdStore : IOfflineEventIdStore, IDisposable
{
    private const int MaxEntries = 10_000;

    private readonly string _filePath;
    private readonly ConcurrentDictionary<string, byte> _ids = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private bool _loaded;

    public FileOfflineEventIdStore(string filePath)
    {
        _filePath = filePath;
        EnsureLoaded();
    }

    public bool TryRegister(string eventId)
    {
        EnsureLoaded();
        if (!_ids.TryAdd(eventId, 0))
        {
            return false;
        }

        _order.Enqueue(eventId);
        TrimToMax();
        _ = Task.Run(PersistAsync);
        return true;
    }

    public void Remove(string eventId)
    {
        EnsureLoaded();
        if (_ids.TryRemove(eventId, out _))
        {
            _ = Task.Run(PersistAsync);
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        lock (_ids)
        {
            if (_loaded)
            {
                return;
            }

            try
            {
                if (File.Exists(_filePath))
                {
                    var ids = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_filePath));
                    foreach (var id in ids ?? [])
                    {
                        if (_ids.TryAdd(id, 0))
                        {
                            _order.Enqueue(id);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Corrupt store: start fresh rather than failing every sync.
            }

            _loaded = true;
        }
    }

    private void TrimToMax()
    {
        while (_order.Count > MaxEntries && _order.TryPeek(out var oldest))
        {
            if (_order.TryDequeue(out _))
            {
                _ids.TryRemove(oldest, out _);
            }
        }
    }

    private async Task PersistAsync()
    {
        await _ioLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tmpPath = _filePath + ".tmp";
            await File.WriteAllTextAsync(tmpPath, JsonSerializer.Serialize(_order.ToArray())).ConfigureAwait(false);
            File.Move(tmpPath, _filePath, overwrite: true);
        }
        catch (IOException)
        {
            // Best-effort persistence; duplicates after a crash are acceptable
            // because Kimai tolerates a repeated stop and the UI shows reality.
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public void Dispose()
    {
        PersistAsync().GetAwaiter().GetResult();
        _ioLock.Dispose();
    }
}
