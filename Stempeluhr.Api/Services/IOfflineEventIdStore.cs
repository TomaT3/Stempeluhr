namespace Stempeluhr.Api.Services;

/// <summary>
/// Persists processed offline event IDs so replayed sync batches are
/// idempotent (no double stamps after retries). Implementations must be safe
/// against concurrent calls.
/// </summary>
public interface IOfflineEventIdStore
{
    /// <summary>Returns true when the ID was newly registered; false when it was already known.</summary>
    bool TryRegister(string eventId);

    /// <summary>Un-registers an ID (used when processing failed and must be retried later).</summary>
    void Remove(string eventId);
}
