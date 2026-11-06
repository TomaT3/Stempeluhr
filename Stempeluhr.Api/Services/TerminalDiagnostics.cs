using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Text.RegularExpressions;
using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

/// <summary>Ein erlaubtes Ereignis des Kiosks; <see cref="At"/> ist die Gerätezeit.</summary>
public sealed record TerminalEvent(
    string? Kind, string? Name, string? Operation, string? Code, string? EventId, string? EmployeeId,
    double? Status, double? DurationMs, string? At, double? Seq,
    string? Screen, string? Blocked, bool? Busy, bool? Offline, double? Pending, double? Rejected,
    string? Method, string? Reason, string? Route, string? Via, string? What, string? Action, double? Fallback)
{
    /// <summary>session.open, http.clock, error.storage oder state; null bei unbekannter Art.</summary>
    [JsonIgnore]
    public string? Label => Kind switch
    {
        "flow" => Name,
        "http" when Operation is not null => "http." + Operation,
        "error" when Code is not null => "error." + Code,
        "state" => "state",
        _ => null,
    };
}

/// <summary>Only explicitly allowed technical fields may enter central logs.</summary>
public static class TerminalDiagnostics
{
    private static readonly JsonSerializerOptions DetailOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Nur erlaubte Werte, und im Log soll die Zeit lesbar bleiben (+00:00).
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

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
        };
    }

    /// <summary>Die Ereignisse seit dem letzten Bericht, siehe <see cref="LogEvents"/>.</summary>
    public static IReadOnlyList<TerminalEvent> Events(JsonElement report) =>
        Array(Object(report, "ui"), "events").Take(80).Select(e => new TerminalEvent(
            Kind: Choice(e, "kind", "http", "error", "state", "flow"),
            Name: Choice(e, "name", "session.open", "session.end", "scan.identified", "scan.unknown", "stamp.route", "flow.discarded"),
            Operation: Choice(e, "operation", "clock", "sync", "login", "identify", "hours", "hints", "health"),
            Code: Choice(e, "code", "javascript", "promise", "storage"),
            EventId: SafeIdentifier(e, "eventId"),
            EmployeeId: SafeIdentifier(e, "employeeId"),
            Status: Number(e, "status", -1, 599),
            DurationMs: Number(e, "durationMs"),
            At: Timestamp(e, "at"),
            Seq: Number(e, "seq", 0, 9_007_199_254_740_991),
            Screen: Choice(e, "screen", "idle", "session"),
            Blocked: Choice(e, "blocked", "none", "request", "backlog", "status"),
            Busy: Boolean(e, "busy"),
            Offline: Boolean(e, "offline"),
            Pending: Number(e, "pending"),
            Rejected: Number(e, "rejected"),
            Method: Choice(e, "method", "card", "pin", "pin-offline"),
            Reason: Choice(e, "reason", "user", "idle", "done", "recovered", "flow-timeout", "replaced", "status", "offline", "failed"),
            Route: Choice(e, "route", "agent", "direct", "queue"),
            Via: Choice(e, "via", "cache", "server", "offline", "error"),
            What: Choice(e, "what", "clock", "identify", "hours", "hints", "status"),
            Action: Choice(e, "action", "start", "stop", "pauseStart", "pauseEnd", "switch"),
            Fallback: Number(e, "fallback", -1, 599))).ToArray();

    /// <summary>
    /// Jedes Ereignis als eigener Log-Eintrag. Die Attribute heißen wie an den
    /// Spans (<c>stempeluhr.event_id</c>, <c>.employee_id</c>, <c>.employee_name</c>,
    /// <c>.terminal_id</c>), damit dieselbe Abfrage beide findet. Den Namen
    /// ergänzt erst der Server: Auf dem Pi und in der Konsole steht nur die ID.
    /// </summary>
    public static void LogEvents(ILogger logger, string terminalId, JsonElement report, RuntimeSettings settings)
    {
        foreach (var entry in Events(report))
        {
            if (entry.Label is not { } label) continue;

            var employeeName = entry.EmployeeId is { } id
                ? settings.Employees.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase))?.DisplayName
                : null;
            var attributes = new Dictionary<string, object?>
            {
                ["stempeluhr.kiosk_event"] = label,
                ["stempeluhr.terminal_id"] = terminalId,
                ["stempeluhr.device_time"] = entry.At,
                ["stempeluhr.seq"] = entry.Seq,
                ["stempeluhr.event_id"] = entry.EventId,
                ["stempeluhr.employee_id"] = entry.EmployeeId,
                ["stempeluhr.employee_name"] = employeeName,
                ["stempeluhr.action"] = entry.Action,
                ["stempeluhr.method"] = entry.Method,
                ["stempeluhr.reason"] = entry.Reason,
                ["stempeluhr.route"] = entry.Route,
                ["stempeluhr.fallback"] = entry.Fallback,
                ["stempeluhr.via"] = entry.Via,
                ["stempeluhr.what"] = entry.What,
                ["stempeluhr.http_status"] = entry.Status,
                ["stempeluhr.duration_ms"] = entry.DurationMs,
                ["stempeluhr.screen"] = entry.Screen,
                ["stempeluhr.blocked"] = entry.Blocked,
                ["stempeluhr.busy"] = entry.Busy,
                ["stempeluhr.offline"] = entry.Offline,
                ["stempeluhr.pending"] = entry.Pending,
                ["stempeluhr.rejected"] = entry.Rejected,
            }.Where(pair => pair.Value is not null).ToDictionary();

            using (logger.BeginScope(attributes))
            {
                logger.LogInformation(
                    "Terminal {TerminalId} event {KioskEvent} at {DeviceTime}: {Details}",
                    terminalId, label, entry.At, JsonSerializer.Serialize(entry, DetailOptions));
            }
        }
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
