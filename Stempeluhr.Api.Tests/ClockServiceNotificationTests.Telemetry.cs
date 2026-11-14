using System.Diagnostics;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

/// <summary>
/// Ein Live-Stempel hängt unter der Trace des Kiosks (eingehendes
/// <c>traceparent</c>) und nennt Event-ID, Name, Quelle und Ergebnis, nie die PIN.
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
        Assert.Equal("Max Mustermann", span.GetTagItem("stempeluhr.employee_name"));
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

    [Fact]
    public async Task ClockAsync_StopWentThroughAndKimaiThenDown_IsRetry_NotAnError()
    {
        // Stop gebucht, Start scheitert an 503: Der Kiosk reiht ein, der Nachtrag schließt ab.
        var spans = await CaptureClockSpans(kimai =>
        {
            kimai.EnqueueStatus(Working with { ActiveIsDefaultTask = true });
            kimai.StartFailures.Enqueue(KimaiError(503));
        }, SwitchRequest("kx"), SettingsWithTask(), expectThrow: true);

        var span = Assert.Single(spans);
        Assert.Equal("retry", span.GetTagItem("stempeluhr.outcome"));
        Assert.NotEqual(ActivityStatusCode.Error, span.Status);
        Assert.Equal(nameof(KimaiApiException), span.GetTagItem("error.type"));
    }

    [Fact]
    public async Task ClockAsync_NetworkErrorOrAbort_IsRetry_NotAnError()
    {
        foreach (var failure in new Exception[] { new HttpRequestException("down"), new TaskCanceledException("kiosk gave up") })
        {
            var spans = await CaptureClockSpans(kimai =>
            {
                kimai.EnqueueStatus(ClockedOut);
                kimai.StartFailures.Enqueue(failure);
            }, Request("start"), expectThrow: true);

            var span = Assert.Single(spans);
            Assert.Equal("retry", span.GetTagItem("stempeluhr.outcome"));
            Assert.NotEqual(ActivityStatusCode.Error, span.Status);
        }
    }

    [Fact]
    public async Task ClockAsync_RefusedByKimai_IsFailed_WithErrorStatus()
    {
        // Endgültig: Der Kiosk reiht nicht ein, der Stempel fehlt. Den will man finden.
        var spans = await CaptureClockSpans(kimai =>
        {
            kimai.EnqueueStatus(ClockedOut);
            kimai.StartFailures.Enqueue(KimaiError(403));
        }, Request("start"), expectThrow: true);

        var span = Assert.Single(spans);
        Assert.Equal("failed", span.GetTagItem("stempeluhr.outcome"));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public async Task ClockAsync_BookedButStatusReadFailed_StaysBooked()
    {
        var spans = await CaptureClockSpans(kimai =>
        {
            kimai.EnqueueStatus(Working);
            kimai.StatusFailures.Enqueue(null);
            kimai.StatusFailures.Enqueue(new HttpRequestException("timeout"));
        }, Request("stop"), expectThrow: true);

        var span = Assert.Single(spans);
        Assert.Equal("booked", span.GetTagItem("stempeluhr.outcome"));
        Assert.NotEqual(ActivityStatusCode.Error, span.Status);
        Assert.Equal(nameof(HttpRequestException), span.GetTagItem("error.type"));
    }

    [Fact]
    public async Task ClockAsync_RejectedSwitchContinuedOnPreviousTask_IsTaggedContinued()
    {
        var spans = await CaptureClockSpans(kimai =>
        {
            kimai.EnqueueStatus(Working with { ActiveIsDefaultTask = true });
            kimai.EnqueueStatus(Working with { ActiveIsDefaultTask = true });
            kimai.StartFailures.Enqueue(KimaiError(400));
        }, SwitchRequest("kx"), SettingsWithTask());

        Assert.Equal("continued", Assert.Single(spans).GetTagItem("stempeluhr.outcome"));
    }

    [Fact]
    public async Task ClockAsync_Notification_RunsOutsideTheStampSpan()
    {
        Activity? atLookup = new("not-called");
        var spans = await CaptureClockSpans(kimai =>
        {
            kimai.EnqueueStatus(ClockedOut);
            kimai.EnqueueStatus(Working);
            kimai.OnTimezoneLookup = () => atLookup = Activity.Current;
        }, Request("start"), Settings(telegramEnabled: true));

        Assert.Equal("booked", Assert.Single(spans).GetTagItem("stempeluhr.outcome"));
        Assert.Null(atLookup);
    }

    private static async Task<List<Activity>> CaptureClockSpans(
        Action<ScriptedKimaiClient> script, KioskClockRequest request, RuntimeSettings? settings = null, bool expectThrow = false)
    {
        var (service, kimai, _) = Create(settings ?? Settings(telegramEnabled: false));
        script(kimai);

        return await SpanCapture.InTraceAsync(async () =>
        {
            if (expectThrow) await Assert.ThrowsAnyAsync<Exception>(() => service.ClockAsync(request));
            else await service.ClockAsync(request);
        });
    }

    [Fact]
    public async Task ClockAsync_WrongPin_IsFoundByEventId_WithoutNameOrPin()
    {
        var spans = await CaptureClockSpans(_ => { },
            new KioskClockRequest("max", "9999", "start", null, EventId: "evt-wrong") { AuthenticatedTerminalId = "pi-1" });

        var span = Assert.Single(spans);
        Assert.Equal("kiosk.clock", span.OperationName);
        Assert.Equal("evt-wrong", span.GetTagItem("stempeluhr.event_id"));
        Assert.Equal("unauthorized", span.GetTagItem("stempeluhr.outcome"));
        Assert.Equal("max", span.GetTagItem("stempeluhr.employee_id"));
        Assert.Equal("pi-1", span.GetTagItem("stempeluhr.terminal_id"));
        Assert.Null(span.GetTagItem("stempeluhr.employee_name"));
        Assert.DoesNotContain(span.TagObjects, tag => tag.Value is string value && value.Contains("9999"));
    }

    [Fact]
    public async Task ClockAsync_LockedEmployee_IsTaggedLocked_NotAsAnError()
    {
        var settings = Settings(telegramEnabled: false);
        var guard = new PinAttemptGuard();
        for (var i = 0; i < PinAttemptGuard.EmployeeThreshold; i++) guard.RecordFailure(PinAttemptGuard.EmployeeKey("max"));
        var service = new ClockService(new StubSettingsStore(settings), new EmployeeService(), new ScriptedKimaiClient(), new RecordingNotifier(), pinAttempts: guard);

        var spans = await SpanCapture.InTraceAsync(async () =>
            await Assert.ThrowsAsync<PinLockedException>(() => service.ClockAsync(Request("start") with { EventId = "evt-locked" })));

        var span = Assert.Single(spans);
        Assert.Equal("evt-locked", span.GetTagItem("stempeluhr.event_id"));
        Assert.Equal("locked", span.GetTagItem("stempeluhr.outcome"));
        Assert.NotEqual(ActivityStatusCode.Error, span.Status);
    }
}
