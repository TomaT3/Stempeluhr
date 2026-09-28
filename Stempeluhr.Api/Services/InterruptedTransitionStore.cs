using System.Collections.Concurrent;

namespace Stempeluhr.Api.Services;

/// <summary>
/// Two-step transitions (pauseEnd, switch) whose stop Kimai confirmed and
/// whose start then failed: kiosk event ID -> id of the sheet that was
/// stopped for it. The offline replay of exactly that event may resume while
/// nothing runs, and only while that very sheet is still the latest stopped
/// one. An ordinary clock-out on another terminal right after the event
/// leaves the same Kimai state, so the Kimai state alone cannot tell the two
/// apart (issue #55).
///
/// Set by the replay (<see cref="OfflineClockService"/>) and by the live path
/// (<see cref="ClockService"/>, when the kiosk sent the event ID it queues on
/// failure - issue #67), hence a singleton. Kept in memory only: after a
/// restart the replay rejects instead of guessing.
/// </summary>
public sealed class InterruptedTransitionStore
{
    private readonly ConcurrentDictionary<string, int> _stoppedSheets = new(StringComparer.Ordinal);

    public void Remember(string eventId, int stoppedTimesheetId) => _stoppedSheets[eventId] = stoppedTimesheetId;

    public bool TryGet(string eventId, out int stoppedTimesheetId) => _stoppedSheets.TryGetValue(eventId, out stoppedTimesheetId);

    public void Forget(string eventId) => _stoppedSheets.TryRemove(eventId, out _);
}
