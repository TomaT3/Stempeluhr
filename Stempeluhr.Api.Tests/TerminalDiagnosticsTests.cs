using System.Text.Json;
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
        var state = output.GetProperty("events")[0];
        Assert.Equal("2026-10-03T16:00:00.0000000+00:00", state.GetProperty("at").GetString());
        Assert.Equal("request", state.GetProperty("blocked").GetString());
        Assert.True(state.GetProperty("busy").GetBoolean());
        Assert.Equal(2, state.GetProperty("pending").GetDouble());
        Assert.DoesNotContain("SECRET", output.ToString());
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
        Assert.Equal("hints", output.GetProperty("events")[0].GetProperty("operation").GetString());
        Assert.DoesNotContain("SECRET", output.ToString());
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
        Assert.DoesNotContain("SECRET", output);
        Assert.Contains("abc-123", output);
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
}
