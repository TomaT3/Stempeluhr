using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

/// <summary>
/// Logs und Traces gehen per OTLP an OpenObserve. Ohne Endpunkt bleibt alles
/// wie bisher; mit Endpunkt dürfen weder Telegram-Bot-Token noch Query-Strings
/// das Haus verlassen, und Hintergrund-Abfragen übertönen die Stempel nicht.
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

    [Theory]
    [InlineData("https://api.telegram.org/bot123:SECRET/getUpdates", false)]
    [InlineData("http://nas:8086/api/v2/write?org=home", false)]
    [InlineData("https://api.telegram.org/bot123:SECRET/sendMessage", true)]
    [InlineData("https://kimai.example.com/api/timesheets", true)]
    public void IsTracedHttpCall_SkipsPollingAndInflux(string url, bool traced)
    {
        Assert.Equal(traced, TelemetrySetup.IsTracedHttpCall(new HttpRequestMessage(HttpMethod.Post, url)));
    }

    [Fact]
    public void ServiceVersion_IsTheReleaseTag_NotThePinnedAssemblyVersion()
    {
        var expected = typeof(TelemetrySetup).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        Assert.Equal(expected, TelemetrySetup.ServiceVersion);
        Assert.NotEqual("0.0.0.0", TelemetrySetup.ServiceVersion);
    }

    [Fact]
    public void KimaiHttpLogs_AreRaisedOnlyForTheExporter()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration[TelemetrySetup.EndpointKey] = "http://nas:5080/api/default";
        builder.AddStempeluhrTelemetry();

        using var services = builder.Services.BuildServiceProvider();
        var rule = Assert.Single(
            services.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.Rules,
            rule => rule.CategoryName == TelemetrySetup.KimaiHttpLogCategory);

        Assert.Equal(typeof(OpenTelemetryLoggerProvider).FullName, rule.ProviderName);
        Assert.Equal(LogLevel.Warning, rule.LogLevel);
    }

    /// <summary>
    /// appsettings.Development.json hat keinen Telegram-Filter, und ein Operator
    /// kann das Level zum Debuggen hochsetzen: Der Token bleibt trotzdem draußen.
    /// </summary>
    [Theory]
    [InlineData("System.Net.Http.HttpClient.Telegram.LogicalHandler")]
    [InlineData("System.Net.Http.HttpClient.TelegramPoll.ClientHandler")]
    public void TelegramHttpLogs_NeverReachTheExporter_WhateverLevelIsConfigured(string category)
    {
        var exported = new List<LogRecord>();
        using (var factory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddFilter(TelemetrySetup.TelegramHttpLogCategory, LogLevel.Trace);
            logging.AddStempeluhrExportLogFilters();
            logging.AddOpenTelemetry(options => options.AddInMemoryExporter(exported));
        }))
        {
            var logger = factory.CreateLogger(category);
            logger.LogInformation("Sending HTTP request POST https://api.telegram.org/bot123:SECRET/sendMessage");
            logger.LogError("POST https://api.telegram.org/bot123:SECRET/sendMessage");
            factory.CreateLogger<TelemetryTests>().LogInformation("control");
        }

        Assert.Equal("control", Assert.Single(exported).Body);
    }

    /// <summary>
    /// Über den echten OTLP-Exporter gegen einen lokalen Empfänger: Das Limit
    /// steckt in einer SDK-Variable, die sonst niemand prüft. Ein Client kann
    /// über /api/kiosk/clock/sync vor der Anmeldung MB-große Werte schicken.
    /// </summary>
    [Fact]
    public async Task OtlpExport_CutsOversizedClientValues_InSpansAndLogs()
    {
        var oversized = new string('x', 1_000_000);
        var received = new ConcurrentDictionary<string, long>();

        var collectorBuilder = WebApplication.CreateBuilder();
        collectorBuilder.Logging.ClearProviders();
        collectorBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var collector = collectorBuilder.Build();
        collector.MapPost("/v1/{signal}", async (string signal, HttpRequest request) =>
        {
            using var body = new MemoryStream();
            await request.Body.CopyToAsync(body);
            received.AddOrUpdate(signal, body.Length, (_, size) => Math.Max(size, body.Length));
            return Results.Ok();
        });
        await collector.StartAsync();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration[TelemetrySetup.EndpointKey] = collector.Urls.First();
        builder.AddStempeluhrTelemetry();
        await using (var services = builder.Services.BuildServiceProvider())
        {
            var tracer = services.GetRequiredService<TracerProvider>();
            var logs = services.GetRequiredService<LoggerProvider>();
            var failure = new InvalidOperationException($"Unbekannte Aktion: {oversized}");

            using (var span = StempeluhrTelemetry.StartStamp("offline.apply", oversized, oversized, oversized, null))
            {
                StempeluhrTelemetry.TagFailure(span, "rejected", failure);
            }

            services.GetRequiredService<ILogger<TelemetryTests>>()
                .LogWarning(failure, "Offline kiosk event {EventId} rejected", oversized);

            Assert.True(tracer.ForceFlush());
            Assert.True(logs.ForceFlush());
        }

        // Ein paar Felder à 8 KiB; andere Testklassen können kleine Spans beisteuern.
        Assert.InRange(received["traces"], TelemetrySetup.MaxExportedValueLength, 200_000);
        Assert.InRange(received["logs"], TelemetrySetup.MaxExportedValueLength, 200_000);
    }

    [Fact]
    public void Exporter_KeepsCallsOfAStamp_AndFailedBackgroundCalls_ButNoBotToken()
    {
        var exported = new List<Activity>();
        using var source = new ActivitySource("Stempeluhr.Tests.Redaction." + Guid.NewGuid());
        using (var provider = Sdk.CreateTracerProviderBuilder()
            .AddSource(source.Name)
            .AddProcessor(new TelemetryRedactionProcessor())
            .AddInMemoryExporter(exported)
            .Build())
        {
            using (source.StartActivity("kiosk.clock"))
            using (var send = source.StartActivity("POST", ActivityKind.Client))
            {
                send!.SetTag("url.full", "https://api.telegram.org/bot123456:SECRET/sendMessage");
                send.SetTag("url.path", "/bot123456:SECRET/sendMessage");
            }

            using (var background = source.StartActivity("GET", ActivityKind.Client))
            {
                background!.SetTag("url.full", "https://kimai.example.com/api/timesheets?marker=ok");
            }

            using (var failed = source.StartActivity("GET", ActivityKind.Client))
            {
                failed!.SetTag("url.full", "https://kimai.example.com/api/users/me");
                failed.SetStatus(ActivityStatusCode.Error);
            }

            provider!.ForceFlush();
        }

        var urls = exported.Select(span => span.GetTagItem("url.full")).OfType<string>().ToArray();
        Assert.Equal(["https://api.telegram.org/bot***/sendMessage", "https://kimai.example.com/api/users/me"], urls.Order());
        Assert.Contains(exported, span => span.OperationName == "kiosk.clock");
        var telegram = exported.Single(span => span.GetTagItem("url.path") is not null);
        Assert.Equal("/bot***/sendMessage", telegram.GetTagItem("url.path"));
        Assert.DoesNotContain(exported.SelectMany(span => span.TagObjects), tag => tag.Value is string value && value.Contains("SECRET"));
    }

    /// <summary>
    /// Über einen echten <see cref="HttpClient"/>, nicht über gesetzte Tags:
    /// Benennt ein Update der Instrumentierung <c>url.full</c> um, fällt das
    /// hier auf, statt dass der Token unbemerkt exportiert wird. Kein Netz: Der
    /// Verbindungsaufbau scheitert immer, solche Aufrufe bleiben sichtbar.
    /// </summary>
    [Fact]
    public async Task RealHttpClient_RedactsTheToken_AndNeverRecordsPolling()
    {
        var marker = "marker" + Guid.NewGuid().ToString("N");
        var exported = new List<Activity>();
        using var client = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = (_, _) => throw new IOException("no network in this test"),
        });

        using (var provider = Sdk.CreateTracerProviderBuilder()
            .AddStempeluhrHttpTracing()
            .AddInMemoryExporter(exported)
            .Build())
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync(
                $"http://api.telegram.org/bot123456:SECRET/{marker}?token=QUERYSECRET", null));
            await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync(
                $"http://api.telegram.org/bot123456:{marker}/getUpdates", null));
            provider!.ForceFlush();
        }

        var urls = exported.Select(span => span.GetTagItem("url.full")).OfType<string>().Where(url => url.Contains(marker)).ToArray();
        Assert.Equal($"http://api.telegram.org/bot***/{marker}", Assert.Single(urls));
        Assert.DoesNotContain(exported.SelectMany(span => span.TagObjects), tag => tag.Value is string value && value.Contains("SECRET"));
    }

    [Fact]
    public void RunDetached_HidesTheSpanInside_AndRestoresIt()
    {
        using var parent = new Activity("stamp").Start();
        Activity? inside = parent;

        StempeluhrTelemetry.RunDetached(() => inside = Activity.Current);

        Assert.Null(inside);
        Assert.Same(parent, Activity.Current);
    }
}

/// <summary>
/// Sammelt Stempel-Spans einer Trace, wie unter einem eingehenden
/// <c>traceparent</c>. Nur diese Trace: andere Testklassen laufen parallel.
/// </summary>
internal static class SpanCapture
{
    public static async Task<List<Activity>> InTraceAsync(Func<Task> act)
    {
        var traceId = ActivityTraceId.CreateRandom();
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == StempeluhrTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.TraceId != traceId) return;
                lock (spans) spans.Add(activity);
            },
        };
        ActivitySource.AddActivityListener(listener);

        using (new Activity("incoming").SetParentId(traceId, ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded).Start())
        {
            await act();
        }
        return spans;
    }
}
