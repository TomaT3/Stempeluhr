using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Stempeluhr.Api.Services;

/// <summary>Only explicitly allowed technical fields may enter central logs.</summary>
public static class TerminalDiagnostics
{
    public static object Filter(JsonElement report)
    {
        var ui = Object(report, "ui");
        var system = Object(report, "system");
        return new
        {
            agentVersion = Version(report, "agentVersion"),
            uiStatus = Choice(report, "uiStatus", "alive", "missing", "not-seen"),
            heartbeatAgeSeconds = Number(report, "heartbeatAgeSeconds"),
            appVersion = Version(ui, "appVersion"),
            screen = Choice(ui, "screen", "idle", "session"),
            blocked = Choice(ui, "blocked", "none", "request", "backlog", "status"),
            busy = Boolean(ui, "busy"), offline = Boolean(ui, "offline"), visible = Boolean(ui, "visible"),
            pending = Number(ui, "pending"), rejected = Number(ui, "rejected"),
            lagMs = Number(ui, "lagMs"), lastInputAgeMs = Number(ui, "lastInputAgeMs"),
            cpuPercent = Number(system, "cpuPercent"), availableMemoryKb = Number(system, "availableMemoryKb"),
            chromiumProcesses = Number(system, "chromiumProcesses"), chromiumRssSumKb = Number(system, "chromiumRssSumKb"),
            pcscdRssKb = Number(system, "pcscdRssKb"),
            pcscdAnonymousKb = Number(system, "pcscdAnonymousKb"), pcscdSwapKb = Number(system, "pcscdSwapKb"),
            pcscdAnonymousAndSwapKb = AnonymousAndSwap(system),
            pcscdPid = Number(system, "pcscdPid", 1, int.MaxValue),
            pcscdStartTicks = Number(system, "pcscdStartTicks", 0, 9_007_199_254_740_991),
            pcscdVersion = PackageVersion(system, "pcscdVersion"), temperatureC = Number(system, "temperatureC"),
            throttledFlags = Number(system, "throttledFlags"), diskFreeMb = Number(system, "diskFreeMb"),
            uptimeSeconds = Number(system, "uptimeSeconds"), load1 = Number(system, "load1"),
            requests = Array(ui, "requests").Take(10).Select(r => new
            {
                operation = Choice(r, "operation", "clock", "sync", "login", "identify", "hours", "hints", "health"),
                ageMs = Number(r, "ageMs"),
            }).ToArray(),
            events = Array(ui, "events").Take(80).Select(e => new
            {
                kind = Choice(e, "kind", "http", "error", "state"),
                operation = Choice(e, "operation", "clock", "sync", "login", "identify", "hours", "hints", "health"),
                code = Choice(e, "code", "javascript", "promise", "storage"),
                eventId = SafeIdentifier(e, "eventId"),
                status = Number(e, "status", -1, 599), durationMs = Number(e, "durationMs"),
                at = Timestamp(e, "at"), seq = Number(e, "seq", 0, 9_007_199_254_740_991),
                screen = Choice(e, "screen", "idle", "session"),
                blocked = Choice(e, "blocked", "none", "request", "backlog", "status"),
                busy = Boolean(e, "busy"), offline = Boolean(e, "offline"),
                pending = Number(e, "pending"), rejected = Number(e, "rejected"),
            }).ToArray(),
        };
    }

    /// <summary>The same allowed fields as <see cref="Filter"/>, typed for monitoring.</summary>
    public static TerminalHealthReport Summarize(JsonElement report)
    {
        var ui = Object(report, "ui");
        var system = Object(report, "system");
        return new TerminalHealthReport(
            AgentVersion: Version(report, "agentVersion"),
            AppVersion: Version(ui, "appVersion"),
            UiStatus: Choice(report, "uiStatus", "alive", "missing", "not-seen"),
            HeartbeatAgeSeconds: Number(report, "heartbeatAgeSeconds"),
            Screen: Choice(ui, "screen", "idle", "session"),
            Blocked: Choice(ui, "blocked", "none", "request", "backlog", "status"),
            Busy: Boolean(ui, "busy"),
            Offline: Boolean(ui, "offline"),
            Pending: Number(ui, "pending"),
            Rejected: Number(ui, "rejected"),
            CpuPercent: Number(system, "cpuPercent"),
            AvailableMemoryKb: Number(system, "availableMemoryKb"),
            ChromiumRssSumKb: Number(system, "chromiumRssSumKb"),
            TemperatureC: Number(system, "temperatureC", -100),
            ThrottledFlags: Number(system, "throttledFlags", 0, int.MaxValue),
            DiskFreeMb: Number(system, "diskFreeMb"),
            UptimeSeconds: Number(system, "uptimeSeconds"),
            Load1: Number(system, "load1"),
            PcscdRssKb: Number(system, "pcscdRssKb"),
            PcscdAnonymousKb: Number(system, "pcscdAnonymousKb"),
            PcscdSwapKb: Number(system, "pcscdSwapKb"),
            PcscdAnonymousAndSwapKb: AnonymousAndSwap(system),
            PcscdPid: Number(system, "pcscdPid", 1, int.MaxValue),
            PcscdStartTicks: Number(system, "pcscdStartTicks", 0, 9_007_199_254_740_991),
            PcscdVersion: PackageVersion(system, "pcscdVersion"));
    }

    // Derive the total from validated components; a missing component is unknown.
    private static double? AnonymousAndSwap(JsonElement system) =>
        Number(system, "pcscdAnonymousKb") + Number(system, "pcscdSwapKb");

    private static string? PackageVersion(JsonElement value, string key) =>
        SafeIdentifier(value, key, @"\A[0-9A-Za-z.+:~\-]{1,64}\z");

    private static JsonElement Object(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var child) ? child : default;
    private static IEnumerable<JsonElement> Array(JsonElement value, string key) =>
        Object(value, key) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];
    private static double? Number(JsonElement value, string key, double minimum = 0, double maximum = 1_000_000_000) =>
        Object(value, key) is { ValueKind: JsonValueKind.Number } child && child.TryGetDouble(out var number)
        && double.IsFinite(number) && number >= minimum && number <= maximum ? number : null;
    private static bool? Boolean(JsonElement value, string key) => Object(value, key).ValueKind switch
    {
        JsonValueKind.True => true, JsonValueKind.False => false, _ => null
    };
    private static string? Choice(JsonElement value, string key, params string[] choices) =>
        Object(value, key) is { ValueKind: JsonValueKind.String } child && choices.Contains(child.GetString()) ? child.GetString() : null;
    private static string? Version(JsonElement value, string key) => SafeIdentifier(value, key, @"\A[0-9A-Za-z.+-]{1,64}\z");
    private static string? Timestamp(JsonElement value, string key) =>
        Object(value, key) is { ValueKind: JsonValueKind.String } child && child.GetString() is { Length: <= 40 } text
        && Regex.IsMatch(text, @"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})\z")
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : null;
    private static string? SafeIdentifier(JsonElement value, string key, string pattern = @"\A[0-9A-Za-z-]{1,64}\z") =>
        Object(value, key) is { ValueKind: JsonValueKind.String } child && child.GetString() is { Length: <= 64 } text
        && Regex.IsMatch(text, pattern) ? text : null;
}
