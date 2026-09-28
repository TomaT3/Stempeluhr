using System.Collections.Concurrent;

namespace Stempeluhr.Api.Services;

/// <summary>
/// Coordinates the live request and the offline replay of the same kiosk
/// event ID. The kiosk sends the ID with the live request and queues the
/// action under it when the request fails or runs into its timeout (issue
/// #67). Per event ID it holds either:
/// - "live in flight": the live request is still working on the event. The
///   kiosk gives up after 8 s, Kimai calls may take up to 15 s, and a client
///   abort does not reliably reach the API behind a proxy - so the replay
///   must wait for the outcome instead of reading a half-done state
///   (<see cref="IsLiveInFlight"/>).
/// - an interrupted transition: a pauseEnd or switch whose stop Kimai
///   confirmed and whose start then failed - the id of the stopped sheet.
///   The replay of exactly that event may resume while nothing runs, and
///   only while that very sheet is still the latest stopped one. An ordinary
///   clock-out on another terminal right after the event leaves the same
///   Kimai state, so the Kimai state alone cannot tell the two apart (issue
///   #55). Set by the replay itself or by the live request.
///
/// A singleton shared by the replay (<see cref="OfflineClockService"/>) and
/// the scoped <see cref="ClockService"/>. Kept in memory only: after a
/// restart the replay rejects instead of guessing.
/// </summary>
public sealed class KioskEventCoordinator
{
    // null = live request in flight, otherwise the id of the stopped sheet.
    private readonly ConcurrentDictionary<string, int?> _events = new(StringComparer.Ordinal);

    /// <summary>
    /// Marks the event as being worked on live until the returned handle is
    /// disposed. An interrupted marker set meanwhile survives the dispose.
    /// Null when the event is already known here (nothing to coordinate).
    /// </summary>
    public IDisposable? BeginLive(string eventId)
    {
        return _events.TryAdd(eventId, null) ? new LiveHandle(this, eventId) : null;
    }

    public bool IsLiveInFlight(string eventId) => _events.TryGetValue(eventId, out var entry) && entry is null;

    public void Remember(string eventId, int stoppedTimesheetId) => _events[eventId] = stoppedTimesheetId;

    public bool TryGet(string eventId, out int stoppedTimesheetId)
    {
        if (_events.TryGetValue(eventId, out var entry) && entry is int id)
        {
            stoppedTimesheetId = id;
            return true;
        }

        stoppedTimesheetId = 0;
        return false;
    }

    /// <summary>Ends an interrupted marker; a live request in flight keeps its entry.</summary>
    public void Forget(string eventId)
    {
        if (_events.TryGetValue(eventId, out var entry) && entry is not null)
        {
            _events.TryRemove(KeyValuePair.Create(eventId, entry));
        }
    }

    private sealed class LiveHandle(KioskEventCoordinator owner, string eventId) : IDisposable
    {
        public void Dispose() => owner._events.TryRemove(KeyValuePair.Create(eventId, (int?)null));
    }
}
