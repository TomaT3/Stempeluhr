using System.Text.Json;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

public sealed class TerminalDiagnosticsTests
{
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
    }
}
