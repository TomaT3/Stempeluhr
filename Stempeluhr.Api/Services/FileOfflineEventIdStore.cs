using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace Stempeluhr.Api.Services;

/// <summary>
/// JSON-file-backed event-ID store. Writes are atomic (tmp + replace) and the
/// store is trimmed to the most recent entries so it cannot grow unbounded.
///
/// All persist requests are funneled through a single background writer so
/// writes are strictly ordered: a Remove scheduled after a TryRegister can
/// never be persisted before it (which would resurrect a removed ID after a
/// restart and silently lose that event).
///
/// Compound state mutations (register = dictionary add + queue append,
/// remove = dictionary remove + queue rebuild) are atomic via an internal
/// lock: callers are NOT all under one shared lock - the live NFC endpoint
/// touches this store outside the OfflineClockService's _syncLock - so the
/// store must protect its own invariants (e.g. an ID must never end up in
/// the dictionary while missing from the ordering queue, which would make
/// a restart forget a freshly registered ID and turn its retry into a
/// second toggle).
///
/// Residual crash window (accepted trade-off): persists are asynchronous, so
/// a process crash AFTER the persist of a TryRegister but BEFORE the persist
/// of its subsequent Remove leaves that ID on disk although its event was
/// only buffered, never applied. After a restart a client re-send of that
/// event is then discarded as "duplicate" instead of being buffered again -
/// in the worst case a lost stamp. The window is milliseconds wide; closing
/// it would require a synchronous fsync on every Remove (request hot path),
/// which was traded away for throughput. If this ever matters in practice,
/// make Remove flush synchronously before returning.
/// </summary>
public sealed class FileOfflineEventIdStore : IOfflineEventIdStore, IDisposable
{
    private const int MaxEntries = 10_000;

    private readonly string _filePath;
    private readonly object _stateLock = new();
    private readonly ConcurrentDictionary<string, byte> _ids = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private readonly Channel<bool> _persistRequests = Channel.CreateUnbounded<bool>();
    private readonly Task _persistTask;
    private readonly ILogger<FileOfflineEventIdStore>? _logger;
    private bool _loaded;

    public FileOfflineEventIdStore(string filePath, ILogger<FileOfflineEventIdStore>? logger = null)
    {
        _filePath = filePath;
        _logger = logger;
        EnsureLoaded();
        _persistTask = Task.Run(PersistLoopAsync);
    }

    public bool TryRegister(string eventId)
    {
        lock (_stateLock)
        {
            EnsureLoaded();
            if (!_ids.TryAdd(eventId, 0))
            {
                return false;
            }

            _order.Enqueue(eventId);
            TrimToMax();
        }

        SchedulePersist();
        return true;
    }

    public void Remove(string eventId)
    {
        var removed = false;
        lock (_stateLock)
        {
            EnsureLoaded();
            if (_ids.TryRemove(eventId, out _))
            {
                removed = true;
                // Also drop the ordering entry: persisting it would resurrect the
                // removed ID on the next restart (the event was never applied and
                // must be retried, not treated as a duplicate).
                var remaining = _order.ToArray().Where(_ids.ContainsKey).ToArray();
                _order.Clear();
                foreach (var id in remaining)
                {
                    _order.Enqueue(id);
                }
            }
        }

        if (removed)
        {
            SchedulePersist();
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        lock (_stateLock)
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
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Corrupt or unreadable store: move it aside (so the operator
                // can inspect it) and start fresh rather than failing every sync.
                try
                {
                    File.Move(_filePath, _filePath + ".corrupt", overwrite: true);
                }
                catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
                {
                    // Best effort - a leftover corrupt file is handled the same
                    // way on the next startup.
                }

                _logger?.LogWarning(ex, "Offline event-ID store at {Path} was corrupt and has been reset", _filePath);
            }

            _loaded = true;
        }
    }

    /// <summary>
    /// Trims the ordering queue to its maximum size. Must be called while
    /// holding <see cref="_stateLock"/> - peek and dequeue of the same entry
    /// plus the dictionary removal are only atomic together.
    /// </summary>
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

    /// <summary>
    /// Requests a persist. The single background writer serializes all
    /// requests, so the on-disk state always reflects the last mutation in
    /// schedule order (no stale write can land after a newer one).
    /// </summary>
    private void SchedulePersist()
    {
        _persistRequests.Writer.TryWrite(true);
    }

    private async Task PersistLoopAsync()
    {
        await foreach (var _ in _persistRequests.Reader.ReadAllAsync())
        {
            try
            {
                await PersistAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort persistence; duplicates after a crash are acceptable
                // because Kimai tolerates a repeated stop and the UI shows reality.
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
            string[] orderSnapshot;
            lock (_stateLock)
            {
                orderSnapshot = _order.ToArray();
            }

            await File.WriteAllTextAsync(tmpPath, JsonSerializer.Serialize(orderSnapshot)).ConfigureAwait(false);
            File.Move(tmpPath, _filePath, overwrite: true);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public void Dispose()
    {
        _persistRequests.Writer.TryComplete();
        try
        {
            _persistTask.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing left to do - the store is best-effort.
        }

        _ioLock.Dispose();
    }
}
