using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

/// <summary>
/// Regression tests for the offline replay ordering semantics introduced by
/// the review round on 2026-08-24:
/// - a transient failure buffers the failed event AND everything after it
/// - the outbox replays strictly in scan order across flush rounds
///   (failed entries go back to the FRONT)
/// - a new batch never jumps over a backlog that is still in the outbox
///   (both sync paths)
/// - NFC and kiosk backlogs replay as ONE timeline in event-time order,
///   never "all NFC first, then all kiosk"
/// - a pauseEnd whose resume-start failed transiently after the pause stop
///   succeeds resumes the work on the retry instead of dying as a no-op -
///   but ONLY when Kimai's latest stopped timesheet is the matching pause
///   stop (no phantom starts after live stops or other terminals' actions)
///
/// Note on the failure counters: every sync runs an opportunistic flush at
/// its end, so simulating an ongoing outage needs one failing status call
/// for the batch processing plus one for that trailing flush.
/// </summary>
public sealed class OfflineClockServiceTests
{
    private static readonly DateTimeOffset T08 = Parse("2026-08-24T08:00:00Z");
    private static readonly DateTimeOffset T10 = Parse("2026-08-24T10:00:00Z");
    private static readonly DateTimeOffset T12 = Parse("2026-08-24T12:00:00Z");
    private static readonly DateTimeOffset T1230 = Parse("2026-08-24T12:30:00Z");

    [Fact]
    public async Task TransientFailure_BuffersWholeBatch_AndReplaysInScanOrder()
    {
        var (service, kimai) = CreateService();
        kimai.FailNextStatusCalls = 2;

        var result = await service.SyncKioskAsync(
        [
            Kiosk("e1", "start", T08),
            Kiosk("e2", "stop", T12),
        ]);

        // Nothing applied yet - both events buffered including the one AFTER
        // the failure (previously only the failed event was buffered and the
        // later stop could run against an outdated state).
        Assert.Equal(0, result.Accepted);
        Assert.Equal(2, result.Buffered);
        Assert.All(result.Results, r => Assert.Equal("buffered", r.Status));
        Assert.Empty(kimai.Operations);

        await service.FlushOutboxAsync();

        // Replay must arrive exactly in scan order.
        Assert.Equal(2, kimai.Operations.Count);
        Assert.Equal("start", kimai.Operations[0].Kind);
        Assert.Equal(T08, kimai.Operations[0].At);
        Assert.Equal("stop", kimai.Operations[1].Kind);
        Assert.Equal(T12, kimai.Operations[1].At);
        Assert.False(kimai.IsRunning);
    }

    [Fact]
    public async Task Flush_KeepsScanOrder_AcrossFailedRounds()
    {
        var (service, kimai) = CreateService();

        // Fill the outbox while Kimai is down (batch + trailing flush fail).
        kimai.FailNextStatusCalls = 2;
        await service.SyncKioskAsync([Kiosk("e1", "start", T08), Kiosk("e2", "stop", T12)]);
        Assert.Empty(kimai.Operations);

        // One more failing round: this flush must fail at e1 and keep it at
        // the FRONT of the outbox.
        kimai.FailNextStatusCalls = 1;
        await service.FlushOutboxAsync();
        Assert.Empty(kimai.Operations);

        // Next round: e1 then e2, in order. With a tail-reinsert regression
        // this would apply e2 first ("Lief nicht" no-op) and lose the stop.
        await service.FlushOutboxAsync();

        Assert.Equal(2, kimai.Operations.Count);
        Assert.Equal("start", kimai.Operations[0].Kind);
        Assert.Equal(T08, kimai.Operations[0].At);
        Assert.Equal("stop", kimai.Operations[1].Kind);
        Assert.Equal(T12, kimai.Operations[1].At);
        Assert.False(kimai.IsRunning);
    }

    [Fact]
    public async Task NewBatch_AfterRecovery_FlushesBacklogFirst_ThenAppliesInOrder()
    {
        var (service, kimai) = CreateService();

        // First request: Kimai down (processing + trailing flush fail), the
        // start event lands in the outbox.
        kimai.FailNextStatusCalls = 2;
        var first = await service.SyncKioskAsync([Kiosk("e1", "start", T08)]);
        Assert.Equal(1, first.Buffered);
        Assert.Empty(kimai.Operations);

        // Kimai recovered before the second request arrives: the sync's
        // leading flush drains the backlog first, so the stop is applied
        // against the freshly started timesheet instead of becoming a no-op.
        var second = await service.SyncKioskAsync([Kiosk("e2", "stop", T12)]);

        Assert.Equal(1, second.Accepted);
        Assert.Equal(0, second.Buffered);

        Assert.Equal(2, kimai.Operations.Count);
        Assert.Equal("start", kimai.Operations[0].Kind);
        Assert.Equal(T08, kimai.Operations[0].At);
        Assert.Equal("stop", kimai.Operations[1].Kind);
        Assert.Equal(T12, kimai.Operations[1].At);
        Assert.False(kimai.IsRunning);
    }

    [Fact]
    public async Task NewBatch_IsQueuedBehindBacklog_WhenKimaiStillDown()
    {
        var (service, kimai) = CreateService();

        // First request: Kimai down (processing + trailing flush fail).
        kimai.FailNextStatusCalls = 2;
        await service.SyncKioskAsync([Kiosk("e1", "start", T08)]);
        Assert.Empty(kimai.Operations);

        // Second request while Kimai is STILL down: its leading flush must
        // fail so the backlog survives - then the new stop event has to be
        // queued BEHIND it (buffered), never applied ahead of the start.
        kimai.FailNextStatusCalls = 1;
        var second = await service.SyncKioskAsync([Kiosk("e2", "stop", T12)]);

        Assert.Equal(0, second.Accepted);
        Assert.Equal(1, second.Buffered);
        Assert.Equal("buffered", second.Results.Single().Status);

        // The trailing flush of this very request applies everything in
        // scan order once the fake recovers.
        Assert.Equal(2, kimai.Operations.Count);
        Assert.Equal("start", kimai.Operations[0].Kind);
        Assert.Equal(T08, kimai.Operations[0].At);
        Assert.Equal("stop", kimai.Operations[1].Kind);
        Assert.Equal(T12, kimai.Operations[1].At);
        Assert.False(kimai.IsRunning);
    }

    [Fact]
    public async Task ResendWhileBuffered_DoesNotDuplicateOutboxEntries()
    {
        var (service, kimai, logger) = CreateServiceWithLogger();

        // First send: Kimai down (processing + trailing flush fail), both
        // events land in the outbox.
        kimai.FailNextStatusCalls = 2;
        var events = new[] { Kiosk("k1", "start", T08), Kiosk("k2", "stop", T12) };
        var first = await service.SyncKioskAsync(events);
        Assert.Equal(2, first.Buffered);
        Assert.Empty(kimai.Operations);

        // Safety-net re-send while Kimai is STILL down: buffering freed the
        // event IDs, so without physical dedup this would enqueue a second
        // COPY of each event - one more with every retry across the outage.
        kimai.FailNextStatusCalls = 1;
        var second = await service.SyncKioskAsync(events);

        Assert.Equal(0, second.Accepted);
        Assert.Equal(2, second.Buffered);
        Assert.All(second.Results, r => Assert.Equal("buffered", r.Status));

        // Recovery drains EXACTLY one copy per event: the flushed-count log
        // line says 2. A duplicate-copy regression would report 4 there (the
        // redundant entries are skipped via TryRegister but still counted).
        await service.FlushOutboxAsync();

        Assert.Equal(2, kimai.Operations.Count);
        Assert.Equal(("start", T08), kimai.Operations[0]);
        Assert.Equal(("stop", T12), kimai.Operations[1]);
        Assert.Contains(logger.Messages, m => m.Contains("Outbox flushed 2 offline event(s)"));
    }

    [Fact]
    public async Task PauseEnd_TransientFailureAfterPauseStop_ResumesWorkOnRetry()
    {
        var (service, kimai) = CreateService();

        // Build up the timeline while Kimai is healthy: start, then a pause.
        await service.SyncKioskAsync([Kiosk("p1", "start", T08)]);
        await service.SyncKioskAsync([Kiosk("p2", "pauseStart", T12)]);
        Assert.True(kimai.IsRunning);
        Assert.True(kimai.ActiveIsPause);

        // pauseEnd is a two-step transaction (stop the pause timesheet, then
        // resume work). The resume-start fails transiently AFTER the pause
        // stop already succeeded - exactly the recovery window after an
        // outage. The event goes back into the outbox.
        kimai.FailNextStartCalls = 1;
        var failed = await service.SyncKioskAsync([Kiosk("p3", "pauseEnd", T1230)]);

        Assert.Equal(0, failed.Accepted);
        Assert.Equal(1, failed.Buffered);
        Assert.Equal("buffered", failed.Results.Single().Status);

        // Retry round: nothing is running anymore (the pause was already
        // stopped). Without the partial-application recovery this retry was
        // answered as a no-op and acknowledged as applied - leaving the
        // employee clocked out for the rest of the day.
        await service.FlushOutboxAsync();

        Assert.Equal(5, kimai.Operations.Count);
        Assert.Equal(("start", T08), kimai.Operations[0]);      // start work
        Assert.Equal(("stop", T12), kimai.Operations[1]);       // pauseStart: end work
        Assert.Equal(("start", T12), kimai.Operations[2]);      // pauseStart: open pause
        Assert.Equal(("stop", T1230), kimai.Operations[3]);     // pauseEnd: stop pause
        Assert.Equal(("start", T1230), kimai.Operations[4]);    // pauseEnd retry: resume work
        Assert.True(kimai.IsRunning);
        Assert.False(kimai.ActiveIsPause);
    }

    [Fact]
    public async Task PauseStart_WhilePauseAlreadyRuns_IsNoOp()
    {
        var (service, kimai) = CreateService();

        // The kiosk sent pauseStart live, the request timed out on the client
        // AFTER the server had applied it, so the same action is also queued.
        await service.SyncKioskAsync([Kiosk("s1", "start", T08)]);
        await service.SyncKioskAsync([Kiosk("s2", "pauseStart", T12)]);
        var operationsAfterPause = kimai.Operations.Count;

        // Replaying it must not stop the pause and open a second one.
        var result = await service.SyncKioskAsync([Kiosk("s3", "pauseStart", T12)]);

        Assert.Equal(1, result.Accepted);
        Assert.Equal("Pause lief bereits - kein Nachtrag noetig.", result.Results.Single().Message);
        Assert.Equal(operationsAfterPause, kimai.Operations.Count);
        Assert.True(kimai.ActiveIsPause);
    }

    [Fact]
    public async Task PauseEnd_LiveStopBeforeFlush_DoesNotPhantomStart()
    {
        var (service, kimai) = CreateService();

        // Live timeline while online: start, then a pause.
        await service.SyncKioskAsync([Kiosk("q1", "start", T08)]);
        await service.SyncKioskAsync([Kiosk("q2", "pauseStart", T12)]);

        // The network drops; the pauseEnd@12:30 is queued. Kimai comes back
        // and the employee presses STOP live at 13:00 - BEFORE the 15 s retry
        // timer flushes the queue. The live stop ends the PAUSE timesheet.
        var stopAt1300 = Parse("2026-08-24T13:00:00Z");
        await service.SyncKioskAsync([Kiosk("q3", "stop", stopAt1300)]);
        Assert.False(kimai.IsRunning);
        var operationsAfterLiveStop = kimai.Operations.Count;

        // Now the offline pauseEnd@12:30 replays against "nothing running".
        // The latest stopped timesheet ended at 13:00, NOT at the event time,
        // so this was no interrupted transaction: starting work@12:30 here
        // would book a phantom timesheet running until the next stamp.
        var result = await service.SyncKioskAsync([Kiosk("q4", "pauseEnd", T1230)]);

        Assert.Equal(1, result.Accepted);
        Assert.Equal("Keine laufende Pause - Nachtrag nicht moeglich.", result.Results.Single().Message);
        Assert.Equal(operationsAfterLiveStop, kimai.Operations.Count);
        Assert.False(kimai.IsRunning);
    }

    [Fact]
    public async Task PauseEnd_LiveStopWithinOldTolerance_DoesNotPhantomStart()
    {
        var (service, kimai) = CreateService();

        // Live timeline while online: start, then a pause.
        await service.SyncKioskAsync([Kiosk("r1", "start", T08)]);
        await service.SyncKioskAsync([Kiosk("r2", "pauseStart", T12)]);

        // The pauseEnd@12:30 is queued offline. Kimai comes back and the
        // employee presses STOP live at 12:31 - just 60 s after the queued
        // event's timestamp, INSIDE the old 120 s tolerance but OUTSIDE the
        // current 30 s one. This is a genuine live stop (the employee ended
        // the pause manually), NOT our interrupted transaction: resuming work
        // here would book a phantom timesheet.
        var stopAt1231 = Parse("2026-08-24T12:31:00Z");
        await service.SyncKioskAsync([Kiosk("r3", "stop", stopAt1231)]);
        Assert.False(kimai.IsRunning);
        var operationsAfterLiveStop = kimai.Operations.Count;

        var result = await service.SyncKioskAsync([Kiosk("r4", "pauseEnd", T1230)]);

        Assert.Equal(1, result.Accepted);
        Assert.Equal("Keine laufende Pause - Nachtrag nicht moeglich.", result.Results.Single().Message);
        Assert.Equal(operationsAfterLiveStop, kimai.Operations.Count);
        Assert.False(kimai.IsRunning);
    }

    [Fact]
    public async Task PauseEnd_NothingEverStopped_DoesNotResume()
    {
        var (service, kimai) = CreateService();

        // Fresh clockedOut state without any history: a replayed pauseEnd has
        // nothing to point at and must not start work on its own.
        var result = await service.SyncKioskAsync([Kiosk("z1", "pauseEnd", T1230)]);

        Assert.Equal(1, result.Accepted);
        Assert.Equal("Keine laufende Pause - Nachtrag nicht moeglich.", result.Results.Single().Message);
        Assert.Empty(kimai.Operations);
        Assert.False(kimai.IsRunning);
    }

    [Fact]
    public async Task PauseEnd_WithWorkAlreadyRunning_IsANoOp()
    {
        var (service, kimai) = CreateService();

        await service.SyncKioskAsync([Kiosk("w1", "start", T08)]);
        var operationsBefore = kimai.Operations.Count;

        // Work running but no pause (the transition completed some other way):
        // a replayed pauseEnd must not touch anything.
        var result = await service.SyncKioskAsync([Kiosk("w2", "pauseEnd", T1230)]);

        Assert.Equal(1, result.Accepted);
        Assert.Equal("Keine laufende Pause - Nachtrag nicht moeglich.", result.Results.Single().Message);
        Assert.Equal(operationsBefore, kimai.Operations.Count);
    }

    [Fact]
    public async Task StaleQueuedStop_DoesNotKillNewerLiveTimesheet()
    {
        var (service, kimai) = CreateService();

        // Outage: the stop@08:00 lands in the outbox (batch processing plus
        // the trailing flush each burn one failing status call).
        kimai.FailNextStatusCalls = 2;
        var queued = await service.SyncKioskAsync([Kiosk("s1", "stop", T08)]);
        Assert.Equal(1, queued.Buffered);
        Assert.Empty(kimai.Operations);

        // Recovery - and the employee stamps IN LIVE at 12:00 via the live
        // endpoint path (which does not share the sync lock). The outbox now
        // holds a stop OLDER than the running sheet's begin.
        kimai.SimulateLiveStart(T12);

        // Without the stale-event guard this replay derived a stop for the
        // NEW sheet and backdated it to 08:00 - before its own begin (Kimai
        // would reject that with end < begin and the stamp was lost).
        await service.FlushOutboxAsync();

        var op = Assert.Single(kimai.Operations);
        Assert.Equal(("start", T12), op);
        Assert.True(kimai.IsRunning);
    }

    [Fact]
    public async Task StaleQueuedPauseStart_DoesNotTruncateNewerLiveTimesheet()
    {
        var (service, kimai) = CreateService();

        kimai.FailNextStatusCalls = 2;
        var queued = await service.SyncKioskAsync([Kiosk("p1", "pauseStart", T08)]);
        Assert.Equal(1, queued.Buffered);

        // Live recovery: work starts at 12:00, THEN the stale pauseStart@08:00
        // replays. Pausing that newer sheet would backdate its end to 08:00 -
        // four hours of real work silently gone.
        kimai.SimulateLiveStart(T12);

        await service.FlushOutboxAsync();

        var op = Assert.Single(kimai.Operations);
        Assert.Equal(("start", T12), op);
        Assert.True(kimai.IsRunning);
        Assert.False(kimai.ActiveIsPause);
    }

    [Fact]
    public async Task WrongPin_RejectsOnlyFirstEvent_RestOfBatchStaysQueued()
    {
        var (service, kimai) = CreateService();

        // One batch, two events with DIFFERENT credentials' outcomes: b1 has
        // a wrong PIN, b2 is correct. Answering both in ONE request would let
        // an attacker harvest one PIN verdict per event (20 req x 100 events
        // = 2,000 guesses/min); aborting at the first rejection caps ONE
        // request at exactly ONE verdict.
        var result = await service.SyncKioskAsync(
        [
            Kiosk("b1", "start", T08, pin: "0000"),
            Kiosk("b2", "start", T12),
        ]);

        Assert.Collection(
            result.Results,
            r =>
            {
                Assert.Equal("b1", r.EventId);
                Assert.Equal("rejected", r.Status);
                Assert.Equal("Mitarbeiter nicht gefunden oder PIN falsch.", r.Message);
            },
            r =>
            {
                Assert.Equal("b2", r.EventId);
                Assert.Equal("buffered", r.Status);
            });
        Assert.Empty(kimai.Operations);

        // The retained event drains on the NEXT round (still one verdict per
        // round) - a legitimate backlog survives instead of being mass-dropped.
        var retry = await service.SyncKioskAsync([Kiosk("b2", "start", T08)]);

        Assert.Equal(1, retry.Accepted);
        var op = Assert.Single(kimai.Operations);
        Assert.Equal(("start", T08), op);
        Assert.True(kimai.IsRunning);
    }

    [Fact]
    public async Task KioskReplay_WithNfcCardId_AppliesWithoutPin()
    {
        var (service, kimai) = CreateService();

        // NFC-unlocked session whose PIN was never entered: the replay must
        // resolve the employee by the card, mirroring the live kiosk path.
        var result = await service.SyncKioskAsync(
        [
            new OfflineKioskClockEventDto("n1", "max", null, "start", T08, NfcCardId: "04AB"),
        ]);

        Assert.Equal(1, result.Accepted);
        Assert.Equal(("start", T08), Assert.Single(kimai.Operations));
        Assert.True(kimai.IsRunning);
    }

    [Fact]
    public async Task KioskReplay_NfcCardOfAnotherEmployee_IsRejected()
    {
        var (service, kimai) = CreateService();

        // Anna's session id paired with MAX's card: the live path accepts a
        // card only when it maps to the SAME employee id - the replay must
        // never become a way around that check.
        var result = await service.SyncKioskAsync(
        [
            new OfflineKioskClockEventDto("n1", "anna", null, "start", T08, NfcCardId: "04AB"),
        ]);

        var single = Assert.Single(result.Results);
        Assert.Equal("rejected", single.Status);
        Assert.Empty(kimai.Operations);
        Assert.False(kimai.IsRunning);
    }

    [Fact]
    public async Task TransientStartFailure_BuffersEventInsteadOfRejecting()
    {
        // Service-level pin for the begin-backdate re-throw contract: a
        // TRANSIENT failure inside StartAtAsync (e.g. the old-Kimai fallback
        // whose begin-backdate keeps failing after the compensating stop)
        // must surface as "buffered", never as "rejected" - otherwise the
        // whole offline session would be lost silently.
        var (service, kimai) = CreateService();
        kimai.FailNextStartCalls = 2; // batch processing + trailing flush

        var result = await service.SyncKioskAsync([Kiosk("e1", "start", T08)]);

        Assert.Equal(0, result.Accepted);
        var single = Assert.Single(result.Results);
        Assert.Equal("buffered", single.Status);
        Assert.Empty(kimai.Operations);

        // Once Kimai recovers, the buffered event replays cleanly.
        await service.FlushOutboxAsync();

        var op = Assert.Single(kimai.Operations);
        Assert.Equal("start", op.Kind);
        Assert.Equal(T08, op.At);
        Assert.True(kimai.IsRunning);
    }

    [Fact]
    public async Task Switch_Replay_MovesWorkToTaskAndBack()
    {
        var (service, kimai) = CreateService();

        var result = await service.SyncKioskAsync(
        [
            Kiosk("e1", "start", T08),
            Switch("e2", "kx", T10),
            Switch("e3", null, T12),
            Kiosk("e4", "stop", T1230),
        ]);

        Assert.Equal(4, result.Accepted);
        Assert.Equal(
        [
            ("start", T08),
            ("stop", T10), ("start", T10),
            ("stop", T12), ("start", T12),
            ("stop", T1230),
        ], kimai.Operations);
        Assert.Equal([(7, 9), (20, 21), (7, 9)], kimai.StartedTargets.Select(t => (t.ProjectId, t.ActivityId)));
        Assert.Equal("Kunde X", kimai.StartedTargets[1].Description);
        Assert.False(kimai.IsRunning);
    }

    [Fact]
    public async Task Start_Replay_BooksTheChosenTask()
    {
        var (service, kimai) = CreateService();

        var result = await service.SyncKioskAsync(
        [
            StartOn("e1", "kx", T08),
            Kiosk("e2", "stop", T12),
        ]);

        Assert.Equal(2, result.Accepted);
        Assert.Equal([("start", T08), ("stop", T12)], kimai.Operations);
        var target = Assert.Single(kimai.StartedTargets);
        Assert.Equal((20, 21, "Kunde X"), (target.ProjectId, target.ActivityId, target.Description));
    }

    [Fact]
    public async Task Start_OnTask_AppliedLiveAndQueued_IsNoOp()
    {
        var (service, kimai) = CreateService();
        await service.SyncKioskAsync([StartOn("e1", "kx", T08)]);

        var result = await service.SyncKioskAsync([StartOn("e2", "kx", T08)]);

        Assert.Equal("applied", result.Results.Single().Status);
        Assert.Contains("Lief bereits", result.Results.Single().Message);
        Assert.Single(kimai.Operations);
    }

    [Fact]
    public async Task Start_OnDeletedTask_KeepsTheWorkingTimeOnTheDefaultTask()
    {
        // Rejecting would lose the whole time until the next stamp: book the
        // default task instead. Nobody reads the replay message of an applied
        // event, so the note goes onto the timesheet itself.
        var (service, kimai, logger) = CreateServiceWithLogger();

        var result = await service.SyncKioskAsync([StartOn("e1", "gone", T08)]);

        var single = Assert.Single(result.Results);
        Assert.Equal("applied", single.Status);
        Assert.Contains("gelöscht", single.Message);
        var target = Assert.Single(kimai.StartedTargets);
        Assert.Equal((7, 9), (target.ProjectId, target.ActivityId));
        Assert.Contains("gelöscht", target.Description);
        Assert.Contains(logger.Messages, message => message.Contains("no longer exists"));
    }

    [Fact]
    public async Task Switch_AlreadyOnTarget_IsNoOp()
    {
        // The switch was applied live and ALSO queued (request timed out on
        // the kiosk): the replay must not book a zero-length sheet.
        var (service, kimai) = CreateService();
        await service.SyncKioskAsync([Kiosk("e1", "start", T08), Switch("e2", "kx", T10)]);
        var before = kimai.Operations.Count;

        var result = await service.SyncKioskAsync([Switch("e3", "kx", T10)]);

        Assert.Equal("applied", result.Results.Single().Status);
        Assert.Contains("Lief bereits", result.Results.Single().Message);
        Assert.Equal(before, kimai.Operations.Count);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("clockedOut")]
    public async Task Switch_WithoutRunningWork_IsNoOp(string state)
    {
        var (service, kimai) = CreateService();
        IReadOnlyList<OfflineKioskClockEventDto> setup = state == "pause"
            ? [Kiosk("e1", "start", T08), Kiosk("e2", "pauseStart", T10)]
            : [Kiosk("e1", "start", T08), Kiosk("e2", "stop", T10)];
        await service.SyncKioskAsync(setup);
        var before = kimai.Operations.Count;

        var result = await service.SyncKioskAsync([Switch("e3", "kx", T12)]);

        Assert.Equal("applied", result.Results.Single().Status);
        Assert.Contains("nicht nachtragbar", result.Results.Single().Message);
        Assert.Equal(before, kimai.Operations.Count);
    }

    [Fact]
    public async Task Switch_SheetNewerThanEvent_IsObsoleteNoOp()
    {
        var (service, kimai) = CreateService();
        kimai.SimulateLiveStart(T12);

        var result = await service.SyncKioskAsync([Switch("e1", "kx", T10)]);

        Assert.Contains("Veraltet", result.Results.Single().Message);
        Assert.Single(kimai.Operations);
    }

    [Fact]
    public async Task Switch_ToDeletedTask_IsRejected()
    {
        var (service, kimai) = CreateService();
        await service.SyncKioskAsync([Kiosk("e1", "start", T08)]);

        var result = await service.SyncKioskAsync([Switch("e2", "gone", T10)]);

        var single = Assert.Single(result.Results);
        Assert.Equal("rejected", single.Status);
        Assert.Contains("nicht mehr vorhanden", single.Message);
        Assert.Single(kimai.Operations);
    }

    [Fact]
    public async Task Switch_InterruptedAfterStop_ResumesOnTargetInsteadOfClockingOut()
    {
        var (service, kimai) = CreateService();
        await service.SyncKioskAsync([Kiosk("e1", "start", T08)]);

        // Stop succeeds, the start on the task fails transiently: the event
        // buffers and the trailing flush sees NOTHING running. Without the
        // recovery the retry would be a "Lief nicht" no-op and the employee
        // would stay clocked out.
        kimai.FailNextStartCalls = 1;
        await service.SyncKioskAsync([Switch("e2", "kx", T10)]);
        await service.FlushOutboxAsync();

        Assert.Equal([("start", T08), ("stop", T10), ("start", T10)], kimai.Operations);
        Assert.Equal((20, 21), (kimai.StartedTargets[^1].ProjectId, kimai.StartedTargets[^1].ActivityId));
        Assert.True(kimai.IsRunning);
    }

    [Fact]
    public async Task Switch_ClockOutElsewhereRightAfterQueuedSwitch_IsRejectedWithoutPhantomStart()
    {
        // Terminal A queued "switch to Kunde X" at 10:00:00; 20 s later the
        // employee clocked out on terminal B. Kimai then looks exactly like a
        // half-done switch (latest stopped = work sheet ending at the event),
        // but this server never stopped it - resuming would book Kunde X
        // until the next morning.
        var (service, kimai) = CreateService();
        await service.SyncKioskAsync([Kiosk("e1", "start", T08)]);
        kimai.SimulateLiveStop(T10.AddSeconds(20));
        var before = kimai.Operations.Count;

        var result = await service.SyncKioskAsync([Switch("e2", "kx", T10)]);

        var single = Assert.Single(result.Results);
        Assert.Equal("rejected", single.Status);
        Assert.Contains("nicht eindeutig", single.Message);
        Assert.Equal(before, kimai.Operations.Count);
        Assert.False(kimai.IsRunning);
    }

    [Fact]
    public async Task Switch_AppliedLiveThenStoppedNearEvent_IsNoOp()
    {
        // The switch reached Kimai live (the kiosk queued it anyway after a
        // timeout), then the employee clocked out within the tolerance: the
        // stopped sheet already ran on the target - nothing is missing.
        var (service, kimai) = CreateService();
        await service.SyncKioskAsync([Kiosk("e1", "start", T08), Switch("e2", "kx", T10)]);
        kimai.SimulateLiveStop(T10.AddSeconds(20));
        var before = kimai.Operations.Count;

        var result = await service.SyncKioskAsync([Switch("e3", "kx", T10)]);

        Assert.Equal("applied", result.Results.Single().Status);
        Assert.Contains("nicht nachtragbar", result.Results.Single().Message);
        Assert.Equal(before, kimai.Operations.Count);
    }

    [Fact]
    public async Task Switch_RecoveryFailingAgain_StillResumesOnTheNextRetry()
    {
        var (service, kimai) = CreateService();
        await service.SyncKioskAsync([Kiosk("e1", "start", T08)]);

        // Start fails in the batch AND in the recovery of the trailing flush:
        // the marker of the interrupted switch must survive both.
        kimai.FailNextStartCalls = 2;
        await service.SyncKioskAsync([Switch("e2", "kx", T10)]);
        Assert.False(kimai.IsRunning);

        await service.FlushOutboxAsync();

        Assert.Equal([("start", T08), ("stop", T10), ("start", T10)], kimai.Operations);
        Assert.Equal((20, 21), (kimai.StartedTargets[^1].ProjectId, kimai.StartedTargets[^1].ActivityId));
    }

    [Fact]
    public async Task Switch_InterruptedAfterStop_TaskDeletedBeforeRetry_IsDroppedLoudly()
    {
        var (service, kimai, logger, settings) = CreateServiceWithSettings();
        await service.SyncKioskAsync([Kiosk("e1", "start", T08)]);

        // Start fails in the batch AND in the trailing flush: the event stays
        // buffered while the employee is already stopped at 10:00.
        kimai.FailNextStartCalls = 2;
        await service.SyncKioskAsync([Switch("e2", "kx", T10)]);
        Assert.False(kimai.IsRunning);

        // Admin removes the task before the next retry.
        settings.Employees[0].Tasks[0] = new EmployeeTaskSettings { Id = "kx", Label = "Kunde X" };
        await service.FlushOutboxAsync();

        Assert.Equal([("start", T08), ("stop", T10)], kimai.Operations);
        Assert.Contains(logger.Messages, m => m.Contains("dropping kiosk event e2"));
    }

    [Fact]
    public async Task Switch_ToDefault_WhileOnUnknownSheet_Applies()
    {
        // A sheet that matches no task (deleted task, booking from the Kimai
        // UI) is NOT the default task: switching back must still happen.
        var (service, kimai) = CreateService();
        kimai.SimulateLiveStart(T08);

        var result = await service.SyncKioskAsync([Switch("e1", null, T10)]);

        Assert.Equal("applied", result.Results.Single().Status);
        Assert.Equal([("start", T08), ("stop", T10), ("start", T10)], kimai.Operations);
        Assert.Equal((7, 9), (kimai.StartedTargets[^1].ProjectId, kimai.StartedTargets[^1].ActivityId));
    }

    [Fact]
    public async Task Switch_ToDefault_WhileOnDefault_IsNoOp()
    {
        var (service, kimai) = CreateService();
        await service.SyncKioskAsync([Kiosk("e1", "start", T08)]);

        var result = await service.SyncKioskAsync([Switch("e2", null, T10)]);

        Assert.Contains("Lief bereits", result.Results.Single().Message);
        Assert.Single(kimai.Operations);
    }

    [Fact]
    public async Task PauseEnd_InterruptedAfterPauseStop_ResumesTaskThatRanBeforeThePause()
    {
        var (service, kimai) = CreateService();
        await service.SyncKioskAsync([Kiosk("e1", "start", T08), Switch("e2", "kx", T10), Kiosk("e3", "pauseStart", T12)]);

        // The resume-start fails after the pause stop; the trailing flush
        // completes the interrupted pause end.
        kimai.FailNextStartCalls = 1;
        await service.SyncKioskAsync([Kiosk("e4", "pauseEnd", T1230)]);

        Assert.Equal(("start", T1230), kimai.Operations[^1]);
        Assert.Equal((20, 21), (kimai.StartedTargets[^1].ProjectId, kimai.StartedTargets[^1].ActivityId));
        Assert.True(kimai.IsRunning);
        Assert.False(kimai.ActiveIsPause);
    }

    [Fact]
    public async Task PauseEnd_Replay_ResumesTaskThatRanBeforeThePause()
    {
        var (service, kimai) = CreateService();

        await service.SyncKioskAsync(
        [
            Kiosk("e1", "start", T08),
            Switch("e2", "kx", T10),
            Kiosk("e3", "pauseStart", T12),
            Kiosk("e4", "pauseEnd", T1230),
        ]);

        Assert.Equal((20, 21), (kimai.StartedTargets[^1].ProjectId, kimai.StartedTargets[^1].ActivityId));
        Assert.True(kimai.IsRunning);
        Assert.False(kimai.ActiveIsPause);
    }

    [Fact]
    public async Task PauseStart_Replay_BooksNonBillablePauseLikeTheLivePath()
    {
        var (service, kimai) = CreateService();

        await service.SyncKioskAsync([Kiosk("e1", "start", T08), Kiosk("e2", "pauseStart", T12)]);

        var pause = kimai.StartedTargets[^1];
        Assert.Equal((7, 42), (pause.ProjectId, pause.ActivityId));
        Assert.Equal("Pause", pause.Description);
        Assert.False(pause.Billable);
    }

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static OfflineKioskClockEventDto Switch(string eventId, string? taskId, DateTimeOffset at) =>
        new(eventId, "max", "1234", "switch", at, null, taskId);

    private static OfflineKioskClockEventDto StartOn(string eventId, string? taskId, DateTimeOffset at) =>
        new(eventId, "max", "1234", "start", at, null, taskId);

    private static OfflineKioskClockEventDto Kiosk(string eventId, string action, DateTimeOffset at, string? pin = "1234") =>
        new(eventId, "max", pin, action, at);

    private static (OfflineClockService Service, FakeKimaiClient Kimai) CreateService()
    {
        var (service, kimai, _) = CreateServiceWithLogger();
        return (service, kimai);
    }

    private static (OfflineClockService Service, FakeKimaiClient Kimai, RecordingLogger Logger) CreateServiceWithLogger()
    {
        var (service, kimai, logger, _) = CreateServiceWithSettings();
        return (service, kimai, logger);
    }

    private static (OfflineClockService Service, FakeKimaiClient Kimai, RecordingLogger Logger, RuntimeSettings Settings) CreateServiceWithSettings()
    {
        var settings = new RuntimeSettings
        {
            BaseUrl = "http://kimai.test",
            DefaultProjectId = 1,
            DefaultActivityId = 1,

            // Lets the fake distinguish pause timesheets from work timesheets
            // (StartAtAsync with this activity id opens a pause).
            PauseActivityId = 42,

            Employees =
            [
                new EmployeeSettings
                {
                    Id = "max",
                    DisplayName = "Max Mustermann",
                    Pin = "1234",
                    NfcCardId = "04AB",
                    ApiToken = "token",
                    ProjectId = 7,
                    ActivityId = 9,
                    Tasks = [new EmployeeTaskSettings { Id = "kx", Label = "Kunde X", ProjectId = 20, ActivityId = 21 }],
                },
                new EmployeeSettings
                {
                    Id = "anna",
                    DisplayName = "Anna Beispiel",
                    Pin = "5678",
                    NfcCardId = "04CD",
                    ApiToken = "token-anna",
                    ProjectId = 7,
                    ActivityId = 9,
                },
            ],
        };

        var kimai = new FakeKimaiClient();
        var logger = new RecordingLogger();
        var service = new OfflineClockService(
            new InMemorySettingsStore(settings),
            new InMemoryEmployeeService(),
            kimai,
            new InMemoryEventIdStore(),
            logger);

        return (service, kimai, logger, settings);
    }

    /// <summary>
    /// Captures formatted log messages so tests can assert on operational
    /// side effects that are not visible through the public API (e.g. the
    /// outbox flushed-count distinguishes physical duplicate entries from
    /// deduplicated ones).
    /// </summary>
    private sealed class RecordingLogger : ILogger<OfflineClockService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class InMemorySettingsStore(RuntimeSettings settings) : IRuntimeSettingsStore
    {
        public RuntimeSettings Load() => settings;

        public Task SaveAsync(RuntimeSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class InMemoryEventIdStore : IOfflineEventIdStore
    {
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

        public bool TryRegister(string eventId) => _ids.Add(eventId);

        public void Remove(string eventId) => _ids.Remove(eventId);
    }

    private sealed class InMemoryEmployeeService : IEmployeeService
    {
        public IReadOnlyCollection<EmployeeDto> GetEnabledEmployees(RuntimeSettings settings) => [];

        public EmployeeSettings? FindEmployee(RuntimeSettings settings, ClockRequest request) =>
            settings.Employees.FirstOrDefault(employee =>
                employee.Id == request.EmployeeId &&
                (string.IsNullOrWhiteSpace(employee.Pin) || employee.Pin == request.Pin));

        public EmployeeSettings? FindEmployeeByPin(RuntimeSettings settings, string? pin) => null;

        public EmployeeSettings? FindEmployeeByNfcCardId(RuntimeSettings settings, string? cardId) =>
            settings.Employees.FirstOrDefault(employee =>
                string.Equals(employee.NfcCardId, cardId, StringComparison.OrdinalIgnoreCase));

        public EmployeeDto ToEmployeeDto(EmployeeSettings employee) =>
            new(employee.Id, employee.DisplayName, string.Empty, employee.Color, null, !string.IsNullOrEmpty(employee.Pin));
    }

    /// <summary>
    /// Minimal Kimai fake: tracks start/stop operations in order and can fail
    /// the next N status calls with a transient network error to simulate an
    /// outage/recovery window.
    ///
    /// Simplification to keep in mind when reading multi-employee assertions:
    /// the timesheet state is GLOBAL across all employees (one shared active
    /// timesheet), unlike real Kimai where each employee has their own.
    /// </summary>
    private sealed class FakeKimaiClient : IKimaiClient
    {
        public List<(string Kind, DateTimeOffset At)> Operations { get; } = [];

        public int FailNextStatusCalls { get; set; }

        /// <summary>
        /// Fails the next N StartAtAsync calls with a transient network error -
        /// used to cut a two-step transaction (pauseStart/pauseEnd) in half.
        /// </summary>
        public int FailNextStartCalls { get; set; }

        private int _timesheetCounter;
        private int? _activeTimesheetId;
        private int? _activeActivityId;
        private int? _activeProjectId;
        private bool _activeIsPause;
        private DateTimeOffset? _activeBeganAt;
        private readonly List<(int ActivityId, int? ProjectId, DateTimeOffset EndedAt)> _stoppedTimesheets = [];

        /// <summary>Every started target in order (for task assertions).</summary>
        public List<KimaiTimesheetTarget> StartedTargets { get; } = [];

        public bool IsRunning => _activeTimesheetId is not null;

        public bool ActiveIsPause => _activeTimesheetId is not null && _activeIsPause;

        /// <summary>
        /// Simulates a LIVE stamp via the live endpoint path (which does not
        /// share the offline service's sync lock): the active sheet begins at
        /// <paramref name="beganAt"/> without any replay operation involved.
        /// Used to build replay-vs-live race scenarios.
        /// </summary>
        public void SimulateLiveStart(DateTimeOffset beganAt)
        {
            Operations.Add(("start", beganAt));
            _activeTimesheetId = ++_timesheetCounter;
            _activeActivityId = 9;
            _activeProjectId = null;
            _activeIsPause = false;
            _activeBeganAt = beganAt;
        }

        /// <summary>
        /// Simulates a LIVE stop (e.g. Ausstempeln on another terminal) of
        /// the running sheet at <paramref name="stoppedAt"/>.
        /// </summary>
        public void SimulateLiveStop(DateTimeOffset stoppedAt)
        {
            Operations.Add(("stop", stoppedAt));
            _stoppedTimesheets.Add((_activeActivityId!.Value, _activeProjectId, stoppedAt));
            _activeTimesheetId = null;
            _activeActivityId = null;
            _activeProjectId = null;
            _activeIsPause = false;
            _activeBeganAt = null;
        }

        public Task<ClockStatusDto> GetStatusAsync(
            RuntimeSettings settings,
            EmployeeSettings employee,
            CancellationToken cancellationToken = default)
        {
            if (FailNextStatusCalls > 0)
            {
                FailNextStatusCalls--;
                throw new HttpRequestException("simulated Kimai outage");
            }

            var running = _activeTimesheetId is not null;
            var state = !running ? "clockedOut" : _activeIsPause ? "paused" : "working";
            var task = running && !_activeIsPause ? WorkTargetResolver.MatchTask(employee, _activeProjectId, _activeActivityId) : null;
            var onDefault = running && !_activeIsPause && task is null
                && WorkTargetResolver.IsDefault(settings, employee, _activeProjectId, _activeActivityId);
            return Task.FromResult(new ClockStatusDto(
                running,
                _activeTimesheetId,
                running ? (_activeBeganAt ?? Parse("2026-08-24T00:00:00Z")).ToString("yyyy-MM-dd'T'HH:mm:sszzz") : null,
                0,
                state,
                running ? "Eingestempelt" : "Nicht eingestempelt",
                task?.Id,
                task?.Label,
                onDefault));
        }

        public Task StartAtAsync(
            RuntimeSettings settings,
            EmployeeSettings employee,
            KimaiTimesheetTarget target,
            DateTimeOffset startedAt,
            CancellationToken cancellationToken = default)
        {
            if (FailNextStartCalls > 0)
            {
                FailNextStartCalls--;
                throw new HttpRequestException("simulated transient start failure");
            }

            Operations.Add(("start", startedAt));
            _activeTimesheetId = ++_timesheetCounter;
            StartedTargets.Add(target);
            _activeActivityId = target.ActivityId;
            _activeProjectId = target.ProjectId;
            _activeIsPause = settings.PauseActivityId == target.ActivityId;
            _activeBeganAt = startedAt;
            return Task.CompletedTask;
        }

        public Task StopAtAsync(
            RuntimeSettings settings,
            EmployeeSettings employee,
            int timesheetId,
            DateTimeOffset stoppedAt,
            CancellationToken cancellationToken = default)
        {
            Operations.Add(("stop", stoppedAt));
            if (_activeActivityId is int activityId)
            {
                _stoppedTimesheets.Add((activityId, _activeProjectId, stoppedAt));
            }

            _activeTimesheetId = null;
            _activeActivityId = null;
            _activeProjectId = null;
            _activeIsPause = false;
            _activeBeganAt = null;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<KimaiRecentTimesheetDto>> GetRecentStoppedTimesheetsAsync(
            RuntimeSettings settings,
            EmployeeSettings employee,
            int count,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<KimaiRecentTimesheetDto> recent = _stoppedTimesheets
                .OrderByDescending(sheet => sheet.EndedAt)
                .Take(count)
                .Select(sheet => new KimaiRecentTimesheetDto(sheet.ActivityId, sheet.EndedAt, sheet.ProjectId))
                .ToList();
            return Task.FromResult(recent);
        }

        public Task<string?> GetCurrentUserTimezoneAsync(
            RuntimeSettings settings,
            EmployeeSettings employee,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("Europe/Berlin");

        public Task StartAsync(RuntimeSettings settings, EmployeeSettings employee, KimaiTimesheetTarget target, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task StopAsync(RuntimeSettings settings, EmployeeSettings employee, int timesheetId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<KimaiUserDto>> GetUsersAsync(string baseUrl, string apiToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<KimaiActivityDto>> GetActivitiesAsync(string baseUrl, string apiToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<KimaiProjectDto>> GetProjectsAsync(string baseUrl, string apiToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<KimaiTimesheetEntryDto>> GetTimesheetsAsync(
            RuntimeSettings settings,
            EmployeeSettings employee,
            DateTime begin,
            DateTime end,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
