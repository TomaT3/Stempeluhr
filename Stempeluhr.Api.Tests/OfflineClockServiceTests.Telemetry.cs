using System.Diagnostics;
using Stempeluhr.Api.Models;
using Xunit;

namespace Stempeluhr.Api.Tests;

/// <summary>
/// Gerade verlorene oder zurückgestellte Stempel müssen über ihre Event-ID
/// auffindbar sein, und <c>stempeluhr.outcome</c> hat überall dieselben Werte.
/// </summary>
public sealed partial class OfflineClockServiceTests
{
    private static Activity ApplySpan(IEnumerable<Activity> spans, string eventId) => spans.First(span =>
        span.OperationName == "offline.apply" && (string?)span.GetTagItem("stempeluhr.event_id") == eventId);

    [Fact]
    public async Task OfflineApply_Booked_NamesTheEmployee()
    {
        var (service, _) = CreateService();

        var spans = await SpanCapture.InTraceAsync(() => service.SyncKioskAsync([Kiosk("booked", "start", T08)]));

        var span = ApplySpan(spans, "booked");
        Assert.Equal("booked", span.GetTagItem("stempeluhr.outcome"));
        Assert.Equal("max", span.GetTagItem("stempeluhr.employee_id"));
        Assert.False(string.IsNullOrEmpty(span.GetTagItem("stempeluhr.employee_name") as string));
    }

    [Fact]
    public async Task OfflineApply_IdempotentReplay_IsNoop_WithTheStateSeparately()
    {
        var (service, _) = CreateService();
        await service.SyncKioskAsync([Kiosk("first", "start", T08)]);

        var spans = await SpanCapture.InTraceAsync(() => service.SyncKioskAsync([Kiosk("again", "start", T10)]));

        var span = ApplySpan(spans, "again");
        Assert.Equal("noop", span.GetTagItem("stempeluhr.outcome"));
        Assert.False(string.IsNullOrEmpty(span.GetTagItem("stempeluhr.state") as string));
    }

    [Fact]
    public async Task OfflineApply_WrongPin_IsFoundByEventId_WithoutNameOrPin()
    {
        var (service, _) = CreateService();
        var entry = new OfflineKioskClockEventDto("wrong-pin", "max", "9999", "start", T08);

        var spans = await SpanCapture.InTraceAsync(() => service.SyncKioskAsync([entry]));

        var span = ApplySpan(spans, "wrong-pin");
        Assert.Equal("rejected", span.GetTagItem("stempeluhr.outcome"));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Null(span.GetTagItem("stempeluhr.employee_name"));
        Assert.DoesNotContain(span.TagObjects, tag => tag.Value is string value && value.Contains("9999"));
    }

    [Fact]
    public async Task OfflineApply_KimaiDown_IsBuffered_NotAnError_AndFlushSaysInterrupted()
    {
        var (service, kimai) = CreateService();
        kimai.FailNextStatusCalls = 2; // Batch und die Runde danach, siehe Klassenkommentar.

        var spans = await SpanCapture.InTraceAsync(() => service.SyncKioskAsync([Kiosk("down", "start", T08)]));

        var span = ApplySpan(spans, "down");
        Assert.Equal("buffered", span.GetTagItem("stempeluhr.outcome"));
        Assert.NotEqual(ActivityStatusCode.Error, span.Status);
        Assert.False(string.IsNullOrEmpty(span.GetTagItem("stempeluhr.error") as string));

        var flush = spans.Single(candidate => candidate.OperationName == "outbox.flush");
        Assert.Equal(1, flush.GetTagItem("stempeluhr.outbox_size"));
        Assert.Equal(0, flush.GetTagItem("stempeluhr.drained"));
        Assert.Equal(true, flush.GetTagItem("stempeluhr.interrupted"));
    }
}
