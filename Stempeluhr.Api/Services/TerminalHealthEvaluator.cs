namespace Stempeluhr.Api.Services;

/// <summary>Allowed technical fields of a terminal diagnostics report (see <see cref="TerminalDiagnostics"/>).</summary>
public sealed record TerminalHealthReport(
    string? AgentVersion,
    string? AppVersion,
    string? UiStatus,
    double? HeartbeatAgeSeconds,
    string? Screen,
    string? Blocked,
    bool? Busy,
    bool? Offline,
    double? Pending,
    double? Rejected,
    double? CpuPercent,
    double? AvailableMemoryKb,
    double? ChromiumRssSumKb,
    double? TemperatureC,
    double? ThrottledFlags,
    double? DiskFreeMb,
    double? UptimeSeconds,
    double? Load1,
    double? PcscdRssKb = null,
    double? PcscdAnonymousKb = null,
    double? PcscdSwapKb = null,
    double? PcscdAnonymousAndSwapKb = null,
    double? PcscdPid = null,
    double? PcscdStartTicks = null,
    string? PcscdVersion = null);

public enum TerminalCondition
{
    Unreachable,
    PageHung,
    QueueBacklog,
    Power,
    Temperature,
    Memory,
    Cpu,
    Disk,
}

/// <summary>
/// Decides from the latest report which problems a terminal has. Pure and
/// without I/O like <see cref="WorkTimeLimitCalculator"/>; all limits live here.
/// A condition is present from the first report that triggers it until a
/// report clears it. Values between both limits (hysteresis) and missing
/// values keep the previous state. It counts as an alarm once it has been
/// present for its hold time.
/// </summary>
public static class TerminalHealthEvaluator
{
    /// <summary>The agent reports every minute: five missed reports.</summary>
    public static readonly TimeSpan UnreachableAfter = TimeSpan.FromMinutes(5);

    public const double TemperatureAlarmC = 80, TemperatureClearC = 75;
    public const double MemoryAlarmMb = 100, MemoryClearMb = 150;
    public const double CpuAlarmPercent = 90, CpuClearPercent = 70;
    public const double DiskAlarmMb = 500, DiskClearMb = 750;

    /// <summary>Bits 0-3 of <c>vcgencmd get_throttled</c>: currently active.</summary>
    private const int CurrentThrottlingMask = 0xF;

    private sealed record Rule(TimeSpan Hold, Func<TerminalHealthReport, bool?> Triggered);

    private static readonly Dictionary<TerminalCondition, Rule> Rules = new()
    {
        [TerminalCondition.PageHung] = new(TimeSpan.FromMinutes(3), report => report.UiStatus switch
        {
            "missing" or "not-seen" => true,
            "alive" => false,
            _ => null,
        }),
        [TerminalCondition.QueueBacklog] = new(TimeSpan.FromMinutes(15), report => report.Pending switch
        {
            null => null,
            var pending => pending > 0,
        }),
        [TerminalCondition.Power] = new(TimeSpan.FromMinutes(2), report => report.ThrottledFlags switch
        {
            null => null,
            var flags => ((int)flags & CurrentThrottlingMask) != 0,
        }),
        [TerminalCondition.Temperature] = new(TimeSpan.FromMinutes(5),
            report => Hysteresis(report.TemperatureC, value => value >= TemperatureAlarmC, value => value < TemperatureClearC)),
        [TerminalCondition.Memory] = new(TimeSpan.FromMinutes(5),
            report => Hysteresis(report.AvailableMemoryKb / 1024, value => value < MemoryAlarmMb, value => value > MemoryClearMb)),
        [TerminalCondition.Cpu] = new(TimeSpan.FromMinutes(10),
            report => Hysteresis(report.CpuPercent, value => value > CpuAlarmPercent, value => value < CpuClearPercent)),
        [TerminalCondition.Disk] = new(TimeSpan.Zero,
            report => Hysteresis(report.DiskFreeMb, value => value < DiskAlarmMb, value => value > DiskClearMb)),
    };

    /// <summary>
    /// Returns the conditions present now, each with the time it began.
    /// <paramref name="monitoringSince"/> stands in for the last report of a
    /// terminal that has not reported since the server started.
    /// </summary>
    public static Dictionary<TerminalCondition, DateTimeOffset> Evaluate(
        TerminalHealthReport? report,
        DateTimeOffset? receivedAt,
        DateTimeOffset monitoringSince,
        DateTimeOffset now,
        IReadOnlyDictionary<TerminalCondition, DateTimeOffset> present)
    {
        var lastContact = receivedAt ?? monitoringSince;
        if (now - lastContact > UnreachableAfter)
        {
            // Without new reports the other values are outdated: keep them
            // as they were, neither alarm nor all-clear.
            return new(present) { [TerminalCondition.Unreachable] = lastContact };
        }
        if (report is null) return [];

        var result = new Dictionary<TerminalCondition, DateTimeOffset>();
        foreach (var (condition, rule) in Rules)
        {
            var triggered = rule.Triggered(report);
            var wasPresent = present.TryGetValue(condition, out var since);
            if (triggered == true || (triggered is null && wasPresent))
            {
                result[condition] = wasPresent ? since : Began(condition, report, lastContact, now);
            }
        }
        return result;
    }

    /// <summary>True once the condition has lasted its hold time.</summary>
    public static bool IsAlarm(TerminalCondition condition, DateTimeOffset since, DateTimeOffset now) =>
        condition == TerminalCondition.Unreachable || now - since >= Rules[condition].Hold;

    /// <summary>Readable <c>vcgencmd get_throttled</c> bits: now and since boot.</summary>
    public static IReadOnlyList<string> DescribeThrottling(double? flags)
    {
        if (flags is null) return [];
        var value = (int)flags;
        string[] names = ["Unterspannung", "Takt begrenzt", "gedrosselt", "Temperaturgrenze"];
        var result = new List<string>();
        for (var bit = 0; bit < names.Length; bit++)
        {
            if ((value & (1 << bit)) != 0) result.Add($"{names[bit]} (jetzt)");
        }
        for (var bit = 0; bit < names.Length; bit++)
        {
            if ((value & (1 << (bit + 16))) != 0) result.Add($"{names[bit]} (seit Start)");
        }
        return result;
    }

    private static bool? Hysteresis(double? value, Func<double, bool> alarm, Func<double, bool> clear) =>
        value is not { } number ? null : alarm(number) ? true : clear(number) ? false : null;

    private static DateTimeOffset Began(
        TerminalCondition condition, TerminalHealthReport report, DateTimeOffset receivedAt, DateTimeOffset now) =>
        // The page hangs since its last heartbeat, not since the agent noticed.
        condition == TerminalCondition.PageHung && report.HeartbeatAgeSeconds is { } age
            ? receivedAt - TimeSpan.FromSeconds(age)
            : now;
}
