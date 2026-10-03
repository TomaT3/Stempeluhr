namespace Stempeluhr.Api.Services;

/// <summary>An alarm or all-clear that Telegram has not accepted yet.</summary>
public sealed record TerminalAlertChange(TerminalCondition Condition, bool Recovered, DateTimeOffset Since);

/// <summary>What the admin status page shows for one terminal.</summary>
public sealed record TerminalHealthSnapshot(
    TerminalHealthReport? Report,
    DateTimeOffset? ReceivedAt,
    IReadOnlyDictionary<TerminalCondition, DateTimeOffset> Alarms);

/// <summary>
/// Latest diagnostics report per terminal plus the evaluated conditions.
/// In memory only: after a server restart an ongoing problem may be
/// reported again and an all-clear across the restart is lost. Restarts are
/// rare; writing a file every minute is not worth it.
/// </summary>
public sealed class TerminalHealthStore
{
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public TerminalHealthStore(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        MonitoringSince = _clock.GetUtcNow();
    }

    /// <summary>Stands in for the last report of terminals not heard from since the start.</summary>
    public DateTimeOffset MonitoringSince { get; }

    /// <summary>
    /// Evaluates every report right away: a clearing report between two checks
    /// must reset hold times and hysteresis even if the next report triggers again.
    /// </summary>
    public void Record(string terminalId, TerminalHealthReport report)
    {
        lock (_gate)
        {
            var entry = Get(terminalId);
            var now = _clock.GetUtcNow();
            entry.Report = report;
            entry.ReceivedAt = now;
            entry.Present = Evaluate(entry, now);
        }
    }

    /// <summary>Re-evaluates the terminal and returns what Telegram still has to hear.</summary>
    public IReadOnlyList<TerminalAlertChange> Update(string terminalId, DateTimeOffset now)
    {
        lock (_gate)
        {
            var entry = Get(terminalId);
            entry.Present = Evaluate(entry, now);
            var alarms = Alarms(entry.Present, now)
                .Where(alarm => !entry.Notified.ContainsKey(alarm.Key))
                .Select(alarm => new TerminalAlertChange(alarm.Key, false, alarm.Value));
            var recoveries = entry.Notified
                .Where(notified => !entry.Present.ContainsKey(notified.Key))
                .Select(notified => new TerminalAlertChange(notified.Key, true, notified.Value));
            return [.. recoveries, .. alarms];
        }
    }

    public void MarkNotified(string terminalId, IEnumerable<TerminalAlertChange> changes)
    {
        lock (_gate)
        {
            var entry = Get(terminalId);
            foreach (var change in changes)
            {
                if (change.Recovered) entry.Notified.Remove(change.Condition);
                else entry.Notified[change.Condition] = change.Since;
            }
        }
    }

    /// <summary>Evaluated for display; the stored state stays unchanged.</summary>
    public TerminalHealthSnapshot Snapshot(string terminalId, DateTimeOffset now)
    {
        lock (_gate)
        {
            _entries.TryGetValue(terminalId, out var entry);
            entry ??= new Entry();
            return new TerminalHealthSnapshot(entry.Report, entry.ReceivedAt, Alarms(Evaluate(entry, now), now));
        }
    }

    /// <summary>Forgets terminals whose token was removed.</summary>
    public void Retain(IEnumerable<string> terminalIds)
    {
        var keep = new HashSet<string>(terminalIds, StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            foreach (var terminalId in _entries.Keys.Where(id => !keep.Contains(id)).ToArray())
            {
                _entries.Remove(terminalId);
            }
        }
    }

    private Dictionary<TerminalCondition, DateTimeOffset> Evaluate(Entry entry, DateTimeOffset now) =>
        TerminalHealthEvaluator.Evaluate(entry.Report, entry.ReceivedAt, MonitoringSince, now, entry.Present);

    private static Dictionary<TerminalCondition, DateTimeOffset> Alarms(
        Dictionary<TerminalCondition, DateTimeOffset> present, DateTimeOffset now)
    {
        // Values kept from before the outage are outdated: no new alarms from them.
        if (present.TryGetValue(TerminalCondition.Unreachable, out var since))
        {
            return new() { [TerminalCondition.Unreachable] = since };
        }
        return present
            .Where(condition => TerminalHealthEvaluator.IsAlarm(condition.Key, condition.Value, now))
            .ToDictionary();
    }

    private Entry Get(string terminalId)
    {
        if (!_entries.TryGetValue(terminalId, out var entry))
        {
            entry = new Entry();
            _entries[terminalId] = entry;
        }
        return entry;
    }

    private sealed class Entry
    {
        public TerminalHealthReport? Report { get; set; }
        public DateTimeOffset? ReceivedAt { get; set; }
        public Dictionary<TerminalCondition, DateTimeOffset> Present { get; set; } = [];
        public Dictionary<TerminalCondition, DateTimeOffset> Notified { get; } = [];
    }
}
