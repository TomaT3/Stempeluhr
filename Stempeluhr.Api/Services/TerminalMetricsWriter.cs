using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;
using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

/// <summary>One terminal report as received by the server.</summary>
public sealed record TerminalMetricsSample(string TerminalId, TerminalHealthReport Report, DateTimeOffset ReceivedAt);

/// <summary>InfluxDB line protocol for terminal reports (measurement <c>terminal</c>).</summary>
public static class TerminalMetricsLineProtocol
{
    public const string Measurement = "terminal";

    /// <summary>Null without any value. Every field always keeps its type, otherwise Influx rejects the line.</summary>
    public static string? Format(TerminalMetricsSample sample)
    {
        var report = sample.Report;
        var fields = new List<string>();
        void Float(string key, double? value)
        {
            if (value is { } number) fields.Add($"{key}={number.ToString(CultureInfo.InvariantCulture)}");
        }
        void Bool(string key, bool? value)
        {
            if (value is { } flag) fields.Add($"{key}={(flag ? "true" : "false")}");
        }

        Float("heartbeat_age_seconds", report.HeartbeatAgeSeconds);
        Float("pending", report.Pending);
        Float("rejected", report.Rejected);
        Float("cpu_percent", report.CpuPercent);
        Float("load1", report.Load1);
        Float("available_memory_kb", report.AvailableMemoryKb);
        Float("chromium_rss_kb", report.ChromiumRssSumKb);
        Float("pcscd_rss_kb", report.PcscdRssKb);
        Float("pcscd_anonymous_kb", report.PcscdAnonymousKb);
        Float("pcscd_swap_kb", report.PcscdSwapKb);
        Float("pcscd_anonymous_and_swap_kb", report.PcscdAnonymousAndSwapKb);
        Float("pcscd_pid", report.PcscdPid);
        Float("pcscd_start_ticks", report.PcscdStartTicks);
        Float("temperature_c", report.TemperatureC);
        if (report.ThrottledFlags is { } throttled)
        {
            // A bit mask: integer so dashboards can test single bits.
            fields.Add($"throttled_flags={(long)throttled}i");
        }
        Float("disk_free_mb", report.DiskFreeMb);
        Float("uptime_seconds", report.UptimeSeconds);
        Bool("ui_alive", report.UiStatus is null ? null : report.UiStatus == "alive");
        Bool("offline", report.Offline);
        Bool("busy", report.Busy);
        if (fields.Count == 0) return null;

        var line = new StringBuilder(Measurement);
        Tag(line, "terminal", sample.TerminalId);
        Tag(line, "agent_version", report.AgentVersion);
        Tag(line, "app_version", report.AppVersion);
        Tag(line, "pcscd_version", report.PcscdVersion);
        line.Append(' ').AppendJoin(',', fields);
        line.Append(' ').Append(sample.ReceivedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        return line.ToString();
    }

    private static void Tag(StringBuilder line, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        line.Append(',').Append(key).Append('=');
        foreach (var character in value)
        {
            if (char.IsControl(character)) continue;
            if (character is '\\' or ',' or '=' or ' ') line.Append('\\');
            line.Append(character);
        }
    }
}

/// <summary>
/// Writes terminal reports to an optional InfluxDB (2.x, or 3 via its v2 write
/// API) so Grafana can show them over time. Best effort: a missing or broken
/// Influx never affects the diagnostics request, and a failed batch is dropped.
/// </summary>
public sealed class TerminalMetricsWriter(
    IRuntimeSettingsStore settingsStore,
    IHttpClientFactory httpClientFactory,
    ILogger<TerminalMetricsWriter> logger,
    TimeProvider? clock = null) : BackgroundService
{
    /// <summary>Name of the configured HttpClient (see Program.cs).</summary>
    public const string ClientName = "Influx";
    private const int MaxBatchSize = 500;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Channel<TerminalMetricsSample> _queue = Channel.CreateBounded<TerminalMetricsSample>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private bool _failing;

    /// <summary>Never blocks or throws; without Influx configuration the sample is dropped when sending.</summary>
    public void Enqueue(string terminalId, TerminalHealthReport report) =>
        _queue.Writer.TryWrite(new TerminalMetricsSample(terminalId, report, _clock.GetUtcNow()));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (await _queue.Reader.WaitToReadAsync(stoppingToken))
        {
            var batch = new List<TerminalMetricsSample>();
            while (batch.Count < MaxBatchSize && _queue.Reader.TryRead(out var sample))
            {
                batch.Add(sample);
            }
            await WriteAsync(batch, stoppingToken);
        }
    }

    public async Task WriteAsync(IReadOnlyCollection<TerminalMetricsSample> batch, CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = settingsStore.Load();
            if (!settings.InfluxEnabled) return;
            var lines = batch.Select(TerminalMetricsLineProtocol.Format).OfType<string>().ToArray();
            if (lines.Length == 0) return;

            using var client = httpClientFactory.CreateClient(ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, WriteUri(settings))
            {
                Content = new StringContent(string.Join('\n', lines), Encoding.UTF8, "text/plain"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Token", settings.InfluxToken!.Trim());
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                if (_failing) logger.LogInformation("Terminal metrics reach InfluxDB again");
                _failing = false;
                return;
            }
            // Influx error bodies name the problem (e.g. a field type conflict) and contain no token.
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            Fail($"HTTP {(int)response.StatusCode}: {body[..Math.Min(body.Length, 300)]}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The inner exception names the cause (refused, unknown host, timeout).
            Fail(ex.GetBaseException().Message);
        }
    }

    /// <summary>Only the first failure is logged, otherwise an outage would log every minute.</summary>
    private void Fail(string reason)
    {
        if (!_failing) logger.LogWarning("Terminal metrics could not be written to InfluxDB: {Reason}", reason);
        _failing = true;
    }

    private static Uri WriteUri(RuntimeSettings settings)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.InfluxOrg)) query.Add($"org={Uri.EscapeDataString(settings.InfluxOrg.Trim())}");
        query.Add($"bucket={Uri.EscapeDataString(settings.InfluxBucket!.Trim())}");
        query.Add("precision=s");
        // Keeps a path prefix such as http://nas/influx/.
        var root = new Uri(settings.InfluxUrl!.Trim().TrimEnd('/') + "/");
        return new Uri(root, "api/v2/write?" + string.Join('&', query));
    }
}
