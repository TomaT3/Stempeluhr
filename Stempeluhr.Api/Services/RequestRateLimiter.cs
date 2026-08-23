using System.Collections.Concurrent;

namespace Stempeluhr.Api.Services;

/// <summary>
/// Minimal fixed-window rate limiter keyed by an arbitrary string (e.g. a
/// remote IP address). Slots are evicted lazily so the table cannot grow
/// unbounded. Not a substitute for real auth - it only throttles abuse.
/// </summary>
public sealed class RequestRateLimiter(TimeSpan window, int maxRequests)
{
    private sealed record WindowEntry(DateTimeOffset WindowStart, int Count);

    private readonly ConcurrentDictionary<string, WindowEntry> _entries = new(StringComparer.Ordinal);

    public bool TryAcquire(string key)
    {
        var now = DateTimeOffset.UtcNow;
        var entry = _entries.AddOrUpdate(
            key,
            _ => new WindowEntry(now, 1),
            (_, existing) => now - existing.WindowStart >= window
                ? new WindowEntry(now, 1)
                : existing with { Count = existing.Count + 1 });

        return entry.Count <= maxRequests;
    }
}
