using System.Diagnostics;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
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

    private static Activity SkipSpan(IEnumerable<Activity> spans, string eventId) => spans.Single(span =>
        span.OperationName == "offline.skip" && (string?)span.GetTagItem("stempeluhr.event_id") == eventId);

    [Fact]
    public async Task BufferedBehindAFailedEvent_IsFoundByEventId_WithOneSpanEach()
    {
        var (service, kimai) = CreateService();
        kimai.FailNextStatusCalls = 2;

        var spans = await SpanCapture.InTraceAsync(() => service.SyncKioskAsync(
            [Kiosk("first", "start", T08), Kiosk("second", "stop", T10)]));

        Assert.Equal("buffered", ApplySpan(spans, "first").GetTagItem("stempeluhr.outcome"));
        Assert.DoesNotContain(spans, span => span.OperationName == "offline.skip" && (string?)span.GetTagItem("stempeluhr.event_id") == "first");
        var second = SkipSpan(spans, "second");
        Assert.Equal("buffered", second.GetTagItem("stempeluhr.outcome"));
        Assert.Equal("stop", second.GetTagItem("stempeluhr.action"));
        Assert.Equal("max", second.GetTagItem("stempeluhr.employee_id"));
        Assert.NotEqual(ActivityStatusCode.Error, second.Status);
    }

    [Fact]
    public async Task BatchBehindABacklog_IsFoundByEventId()
    {
        var (service, kimai) = CreateService();
        kimai.FailNextStatusCalls = 3; // Batch, die Runde danach und die Runde vor dem zweiten Batch.
        await service.SyncKioskAsync([Kiosk("backlog", "start", T08)]);

        var spans = await SpanCapture.InTraceAsync(() => service.SyncKioskAsync([Kiosk("behind", "stop", T10)]));

        Assert.Equal("buffered", SkipSpan(spans, "behind").GetTagItem("stempeluhr.outcome"));
    }

    [Fact]
    public async Task EventsAfterARejectedPin_AreTaggedSkipped_WithoutThePin()
    {
        var (service, _) = CreateService();

        var spans = await SpanCapture.InTraceAsync(() => service.SyncKioskAsync(
            [Kiosk("wrong", "start", T08, "9999"), Kiosk("later", "stop", T10, "9999")]));

        Assert.Equal("rejected", ApplySpan(spans, "wrong").GetTagItem("stempeluhr.outcome"));
        var later = SkipSpan(spans, "later");
        Assert.Equal("skipped", later.GetTagItem("stempeluhr.outcome"));
        Assert.DoesNotContain(later.TagObjects, tag => tag.Value is string value && value.Contains("9999"));
    }

    [Fact]
    public async Task ResentEvent_IsTaggedDuplicate()
    {
        var (service, _) = CreateService();
        await service.SyncKioskAsync([Kiosk("once", "start", T08)]);

        var spans = await SpanCapture.InTraceAsync(() => service.SyncKioskAsync([Kiosk("once", "start", T08)]));

        Assert.Equal("duplicate", SkipSpan(spans, "once").GetTagItem("stempeluhr.outcome"));
    }

    [Fact]
    public async Task LaterEventOfALockedEmployee_IsTaggedLocked()
    {
        var guard = new PinAttemptGuard(clock: new ManualClock(T08));
        var (service, _, _) = CreateServiceWithPinGuard(guard);
        for (var i = 0; i < PinAttemptGuard.EmployeeThreshold; i++) guard.RecordFailure(PinAttemptGuard.EmployeeKey("max"));

        var spans = await SpanCapture.InTraceAsync(() => service.SyncKioskAsync(
            [Kiosk("max-start", "start", T08), Kiosk("max-stop", "stop", T10)]));

        Assert.Equal("locked", ApplySpan(spans, "max-start").GetTagItem("stempeluhr.outcome"));
        Assert.Equal("locked", SkipSpan(spans, "max-stop").GetTagItem("stempeluhr.outcome"));
    }
}
