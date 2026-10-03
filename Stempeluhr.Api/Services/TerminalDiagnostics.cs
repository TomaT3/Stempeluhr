using System.Text.Json;
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
            pcscdRssKb = Number(system, "pcscdRssKb"), temperatureC = Number(system, "temperatureC"),
            throttledFlags = Number(system, "throttledFlags"), diskFreeMb = Number(system, "diskFreeMb"),
            uptimeSeconds = Number(system, "uptimeSeconds"), load1 = Number(system, "load1"),
            events = Array(ui, "events").Take(20).Select(e => new
            {
                kind = Choice(e, "kind", "http", "error", "state"),
                operation = Choice(e, "operation", "clock", "sync", "login", "identify", "hours", "health"),
                code = Choice(e, "code", "javascript", "promise", "storage"),
                eventId = SafeIdentifier(e, "eventId"),
                status = Number(e, "status", -1, 599), durationMs = Number(e, "durationMs"),
            }).ToArray(),
        };
    }

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
    private static string? SafeIdentifier(JsonElement value, string key, string pattern = @"\A[0-9A-Za-z-]{1,64}\z") =>
        Object(value, key) is { ValueKind: JsonValueKind.String } child && child.GetString() is { Length: <= 64 } text
        && Regex.IsMatch(text, pattern) ? text : null;
}
