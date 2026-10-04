using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

public sealed class TerminalMetricsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private static readonly TerminalHealthReport Full = new(
        AgentVersion: "0.18.0", AppVersion: "0.18.1", UiStatus: "alive", HeartbeatAgeSeconds: 5,
        Screen: "idle", Blocked: "none", Busy: false, Offline: true, Pending: 2, Rejected: 0,
        CpuPercent: 12.5, AvailableMemoryKb: 500_000, ChromiumRssSumKb: 200_000, TemperatureC: 48.2,
        ThrottledFlags: 0x50005, DiskFreeMb: 10_000, UptimeSeconds: 86_400, Load1: 0.25);

    private static readonly TerminalHealthReport Empty = new(
        null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

    private static readonly RuntimeSettings Enabled = new()
    {
        InfluxUrl = "http://nas:8086/",
        InfluxOrg = "home lab",
        InfluxBucket = "stempeluhr",
        InfluxToken = " secret-token ",
    };

    [Fact]
    public void Format_FullReport_WritesTagsTypedFieldsAndSeconds()
    {
        var line = TerminalMetricsLineProtocol.Format(new("kiosk-1", Full, At));

        Assert.Equal(
            "terminal,terminal=kiosk-1,agent_version=0.18.0,app_version=0.18.1 "
            + "heartbeat_age_seconds=5,pending=2,rejected=0,cpu_percent=12.5,load1=0.25,"
            + "available_memory_kb=500000,chromium_rss_kb=200000,temperature_c=48.2,throttled_flags=327685i,"
            + "disk_free_mb=10000,uptime_seconds=86400,ui_alive=true,offline=true,busy=false "
            + At.ToUnixTimeSeconds(),
            line);
    }

    [Fact]
    public void Format_LeavesOutMissingValuesAndSkipsEmptyReports()
    {
        var line = TerminalMetricsLineProtocol.Format(new("kiosk-1", Empty with { TemperatureC = 50, UiStatus = "missing" }, At));

        Assert.Equal($"terminal,terminal=kiosk-1 temperature_c=50,ui_alive=false {At.ToUnixTimeSeconds()}", line);
        Assert.Null(TerminalMetricsLineProtocol.Format(new("kiosk-1", Empty with { AgentVersion = "1.0" }, At)));
    }

    [Fact]
    public void Format_EscapesTagValues()
    {
        var line = TerminalMetricsLineProtocol.Format(new("Empfang 1,a=b\\\n", Empty with { Load1 = 1 }, At));

        Assert.StartsWith(@"terminal,terminal=Empfang\ 1\,a\=b\\ load1=1 ", line);
    }

    [Fact]
    public void Format_UsesInvariantCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var line = TerminalMetricsLineProtocol.Format(new("kiosk-1", Empty with { CpuPercent = 1234.5 }, At));

            Assert.Contains("cpu_percent=1234.5 ", line);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Write_PostsLinesToTheV2WriteApiWithToken()
    {
        var handler = new RecordingHandler();
        var writer = CreateWriter(Enabled, handler, out _);

        await writer.WriteAsync([new("kiosk-1", Full, At), new("kiosk-2", Empty, At), new("kiosk-3", Full, At)]);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://nas:8086/api/v2/write?org=home%20lab&bucket=stempeluhr&precision=s", request.Uri);
        Assert.Equal("Token secret-token", request.Authorization);
        var lines = request.Body.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("terminal,terminal=kiosk-1,", lines[0]);
        Assert.StartsWith("terminal,terminal=kiosk-3,", lines[1]);
    }

    [Fact]
    public async Task Write_KeepsAPathPrefixAndOmitsAnEmptyOrg()
    {
        var handler = new RecordingHandler();
        var settings = new RuntimeSettings { InfluxUrl = "https://nas/influx", InfluxBucket = "db", InfluxToken = "t" };
        var writer = CreateWriter(settings, handler, out _);

        await writer.WriteAsync([new("kiosk-1", Full, At)]);

        Assert.Equal("https://nas/influx/api/v2/write?bucket=db&precision=s", Assert.Single(handler.Requests).Uri);
    }

    [Theory]
    [InlineData(null, "db", "t")]
    [InlineData("http://nas:8086", null, "t")]
    [InlineData("http://nas:8086", "db", " ")]
    [InlineData("nas:8086", "db", "t")]
    public async Task Write_WithoutCompleteConfiguration_SendsNothing(string? url, string? bucket, string? token)
    {
        var handler = new RecordingHandler();
        var writer = CreateWriter(new RuntimeSettings { InfluxUrl = url, InfluxBucket = bucket, InfluxToken = token }, handler, out _);

        await writer.WriteAsync([new("kiosk-1", Full, At)]);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Write_Failures_NeverThrowAndAreLoggedOncePerOutage()
    {
        var handler = new RecordingHandler();
        var writer = CreateWriter(Enabled, handler, out var logger);

        handler.Respond = _ => throw new HttpRequestException("Connection refused");
        await writer.WriteAsync([new("kiosk-1", Full, At)]);
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"message\":\"unauthorized\"}") };
        await writer.WriteAsync([new("kiosk-1", Full, At)]);
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NoContent);
        await writer.WriteAsync([new("kiosk-1", Full, At)]);
        await writer.WriteAsync([new("kiosk-1", Full, At)]);

        Assert.Equal(4, handler.Requests.Count);
        Assert.Collection(logger.Entries,
            entry => Assert.Equal((LogLevel.Warning, true), (entry.Level, entry.Message.Contains("Connection refused"))),
            entry => Assert.Equal(LogLevel.Information, entry.Level));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("secret-token"));
    }

    [Fact]
    public async Task EnqueuedReports_AreWrittenInTheBackground()
    {
        var handler = new RecordingHandler();
        var writer = CreateWriter(Enabled, handler, out _);
        await writer.StartAsync(CancellationToken.None);
        try
        {
            writer.Enqueue("kiosk-1", Full);
            for (var attempt = 0; attempt < 100 && handler.Requests.Count == 0; attempt++)
            {
                await Task.Delay(20);
            }

            Assert.EndsWith($" {At.ToUnixTimeSeconds()}", Assert.Single(handler.Requests).Body);
        }
        finally
        {
            await writer.StopAsync(CancellationToken.None);
        }
    }

    private static TerminalMetricsWriter CreateWriter(RuntimeSettings settings, RecordingHandler handler, out ListLogger logger)
    {
        logger = new ListLogger();
        return new TerminalMetricsWriter(new StubSettingsStore(settings), new StubHttpClientFactory(handler), logger, new FixedClock(At));
    }

    private sealed record RecordedRequest(HttpMethod Method, string Uri, string? Authorization, string Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<RecordedRequest> _requests = [];

        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NoContent);

        public IReadOnlyList<RecordedRequest> Requests
        {
            get { lock (_requests) return [.. _requests]; }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_requests)
            {
                _requests.Add(new(request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body));
            }
            return Respond(request);
        }
    }

    /// <summary>The writer disposes its client per batch; the handler must survive that.</summary>
    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubSettingsStore(RuntimeSettings settings) : IRuntimeSettingsStore
    {
        public RuntimeSettings Load() => settings;

        public Task SaveAsync(RuntimeSettings settings, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ListLogger : ILogger<TerminalMetricsWriter>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
