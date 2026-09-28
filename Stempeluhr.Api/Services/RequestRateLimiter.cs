using System.Collections.Concurrent;

namespace Stempeluhr.Api.Services;

/// <summary>
/// Minimal fixed-window rate limiter keyed by an arbitrary string (e.g. a
/// remote IP address). Expired entries are evicted opportunistically and the
/// table is capped at <see cref="MaxEntries"/>: without the cap it would grow
/// unbounded with unique keys (spoofed X-Forwarded-For headers etc.). When the
/// cap is reached, expired entries are reclaimed first; if none can be
/// reclaimed, unknown keys are rejected outright (fail closed under a flood of
/// unique keys - legitimate repeat clients are already tracked).
/// Not a substitute for real auth - it only throttles abuse.
/// </summary>
public sealed class RequestRateLimiter(TimeSpan window, int maxRequests)
{
    private const int MaxEntries = 10_000;

    private sealed record WindowEntry(DateTimeOffset WindowStart, int Count);

    private readonly ConcurrentDictionary<string, WindowEntry> _entries = new(StringComparer.Ordinal);

    public bool TryAcquire(string key) => TryAcquire(key, 1);

    /// <summary>
    /// Acquires <paramref name="cost"/> units from the key's window budget.
    /// Requests are not equal: the unauthenticated kiosk sync endpoint prices
    /// its batches by EVENT count (each event result reveals whether that
    /// event's PIN matched), so 20 requests x 100 events must not smuggle
    /// 2,000 PIN guesses past a per-request limiter. Costs &lt;= 0 count as a
    /// single request unit; an over-budget acquisition fails closed without
    /// granting anything.
    /// </summary>
    public bool TryAcquire(string key, int cost)
    {
        var units = Math.Max(1, cost);
        var now = DateTimeOffset.UtcNow;
        EvictExpired(now);

        if (_entries.TryGetValue(key, out var entry) && now - entry.WindowStart < window)
        {
            var updated = entry with { Count = entry.Count + units };
            _entries[key] = updated;
            return updated.Count <= maxRequests;
        }

        // New window for this key.
        if (_entries.Count >= MaxEntries && !_entries.ContainsKey(key))
        {
            // Table full of active entries: fail closed for unseen keys instead
            // of growing memory without bound.
            return false;
        }

        _entries[key] = new WindowEntry(now, units);
        return units <= maxRequests;
    }

    private void EvictExpired(DateTimeOffset now)
    {
        // Cheap enough at this table size and keeps memory bounded even with
        // many one-shot IPs.
        if (_entries.IsEmpty)
        {
            return;
        }

        foreach (var pair in _entries)
        {
            if (now - pair.Value.WindowStart >= window)
            {
                _entries.TryRemove(pair);
            }
        }
    }
}

/// <summary>
/// The kiosk endpoints' limiters. Both are the same type, so they are keyed
/// services: a plain singleton registered twice resolves to the LAST
/// registration for every consumer (issue #53: sync then ran on the identify
/// budget, and both shared one instance).
/// </summary>
public static class KioskRateLimiters
{
    public const string SyncKey = "kiosk-sync";
    public const string IdentifyKey = "kiosk-identify";

    public static IServiceCollection AddKioskRateLimiters(this IServiceCollection services)
    {
        // Throttles the unauthenticated kiosk sync endpoint (per client IP, fixed
        // window; real per-client IPs require Stempeluhr:KnownProxies - see Program.cs).
        services.AddKeyedSingleton(SyncKey, (_, _) => new RequestRateLimiter(TimeSpan.FromSeconds(60), maxRequests: 20));
        // Separate limiter for the unauthenticated kiosk identify endpoint. More
        // generous than the sync limiter: a shift change can scan many cards in a
        // minute, but 60/min still caps brute-forcing card ids (4-byte UIDs) and
        // protects Kimai from a request flood (each identify hits GetStatusAsync).
        services.AddKeyedSingleton(IdentifyKey, (_, _) => new RequestRateLimiter(TimeSpan.FromSeconds(60), maxRequests: 60));
        return services;
    }
}
