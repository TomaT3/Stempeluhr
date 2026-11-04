using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

/// <summary>
/// Logs und Traces gehen per OTLP an OpenObserve. Ohne Endpunkt bleibt alles
/// wie bisher; mit Endpunkt dürfen weder Telegram-Bot-Token noch Query-Strings
/// das Haus verlassen.
/// </summary>
public sealed class TelemetryTests
{

    [Fact]
    public void IsEnabled_WithoutEndpoint_IsFalse()
    {
        var empty = new ConfigurationBuilder().Build();
        var configured = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [TelemetrySetup.EndpointKey] = "http://nas:5080/api/default" })
            .Build();

        Assert.False(TelemetrySetup.IsEnabled(empty));
        Assert.True(TelemetrySetup.IsEnabled(configured));
    }

    [Theory]
    [InlineData("/api/kiosk/clock", true)]
    [InlineData("/api/kiosk/clock/sync", true)]
    [InlineData("/api/health", false)]
    [InlineData("/terminal", false)]
    [InlineData("/pi/agent.tar.gz", false)]
    [InlineData("/main-ABC.js", false)]
    public void IsTracedRequest_OnlyApiWithoutHealth(string path, bool traced)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        Assert.Equal(traced, TelemetrySetup.IsTracedRequest(context));
    }

    [Theory]
    [InlineData("https://api.telegram.org/bot123456:SECRET/sendMessage", "https://api.telegram.org/bot***/sendMessage")]
    [InlineData("https://kimai.example.com/api/timesheets?user=5&begin=x", "https://kimai.example.com/api/timesheets")]
    [InlineData("https://kimai.example.com/api/bottles", "https://kimai.example.com/api/bottles")]
    public void Redact_RemovesBotTokenAndQuery(string url, string expected)
    {
        Assert.Equal(expected, TelemetryRedactionProcessor.Redact(url));
    }

    [Fact]
    public void Exporter_NeverSeesBotToken_AndSkipsPolling()
    {
        var exported = new List<Activity>();
        using var source = new ActivitySource("Stempeluhr.Tests.Redaction." + Guid.NewGuid());
        using (var provider = Sdk.CreateTracerProviderBuilder()
            .AddSource(source.Name)
            .AddProcessor(new TelemetryRedactionProcessor())
            .AddInMemoryExporter(exported)
            .Build())
        {
            using (var send = source.StartActivity("POST"))
            {
                send!.SetTag("url.full", "https://api.telegram.org/bot123456:SECRET/sendMessage");
                send.SetTag("url.path", "/bot123456:SECRET/sendMessage");
            }
            using (var poll = source.StartActivity("POST"))
            {
                poll!.SetTag("url.full", "https://api.telegram.org/bot123456:SECRET/getUpdates");
            }
            using (var influx = source.StartActivity("POST"))
            {
                influx!.SetTag("url.full", "http://nas:8086/api/v2/write?org=home&bucket=stempeluhr");
            }
            provider!.ForceFlush();
        }

        var span = Assert.Single(exported);
        Assert.Equal("https://api.telegram.org/bot***/sendMessage", span.GetTagItem("url.full"));
        Assert.Equal("/bot***/sendMessage", span.GetTagItem("url.path"));
        Assert.DoesNotContain(span.TagObjects, tag => tag.Value is string value && value.Contains("SECRET"));
    }
}
