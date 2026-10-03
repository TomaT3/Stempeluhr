using System.Text.Json;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

public sealed class TerminalDiagnosticsTests
{
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
