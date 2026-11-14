using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

public sealed class TerminalDiagnosticsTests
{
    [Theory]
    [InlineData("{\"pcscdRssKb\":400,\"pcscdAnonymousKb\":300,\"pcscdSwapKb\":600,\"pcscdAnonymousAndSwapKb\":1,\"pcscdPid\":726,\"pcscdStartTicks\":2000000000,\"pcscdVersion\":\"2.5.2-1~stempeluhr13.1\"}", 900.0)]
    [InlineData("{\"pcscdAnonymousKb\":300}", null)]
    [InlineData("{\"pcscdAnonymousKb\":300,\"pcscdSwapKb\":-1}", null)]
    public void PcscMemoryIsValidatedAndTotalRequiresBothComponents(string system, double? expectedTotal)
    {
        using var document = JsonDocument.Parse("{\"system\":" + system + "}");
        var report = TerminalDiagnostics.Summarize(document.RootElement);
        var filtered = JsonSerializer.SerializeToElement(TerminalDiagnostics.Filter(document.RootElement));
        Assert.Equal(expectedTotal, report.PcscdAnonymousAndSwapKb);
        Assert.Equal(expectedTotal, filtered.GetProperty("pcscdAnonymousAndSwapKb").Deserialize<double?>());
        if (expectedTotal is not null)
        {
            Assert.Equal(2_000_000_000, report.PcscdStartTicks);
            Assert.Equal("2.5.2-1~stempeluhr13.1", report.PcscdVersion);
            var line = TerminalMetricsLineProtocol.Format(new("pi", report, DateTimeOffset.UnixEpoch));
            Assert.Contains("pcscd_anonymous_and_swap_kb=900", line);
            Assert.Contains("pcscd_version=2.5.2-1~stempeluhr13.1", line);
        }
    }

    [Theory]
    [InlineData("bad version!")]
    [InlineData("<script>")]
    public void InvalidPcscPackageVersionIsNotForwarded(string version)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { system = new { pcscdVersion = version } }));
        Assert.Null(TerminalDiagnostics.Summarize(document.RootElement).PcscdVersion);
    }

    [Fact]
    public void RetainsRequestAgeAndEventTimelineAndStateWithoutSecrets()
    {
        using var document = JsonDocument.Parse("""
            {"ui":{"requests":[{"operation":"clock","ageMs":12000,"pin":"SECRET"}],
            "events":[{"kind":"state","at":"2026-10-03T16:00:00Z","seq":9,
            "screen":"session","blocked":"request","busy":true,"offline":false,"pending":2,"rejected":1,
            "name":"SECRET"},{"kind":"error","at":"SECRET"}]}}
            """);
        var output = JsonSerializer.SerializeToElement(TerminalDiagnostics.Filter(document.RootElement));
        Assert.Equal(12000, output.GetProperty("requests")[0].GetProperty("ageMs").GetDouble());
        var events = TerminalDiagnostics.Events(document.RootElement);
        var state = events[0];
        Assert.Equal("2026-10-03T16:00:00.0000000+00:00", state.At);
        Assert.Equal("request", state.Blocked);
        Assert.True(state.Busy);
        Assert.Equal(2, state.Pending);
        Assert.DoesNotContain("SECRET", output.ToString());
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(events));
    }

    [Fact]
    public void KeepsTheWorkTimeHintsOperation()
    {
        using var document = JsonDocument.Parse("""
            {"ui":{"requests":[{"operation":"hints","ageMs":5}],
            "events":[{"kind":"http","operation":"hints","status":503},{"kind":"http","operation":"SECRET"}]}}
            """);
        var output = JsonSerializer.SerializeToElement(TerminalDiagnostics.Filter(document.RootElement));
        Assert.Equal("hints", output.GetProperty("requests")[0].GetProperty("operation").GetString());
        var events = TerminalDiagnostics.Events(document.RootElement);
        Assert.Equal("hints", events[0].Operation);
        Assert.DoesNotContain("SECRET", output.ToString());
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(events));
    }

    [Fact]
    public void FiltersSecretsAndUnboundedUntrustedStrings()
    {
        using var document = JsonDocument.Parse("""
            {"agentVersion":"0.17.1","uiStatus":"missing","heartbeatAgeSeconds":75,
             "token":"SECRET","ui":{"screen":"session","pin":"SECRET","name":"SECRET",
             "appVersion":"0.17.1","blocked":"status","message":"SECRET","pending":2,
             "events":[{"kind":"http","operation":"clock","eventId":"abc-123","status":400,
             "durationMs":100,"body":"SECRET"},{"kind":"error","code":"SECRET"}]},
             "system":{"availableMemoryKb":800000,"temperatureC":68,"extra":"SECRET"}}
            """);
        var output = JsonSerializer.Serialize(TerminalDiagnostics.Filter(document.RootElement));
        var events = JsonSerializer.Serialize(TerminalDiagnostics.Events(document.RootElement));
        Assert.DoesNotContain("SECRET", output);
        Assert.DoesNotContain("SECRET", events);
        Assert.Contains("abc-123", events);
        Assert.Contains("availableMemoryKb", output);
        Assert.Contains("missing", output);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"ui\":[],\"system\":1}")]
    [InlineData("{\"ui\":{\"pending\":-1,\"events\":[null,{}]}}")]
    public void MalformedOptionalFieldsCannotCrashFiltering(string json)
    {
        using var document = JsonDocument.Parse(json);
        TerminalDiagnostics.Filter(document.RootElement);
        TerminalDiagnostics.Events(document.RootElement);
        TerminalDiagnostics.Summarize(document.RootElement);
    }

    [Fact]
    public void SummarizeKeepsTheMonitoredFieldsOnly()
    {
        using var document = JsonDocument.Parse("""
            {"agentVersion":"0.17.1","uiStatus":"missing","heartbeatAgeSeconds":75,"token":"SECRET",
             "ui":{"appVersion":"bad version!","pending":2,"offline":true},
             "system":{"cpuPercent":12.5,"temperatureC":-5,"throttledFlags":327685,"diskFreeMb":"SECRET"}}
            """);

        var report = TerminalDiagnostics.Summarize(document.RootElement);

        Assert.Equal("0.17.1", report.AgentVersion);
        Assert.Null(report.AppVersion);
        Assert.Equal("missing", report.UiStatus);
        Assert.Equal(75, report.HeartbeatAgeSeconds);
        Assert.Equal(2, report.Pending);
        Assert.True(report.Offline);
        Assert.Equal(12.5, report.CpuPercent);
        Assert.Equal(-5, report.TemperatureC);
        Assert.Equal(327685, report.ThrottledFlags);
        Assert.Null(report.DiskFreeMb);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(report));
    }

    private static readonly RuntimeSettings NamedEmployee = new()
    {
        Employees = { new EmployeeSettings { Id = "emp-1", DisplayName = "Max Mustermann" } },
    };

    /// <summary>Über den OTel-Logger wie in Betrieb: Die Scope-Werte werden zu Attributen.</summary>
    private static List<(string? Message, Dictionary<string, object?> Attributes)> Export(string json)
    {
        var records = new List<LogRecord>();
        using (var factory = LoggerFactory.Create(logging => logging.AddOpenTelemetry(options =>
        {
            options.IncludeScopes = true;
            options.IncludeFormattedMessage = true;
            options.AddInMemoryExporter(records);
        })))
        {
            using var document = JsonDocument.Parse(json);
            TerminalDiagnostics.LogEvents(factory.CreateLogger("test"), "pi-1", document.RootElement, NamedEmployee);
        }

        return records.Select(record =>
        {
            var attributes = new Dictionary<string, object?>();
            record.ForEachScope((scope, state) =>
            {
                foreach (var pair in scope) state[pair.Key] = pair.Value;
            }, attributes);
            return (record.FormattedMessage, attributes);
        }).ToList();
    }

    [Fact]
    public void EachEventIsItsOwnEntry_WithDeviceTimeAndTheSpanAttributeNames()
    {
        var entries = Export("""
            {"ui":{"events":[
              {"kind":"flow","name":"session.open","employeeId":"emp-1","method":"card","at":"2026-11-06T00:36:41Z","seq":21},
              {"kind":"flow","name":"stamp.route","employeeId":"emp-1","eventId":"ev1","action":"stop","route":"direct","fallback":404,"at":"2026-11-06T00:36:42Z","seq":22},
              {"kind":"http","operation":"clock","status":200,"durationMs":482,"eventId":"ev1","at":"2026-11-06T00:36:42Z","seq":23}
            ]}}
            """);

        Assert.Equal(3, entries.Count);
        var open = entries[0].Attributes;
        Assert.Equal("session.open", open["stempeluhr.kiosk_event"]);
        Assert.Equal("pi-1", open["stempeluhr.terminal_id"]);
        Assert.Equal("2026-11-06T00:36:41.0000000+00:00", open["stempeluhr.device_time"]);
        Assert.Equal(21d, open["stempeluhr.seq"]);
        Assert.Equal("Max Mustermann", open["stempeluhr.employee_name"]);
        Assert.Equal("card", open["stempeluhr.method"]);
        Assert.False(open.ContainsKey("stempeluhr.route"));

        var route = entries[1].Attributes;
        Assert.Equal("ev1", route["stempeluhr.event_id"]);
        Assert.Equal("direct", route["stempeluhr.route"]);
        Assert.Equal(404d, route["stempeluhr.fallback"]);

        Assert.Contains("+00:00", entries[0].Message);
        Assert.DoesNotContain("label", entries[0].Message);
        Assert.Equal("http.clock", entries[2].Attributes["stempeluhr.kiosk_event"]);
        Assert.Equal(200d, entries[2].Attributes["stempeluhr.http_status"]);
    }

    [Fact]
    public void TheNameComesFromTheServer_NotFromTheKiosk_AndStaysOutOfTheMessage()
    {
        var entry = Assert.Single(Export("""
            {"ui":{"events":[{"kind":"flow","name":"session.open","employeeId":"emp-1","displayName":"SECRET","employeeName":"SECRET"}]}}
            """));

        Assert.Equal("Max Mustermann", entry.Attributes["stempeluhr.employee_name"]);
        Assert.DoesNotContain("Mustermann", entry.Message);
        Assert.DoesNotContain("SECRET", entry.Message);
    }

    [Fact]
    public void UnknownEmployeeHasNoName()
    {
        var entry = Assert.Single(Export("""{"ui":{"events":[{"kind":"flow","name":"session.open","employeeId":"nobody"}]}}"""));

        Assert.Equal("nobody", entry.Attributes["stempeluhr.employee_id"]);
        Assert.False(entry.Attributes.ContainsKey("stempeluhr.employee_name"));
    }

    [Theory]
    [InlineData("""{"kind":"flow","name":"SECRET"}""")]
    [InlineData("""{"kind":"flow"}""")]
    [InlineData("""{"kind":"SECRET","name":"session.open"}""")]
    [InlineData("""{"kind":"http","operation":"SECRET"}""")]
    public void EventsOutsideTheAllowlistAreNotLogged(string entry)
    {
        Assert.Empty(Export("{\"ui\":{\"events\":[" + entry + "]}}"));
    }

    [Fact]
    public void FlowEventsDropEverythingNotAllowed()
    {
        var entry = Assert.Single(Export("""
            {"ui":{"events":[{"kind":"flow","name":"scan.unknown","cardId":"04ABCDEF","nfcCardId":"04ABCDEF","pin":"1234",
             "employeeId":"<script>","eventId":"has space","via":"SECRET","route":"SECRET","reason":"SECRET","fallback":9999}]}}
            """));

        var exported = entry.Message + JsonSerializer.Serialize(entry.Attributes);
        Assert.DoesNotContain("04ABCDEF", exported);
        Assert.DoesNotContain("1234", exported);
        Assert.DoesNotContain("SECRET", exported);
        Assert.DoesNotContain("script", exported);
        Assert.False(entry.Attributes.ContainsKey("stempeluhr.fallback"));
    }

    [Fact]
    public void TheSnapshotLineNoLongerRepeatsTheEvents()
    {
        using var document = JsonDocument.Parse("""{"ui":{"events":[{"kind":"error","code":"promise"}]}}""");

        var output = JsonSerializer.SerializeToElement(TerminalDiagnostics.Filter(document.RootElement));

        Assert.False(output.TryGetProperty("events", out _));
    }

    [Theory]
    [InlineData(StampMethod.Pin, null, false, "PIN ohne Terminal", null)]
    [InlineData(StampMethod.Pin, "pi-1", false, "PIN am Terminal pi-1", "PIN am Terminal pi-1")]
    [InlineData(StampMethod.Card, null, false, "Karte am Terminal", "Karte am Terminal")]
    [InlineData(null, null, false, "unbekannt", null)]
    [InlineData(StampMethod.Pin, null, true, "Webbrowser", "Webbrowser")]
    public void TheLogNamesThePin_WhileTheTelegramTextStaysAsBefore(
        StampMethod? method, string? terminal, bool web, string log, string? telegram)
    {
        var origin = new StampOrigin(method, terminal, web);

        Assert.Equal(log, origin.ToString());
        Assert.Equal(telegram, origin.Describe());
    }
}
