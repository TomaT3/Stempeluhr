using System.Diagnostics;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

/// <summary>
/// Ein Live-Stempel hängt unter der Trace des Kiosks (eingehendes
/// <c>traceparent</c>) und nennt Event-ID, Quelle und Ergebnis, nie die PIN.
/// </summary>
public sealed partial class ClockServiceNotificationTests
{
    [Fact]
    public async Task ClockAsync_JoinsKioskTrace_AndTagsBookedStamp()
    {
        var spans = await CaptureClockSpans(kimai =>
        {
            kimai.EnqueueStatus(ClockedOut);
            kimai.EnqueueStatus(Working);
        }, new KioskClockRequest("max", "1234", "start", null, EventId: "evt-1", Source: "pin") { AuthenticatedTerminalId = "pi-1" });

        var span = Assert.Single(spans);
        Assert.Equal("kiosk.clock", span.OperationName);
        Assert.Equal("evt-1", span.GetTagItem("stempeluhr.event_id"));
        Assert.Equal("start", span.GetTagItem("stempeluhr.action"));
        Assert.Equal("max", span.GetTagItem("stempeluhr.employee_id"));
        Assert.Equal("pi-1", span.GetTagItem("stempeluhr.terminal_id"));
        Assert.Equal("pin", span.GetTagItem("stempeluhr.source"));
        Assert.Equal("booked", span.GetTagItem("stempeluhr.outcome"));
        Assert.DoesNotContain(span.TagObjects, tag => tag.Value is string value && value.Contains("1234"));
    }

    [Fact]
    public async Task ClockAsync_NoOp_IsTaggedNoop()
    {
        var spans = await CaptureClockSpans(kimai => kimai.EnqueueStatus(Working), Request("start"));

        Assert.Equal("noop", Assert.Single(spans).GetTagItem("stempeluhr.outcome"));
    }

    /// <summary>
    /// Startet einen Eltern-Span wie die ASP.NET-Core-Instrumentierung für ein
    /// eingehendes <c>traceparent</c> und sammelt nur Spans dieser Trace -
    /// andere Testklassen laufen parallel.
    /// </summary>
    private static async Task<List<Activity>> CaptureClockSpans(Action<ScriptedKimaiClient> script, KioskClockRequest request)
    {
        var (service, kimai, _) = Create(Settings(telegramEnabled: false));
        script(kimai);

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
            await service.ClockAsync(request);
        }
        return spans;
    }
}
