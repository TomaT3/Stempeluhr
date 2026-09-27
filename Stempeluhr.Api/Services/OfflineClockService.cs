using System.Globalization;
using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

/// <summary>
/// Applies queued offline kiosk actions to Kimai in event-time order.
///
/// Design notes:
/// - Idempotency: each event ID is remembered (persisted JSON store). Replayed
///   batches after a network hiccup are acknowledged as duplicates. On top of
///   that every action is checked against Kimai's current state, so an action
///   that was already applied live becomes a no-op ("Lief bereits", ...).
/// - Backdating: start/stop use the stored action timestamp, so the recorded
///   time matches the moment the button was pressed.
/// - Kimai outage: if Kimai is unreachable while syncing, remaining events are
///   kept in an internal outbox. The service is registered as a singleton and
///   a background service flushes the outbox periodically, so a sync call
///   during a Kimai outage never loses events and they are retried without a
///   new request from the client.
/// - Known limit: the ordering guarantee covers the OUTBOX replay. Live
///   applies (outbox empty, Kimai reachable) run in REQUEST-ARRIVAL order -
///   two kiosks syncing independently right after a recovery can interleave
///   by arrival instead of event-time order (within one request the internal
///   order always holds). Keep one terminal per employee during an outage
///   where the order matters.
/// - Concurrency: every read/mutation of the outbox happens while holding
///   <see cref="_syncLock"/>, so sync requests serialize against each other
///   and against the periodic background flush.
/// - Permanent errors (unknown employee, wrong PIN, missing config) are
///   reported per event as "rejected" instead of failing the whole batch.
/// </summary>
public sealed class OfflineClockService(
    IRuntimeSettingsStore settingsStore,
    IEmployeeService employees,
    IKimaiClient kimai,
    IOfflineEventIdStore eventIdStore,
    ILogger<OfflineClockService> logger) : IOfflineClockService
{
    private const string BufferedStatus = "buffered";
    private const string BufferedMessage = "Kimai nicht erreichbar - wird automatisch nachgetragen.";

    // Plain list instead of a queue: every mutation happens while holding
    // _syncLock, and an entry whose replay failed transiently must go back to
    // the FRONT (Insert at index 0). A queue would move it to the tail and the
    // next flush would start with a LATER action of the same employee.
    private readonly List<OfflineKioskClockEventDto> _kioskOutbox = new();

    // Physical dedup mirroring the outbox contents: the client re-sends
    // buffered events on a slow safety-net timer (offline-queue.ts) while the
    // event IDs are already FREED (buffering removes them from the store), so
    // without this set every re-send would enqueue yet another COPY of the
    // same event - one per retry interval across the whole outage. Guarded by
    // _syncLock like the list itself.
    private readonly HashSet<string> _kioskOutboxIds = new(StringComparer.Ordinal);

    // Event IDs of task switches whose replay stopped the work sheet and then
    // failed on the start. Only these may resume on the target while nothing
    // runs: an ordinary clock-out right after a queued switch leaves the same
    // Kimai state (latest stopped = work sheet ending near the event), so the
    // Kimai state alone cannot tell our own half-done switch apart. Guarded
    // by _syncLock; lost on restart - the replay then rejects instead of
    // guessing (see ApplySwitchAsync).
    private readonly HashSet<string> _interruptedSwitches = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public async Task<OfflineSyncResultDto> SyncKioskAsync(IReadOnlyList<OfflineKioskClockEventDto> events, CancellationToken cancellationToken = default)
    {
        var accepted = 0;
        var duplicates = 0;
        var buffered = 0;
        var results = new List<OfflineSyncEventResultDto>();

        // Malformed entries must be reported back explicitly so the sender can
        // drop them from its queue instead of silently retrying them forever.
        foreach (var invalid in events.Where(e => string.IsNullOrWhiteSpace(e.EventId) || string.IsNullOrWhiteSpace(e.EmployeeId)))
        {
            results.Add(new OfflineSyncEventResultDto(invalid.EventId ?? string.Empty, "rejected",
                "EventId und EmployeeId sind erforderlich."));
        }

        var orderedKioskEvents = events
            .Where(e => !string.IsNullOrWhiteSpace(e.EventId) && !string.IsNullOrWhiteSpace(e.EmployeeId))
            .OrderBy(e => e.PerformedAt)
            .ToList();

        // Serialize the whole operation against parallel syncs and the
        // background flush (List<T> is not thread-safe, and the ordering
        // guarantee depends on serialization).
        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            // Never let a fresh batch jump over events that are still waiting
            // in the outbox.
            var queuedBehindBacklog = await BufferBatchBehindBacklogAsync(orderedKioskEvents, results, cancellationToken);
            if (queuedBehindBacklog > 0)
            {
                buffered += queuedBehindBacklog;
                return new OfflineSyncResultDto(accepted, duplicates, buffered, results);
            }

            void BufferKioskFrom(IReadOnlyList<OfflineKioskClockEventDto> pendingEvents, int failedIndex)
            {
                // The failed event and everything after it must replay
                // together. Applying later actions against a Kimai
                // state that misses the buffered ones turns them into "Lief nicht"
                // no-ops that get acknowledged as applied - silently losing them.
                for (var i = failedIndex; i < pendingEvents.Count; i++)
                {
                    var pending = pendingEvents[i];
                    AddToOutbox(pending);
                    buffered++;
                    results.Add(new OfflineSyncEventResultDto(pending.EventId, BufferedStatus, BufferedMessage));
                }
            }

            for (var i = 0; i < orderedKioskEvents.Count; i++)
            {
                var entry = orderedKioskEvents[i];
                cancellationToken.ThrowIfCancellationRequested();

                if (!eventIdStore.TryRegister(entry.EventId))
                {
                    duplicates++;
                    results.Add(new OfflineSyncEventResultDto(entry.EventId, "duplicate", null));
                    continue;
                }

                try
                {
                    var (message, state) = await ApplyKioskEventAsync(entry, cancellationToken);
                    accepted++;
                    results.Add(new OfflineSyncEventResultDto(entry.EventId, "applied", message, state));
                }
                catch (KimaiApiException ex) when (IsRetryable(ex))
                {
                    eventIdStore.Remove(entry.EventId);
                    BufferKioskFrom(orderedKioskEvents, i);
                    break;
                }
                catch (Exception ex) when (IsTransientNetworkError(ex))
                {
                    // Kimai unreachable at the network level - keep for retry.
                    eventIdStore.Remove(entry.EventId);
                    BufferKioskFrom(orderedKioskEvents, i);
                    break;
                }
                catch (KioskAuthenticationException ex)
                {
                    // Brute-force containment: every remaining event of this
                    // batch carries the same credentials as this one, so
                    // applying them anyway would let ONE request harvest a
                    // PIN verdict per event. The rest is acknowledged as
                    // buffered WITHOUT server-side copies: the client keeps
                    // its queue and retries, so a legitimate backlog survives
                    // and drains ONE verdict per round instead of being
                    // mass-rejected into data loss.
                    logger.LogWarning(
                        ex,
                        "Offline kiosk event {EventId} rejected ({Message}) - skipping the remaining {Skipped} event(s) of this batch",
                        entry.EventId, ex.Message, orderedKioskEvents.Count - i - 1);
                    results.Add(new OfflineSyncEventResultDto(entry.EventId, "rejected", ex.Message));
                    var skipped = orderedKioskEvents.Count - i - 1;
                    const string skippedMessage = "Uebersprungen - die Anmeldedaten dieses Batches wurden abgelehnt.";
                    for (var s = i + 1; s < orderedKioskEvents.Count; s++)
                    {
                        results.Add(new OfflineSyncEventResultDto(orderedKioskEvents[s].EventId, BufferedStatus, skippedMessage));
                    }

                    buffered += skipped;
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Offline kiosk event {EventId} permanently rejected", entry.EventId);
                    results.Add(new OfflineSyncEventResultDto(entry.EventId, "rejected", ex.Message));
                }
            }

            await FlushOutboxCoreAsync(cancellationToken);
        }
        finally
        {
            _syncLock.Release();
        }

        return new OfflineSyncResultDto(accepted, duplicates, buffered, results);
    }

    /// <summary>
    /// Cross-batch rule: when the outbox still holds events (e.g. the client
    /// lost its own queue - the exact case this safety-net exists for), a newly
    /// arriving batch must not race ahead of it. Otherwise later actions would
    /// be applied against a Kimai state that misses earlier ones, turning them
    /// into "Lief nicht" no-ops acknowledged as applied. Drains what it can; if
    /// a backlog survives (Kimai still unreachable), the incoming batch is
    /// appended behind it instead of being applied live, followed by one
    /// opportunistic flush. MUST be called while holding
    /// <see cref="_syncLock"/>. Returns the number of buffered events (0 means
    /// no backlog existed and the caller processes live).
    /// </summary>
    private async Task<int> BufferBatchBehindBacklogAsync(
        IReadOnlyList<OfflineKioskClockEventDto> orderedEvents,
        List<OfflineSyncEventResultDto> results,
        CancellationToken cancellationToken)
    {
        await FlushOutboxCoreAsync(cancellationToken);
        if (_kioskOutbox.Count == 0)
        {
            return 0;
        }

        foreach (var entry in orderedEvents)
        {
            // Physical dedup: a re-sent event that is already waiting in the
            // outbox must not add another copy (see _kioskOutboxIds). The
            // sender still gets "buffered" - the server DOES hold it and will
            // apply it on recovery.
            AddToOutbox(entry);
            results.Add(new OfflineSyncEventResultDto(entry.EventId, BufferedStatus, BufferedMessage));
        }

        await FlushOutboxCoreAsync(cancellationToken);
        return orderedEvents.Count;
    }

    /// <summary>
    /// Appends an entry to the outbox unless its event ID is already queued.
    /// Callers must hold <see cref="_syncLock"/>.
    /// </summary>
    private void AddToOutbox(OfflineKioskClockEventDto entry)
    {
        if (_kioskOutboxIds.Add(entry.EventId))
        {
            _kioskOutbox.Add(entry);
        }
    }

    private async Task<(string Message, string State)> ApplyKioskEventAsync(OfflineKioskClockEventDto entry, CancellationToken cancellationToken)
    {
        var settings = settingsStore.Load();
        var employee = ResolveKioskEmployee(settings, entry);

        var action = NormalizeKioskAction(entry.Action);
        return await ApplyActionAsync(settings, employee, action, entry.EventId, entry.TaskId, entry.PerformedAt, cancellationToken);
    }

    /// <summary>
    /// Mirrors the live kiosk path (ClockService.FindEmployeeForClockAction):
    /// PIN first; when that does not match, the NFC card that unlocked the
    /// session identifies the employee - replaying a queued action of an
    /// NFC-unlocked session whose PIN was never entered used to fail forever
    /// ("PIN falsch") and silently drop the stamp. The card is only accepted
    /// when it maps to the SAME employee id, so one card can never stamp for
    /// someone else. Same trust model as the live path: whoever presents card
    /// or PIN counts as that employee (terminal-token auth remains the agreed
    /// follow-up).
    /// </summary>
    private EmployeeSettings ResolveKioskEmployee(RuntimeSettings settings, OfflineKioskClockEventDto entry)
    {
        var byPin = employees.FindEmployee(settings, new ClockRequest(entry.EmployeeId, entry.Pin));
        if (byPin is not null)
        {
            return byPin;
        }

        var byCard = employees.FindEmployeeByNfcCardId(settings, entry.NfcCardId);
        if (byCard is null || !string.Equals(byCard.Id, entry.EmployeeId, StringComparison.OrdinalIgnoreCase))
        {
            throw new KioskAuthenticationException("Mitarbeiter nicht gefunden oder PIN falsch.");
        }

        return byCard;
    }

    /// <summary>
    /// Authentication failure while replaying one kiosk event (unknown
    /// employee, wrong PIN, card/employee mismatch). Deliberately its own
    /// type so the sync loop can contain credential enumeration: every
    /// further event of a batch shares the same credentials, so answering
    /// each of them individually would turn ONE request into one PIN guess
    /// per event - every result reveals whether its credentials matched.
    /// </summary>
    private sealed class KioskAuthenticationException(string message) : InvalidOperationException(message);

    private static string NormalizeKioskAction(string? action)
    {
        return string.Equals(action, "start", StringComparison.OrdinalIgnoreCase) ? "start"
            : string.Equals(action, "stop", StringComparison.OrdinalIgnoreCase) ? "stop"
            : string.Equals(action, "pauseStart", StringComparison.OrdinalIgnoreCase) ? "pauseStart"
            : string.Equals(action, "pauseEnd", StringComparison.OrdinalIgnoreCase) ? "pauseEnd"
            : string.Equals(action, "switch", StringComparison.OrdinalIgnoreCase) ? "switch"
            : throw new InvalidOperationException($"Unbekannte Aktion: {action}");
    }

    /// <summary>
    /// Applies one clock action at a historical point in time. Returns the
    /// human-readable result plus the resulting clock state
    /// ("working"/"paused"/"clockedOut").
    /// </summary>
    private async Task<(string Message, string State)> ApplyActionAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        string action,
        string eventId,
        string? taskId,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var status = await kimai.GetStatusAsync(settings, employee, cancellationToken);

        switch (action)
        {
            case "start":
                if (status.IsRunning)
                {
                    return ("Lief bereits - kein Nachtrag noetig.", status.State);
                }

                var startTarget = ResolveStartTarget(settings, employee, taskId);
                await kimai.StartAtAsync(settings, employee, startTarget, timestamp, cancellationToken);
                return (startTarget.TaskId is null && !string.IsNullOrWhiteSpace(taskId)
                    ? $"Nachgetragen: Einstempeln {timestamp.ToLocalTime():HH:mm} ({DeletedTaskNote})"
                    : $"Nachgetragen: Einstempeln {timestamp.ToLocalTime():HH:mm}", "working");

            case "stop":
                if (!status.IsRunning || status.ActiveTimesheetId is not int stopId)
                {
                    return ("Lief nicht - kein Nachtrag noetig.", status.State);
                }

                // Replay-vs-live race: while this event waited in an outbox,
                // the employee may have stamped IN live again (live endpoints
                // do not share _syncLock). Stopping that newer timesheet with
                // this old timestamp would truncate real work time or fail in
                // Kimai (end < begin -> 400) and drop the stamp as permanent.
                // A sheet that began AFTER the event cannot be its target -
                // an obsolete no-op is the honest answer.
                if (IsSheetNewerThanEvent(status.StartedAt, timestamp))
                {
                    logger.LogWarning(
                        "Offline stop at {Timestamp}: active timesheet started at {StartedAt} - after the event; obsolete no-op",
                        timestamp, status.StartedAt);
                    return (ObsoleteEventMessage, status.State);
                }

                await kimai.StopAtAsync(settings, employee, stopId, timestamp, cancellationToken);
                return ($"Nachgetragen: Ausstempeln {timestamp.ToLocalTime():HH:mm}", "clockedOut");

            case "pauseStart":
                // A pause that already runs is this very action applied live
                // before its request timed out on the kiosk (the kiosk then
                // queued it as well). Stopping it and opening another pause
                // would leave a zero-length pause timesheet behind.
                if (status.State == "paused")
                {
                    return ("Pause lief bereits - kein Nachtrag noetig.", status.State);
                }

                if (!status.IsRunning || status.ActiveTimesheetId is not int pauseStopId)
                {
                    return ("Lief nicht - Pause nicht nachtragbar.", status.State);
                }

                var pause = WorkTargetResolver.ResolvePause(settings, employee)
                    ?? throw new InvalidOperationException("Projekt und Pausen-Aktivitaet muessen konfiguriert sein.");

                // Same replay-vs-live race as the stop case: pausing a sheet
                // that began AFTER the event would backdate its end to before
                // its own begin (Kimai rejects that and the event is lost).
                if (IsSheetNewerThanEvent(status.StartedAt, timestamp))
                {
                    logger.LogWarning(
                        "Offline pauseStart at {Timestamp}: work timesheet started at {StartedAt} - after the event; obsolete no-op",
                        timestamp, status.StartedAt);
                    return (ObsoleteEventMessage, status.State);
                }

                // Two-step transaction: end work, then open the pause from that
                // moment on. If the second call fails retryably, the employee
                // is left stopped and a later replay sees IsRunning == false
                // ("Lief nicht") - the pause is then lost rather than applied
                // twice. Accepted trade-off: Kimai has no transactional
                // stop+start; the alternative (compensating restart of the work
                // timesheet) would risk double-applying on ambiguous failures.
                await kimai.StopAtAsync(settings, employee, pauseStopId, timestamp, cancellationToken);
                await kimai.StartAtAsync(settings, employee, pause, timestamp, cancellationToken);
                return ($"Nachgetragen: Pausenbeginn {timestamp.ToLocalTime():HH:mm}", "paused");

            case "pauseEnd":
                // Mirror the live path: only end a pause that is actually running.
                if (status.State == "paused" && status.ActiveTimesheetId is int endPauseId)
                {
                    // Same replay-vs-live race as the stop case: a stale
                    // pauseEnd must not kill a pause that began after it.
                    if (IsSheetNewerThanEvent(status.StartedAt, timestamp))
                    {
                        logger.LogWarning(
                            "Offline pauseEnd at {Timestamp}: pause timesheet started at {StartedAt} - after the event; obsolete no-op",
                            timestamp, status.StartedAt);
                        return (ObsoleteEventMessage, status.State);
                    }

                    RequireDefaultTarget(settings, employee);

                    // Resume the task the pause interrupted (live parity,
                    // ClockService.EndPauseAsync): read BEFORE stopping the
                    // pause, while that sheet is still the latest stopped one.
                    var resume = WorkTargetResolver.ResolveResume(
                        settings, employee, await GetTimesheetBeforePauseAsync(settings, employee, cancellationToken))!;

                    await kimai.StopAtAsync(settings, employee, endPauseId, timestamp, cancellationToken);
                    await kimai.StartAtAsync(settings, employee, resume, timestamp, cancellationToken);
                    return ($"Nachgetragen: Pausenende {timestamp.ToLocalTime():HH:mm}", "working");
                }

                if (!status.IsRunning)
                {
                    // Partial-application recovery: pauseEnd is a two-step
                    // transaction (stop the pause timesheet, resume work). If a
                    // previous replay stopped the pause but then failed
                    // transiently on the resume-start, this retry arrives while
                    // NOTHING is running. Answering the old no-op here would
                    // acknowledge the event as applied and leave the employee
                    // clocked out for the rest of the day.
                    //
                    // But "nothing running" alone proves nothing: a LIVE stop
                    // or another terminal's action may have ended the pause
                    // before this event ever reached Kimai. Resuming blindly
                    // would book a phantom work timesheet that runs until the
                    // next stamp. So require the fingerprint our own
                    // interrupted attempt leaves behind: the latest STOPPED
                    // timesheet is a PAUSE timesheet whose end matches this
                    // event's timestamp (StopAt wrote it right before failing).
                    // Two entries: the stopped pause plus the sheet it
                    // interrupted, so the resume picks the task from before
                    // the pause (live parity).
                    var recent = await FindInterruptedTransitionAsync(settings, employee, timestamp, stoppedPause: true, 2, cancellationToken);
                    if (recent is null)
                    {
                        logger.LogWarning(
                            "Offline pauseEnd at {Timestamp}: no pause running and no matching interrupted pause stop - not resuming work",
                            timestamp);
                        return ("Keine laufende Pause - Nachtrag nicht moeglich.", status.State);
                    }

                    logger.LogWarning(
                        "Offline pauseEnd at {Timestamp}: completing an interrupted pause end by resuming work",
                        timestamp);

                    var resumeAfterInterruption = WorkTargetResolver.ResolveResume(settings, employee, recent.ElementAtOrDefault(1))
                        ?? RequireDefaultTarget(settings, employee);
                    await kimai.StartAtAsync(settings, employee, resumeAfterInterruption, timestamp, cancellationToken);
                    return ($"Nachgetragen: Pausenende {timestamp.ToLocalTime():HH:mm}", "working");
                }

                return ("Keine laufende Pause - Nachtrag nicht moeglich.", status.State);

            case "switch":
                return await ApplySwitchAsync(settings, employee, status, eventId, taskId, timestamp, cancellationToken);

            default:
                throw new InvalidOperationException($"Unbekannte Aktion: {action}");
        }
    }

    /// <summary>
    /// Task switch: stop the running work sheet, continue on the target task
    /// from the event's timestamp on. Every branch can become a no-op against
    /// the current Kimai state (already on the target, paused, clocked out,
    /// sheet newer than the event), so a switch that was applied live AND
    /// queued never books twice.
    /// </summary>
    private async Task<(string Message, string State)> ApplySwitchAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        ClockStatusDto status,
        string eventId,
        string? taskId,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        // The marker survives only a TRANSIENT failure (the event buffers and
        // comes back); any final outcome - applied, no-op, rejected - ends it.
        var interruptedHere = _interruptedSwitches.Contains(eventId);
        try
        {
            var result = await ApplySwitchCoreAsync(settings, employee, status, eventId, interruptedHere, taskId, timestamp, cancellationToken);
            _interruptedSwitches.Remove(eventId);
            return result;
        }
        catch (Exception ex) when (!(ex is KimaiApiException kimaiEx ? IsRetryable(kimaiEx) : IsTransientNetworkError(ex)))
        {
            _interruptedSwitches.Remove(eventId);
            throw;
        }
    }

    private async Task<(string Message, string State)> ApplySwitchCoreAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        ClockStatusDto status,
        string eventId,
        bool interruptedHere,
        string? taskId,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        if (status.State == "paused")
        {
            return ("In Pause - Wechsel nicht nachtragbar.", status.State);
        }

        var target = WorkTargetResolver.Resolve(settings, employee, taskId);

        if (!status.IsRunning || status.ActiveTimesheetId is not int switchStopId)
        {
            // Partial-application recovery: a previous replay of THIS event
            // stopped the work sheet but failed transiently on the start.
            // The Kimai fingerprint (latest stopped = work sheet ending at
            // the event) is necessary but not sufficient - an ordinary
            // clock-out on another terminal right after the queued switch
            // leaves the same state - so resuming also requires our own
            // marker (_interruptedSwitches).
            var stopped = (await FindInterruptedTransitionAsync(settings, employee, timestamp, stoppedPause: false, 1, cancellationToken))?[0];
            if (stopped is null
                || (target is not null && WorkTargetResolver.BooksOn(target, stopped.ProjectId, stopped.ActivityId)))
            {
                // Nothing stopped near the event, or the stopped sheet already
                // ran on the target (the switch happened live, then a stop).
                return ("Lief nicht - Wechsel nicht nachtragbar.", status.State);
            }

            if (!interruptedHere)
            {
                // Looks like a half-done switch, but this server did not stop
                // that sheet for this event (clock-out elsewhere, or a restart
                // lost the marker). Starting the target would book a sheet
                // that runs all night; acknowledging would hide a possibly
                // lost switch. Reject: the kiosk reports it for a check.
                logger.LogWarning(
                    "Offline switch {EventId} at {Timestamp}: work sheet stopped near the event, but not by this replay - rejecting",
                    eventId, timestamp);
                throw new InvalidOperationException("Wechsel nicht eindeutig nachtragbar - bitte in Kimai pruefen.");
            }

            if (target is null)
            {
                // Our own stop already ended the work - a silent no-op would
                // leave the employee clocked out without any notice.
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(taskId)
                    ? "Projekt und Aktivitaet muessen konfiguriert sein."
                    : "Taetigkeit nicht mehr vorhanden.");
            }

            await StartSwitchTargetAsync(settings, employee, eventId, target, timestamp, cancellationToken);
            return (SwitchAppliedMessage(target, timestamp), "working");
        }

        if (target is null)
        {
            // Task deleted in the admin area while the event waited: nothing
            // to switch to - reject so the kiosk reports the missing time.
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(taskId)
                ? "Projekt und Aktivitaet muessen konfiguriert sein."
                : "Taetigkeit nicht mehr vorhanden.");
        }

        if (WorkTargetResolver.IsRunningOn(status, target))
        {
            return ("Lief bereits - kein Nachtrag noetig.", status.State);
        }

        // Same replay-vs-live race as stop/pauseStart: a sheet that began
        // AFTER the event cannot be its source.
        if (IsSheetNewerThanEvent(status.StartedAt, timestamp))
        {
            logger.LogWarning(
                "Offline switch at {Timestamp}: work timesheet started at {StartedAt} - after the event; obsolete no-op",
                timestamp, status.StartedAt);
            return (ObsoleteEventMessage, status.State);
        }

        await kimai.StopAtAsync(settings, employee, switchStopId, timestamp, cancellationToken);
        await StartSwitchTargetAsync(settings, employee, eventId, target, timestamp, cancellationToken);
        return (SwitchAppliedMessage(target, timestamp), "working");
    }

    /// <summary>
    /// Second step of a switch. A failure leaves the employee stopped, so the
    /// event is marked as interrupted by this server: its retry may then
    /// resume on the target (see <see cref="_interruptedSwitches"/>).
    /// </summary>
    private async Task StartSwitchTargetAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        string eventId,
        KimaiTimesheetTarget target,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        try
        {
            await kimai.StartAtAsync(settings, employee, target, timestamp, cancellationToken);
        }
        catch
        {
            _interruptedSwitches.Add(eventId);
            throw;
        }
    }

    private static string SwitchAppliedMessage(KimaiTimesheetTarget target, DateTimeOffset timestamp)
    {
        return $"Nachgetragen: Wechsel zu {WorkTargetResolver.DisplayName(target)} {timestamp.ToLocalTime():HH:mm}";
    }

    /// <summary>
    /// Tätigkeit eines nachgetragenen Einstempelns. Anders als beim Wechsel
    /// (dort läuft die bisherige Arbeit weiter) ginge mit einer Ablehnung die
    /// ganze Arbeitszeit bis zum nächsten Stempel verloren. Ist die Tätigkeit
    /// inzwischen gelöscht, wird deshalb auf die Haupttätigkeit gebucht. Die
    /// Nachtrags-Meldung sieht am Kiosk niemand - der Vermerk steht deshalb in
    /// der Beschreibung des Timesheets, also dort, wo die Zeit pro Kunde
    /// ausgewertet und korrigiert wird.
    /// </summary>
    private KimaiTimesheetTarget ResolveStartTarget(RuntimeSettings settings, EmployeeSettings employee, string? taskId)
    {
        if (WorkTargetResolver.Resolve(settings, employee, taskId) is { } target)
        {
            return target;
        }

        var fallback = RequireDefaultTarget(settings, employee);
        logger.LogWarning(
            "Offline start on task {TaskId}: task no longer exists - booking the default task instead",
            taskId);
        return fallback with { Description = $"{fallback.Description} ({DeletedTaskNote})" };
    }

    private static KimaiTimesheetTarget RequireDefaultTarget(RuntimeSettings settings, EmployeeSettings employee)
    {
        return WorkTargetResolver.ResolveDefault(settings, employee)
            ?? throw new InvalidOperationException("Projekt und Aktivitaet muessen konfiguriert sein.");
    }

    /// <summary>
    /// Latest stopped timesheet while a pause runs = the sheet the pause
    /// interrupted. Transient failures propagate (the event buffers); a
    /// permanent Kimai answer only costs the task choice (default task).
    /// Without further tasks the answer cannot matter - no request.
    /// </summary>
    private async Task<KimaiRecentTimesheetDto?> GetTimesheetBeforePauseAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        CancellationToken cancellationToken)
    {
        if (employee.Tasks is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            var recent = await kimai.GetRecentStoppedTimesheetsAsync(settings, employee, 1, cancellationToken);
            return recent.FirstOrDefault();
        }
        catch (KimaiApiException ex) when (!IsRetryable(ex))
        {
            logger.LogWarning(ex, "Offline pauseEnd: timesheet before the pause could not be read - resuming the default task");
            return null;
        }
    }

    /// <summary>
    /// True when the Kimai state matches an interrupted two-step transaction
    /// (pauseEnd: <paramref name="stoppedPause"/> true, switch: false): the
    /// latest stopped timesheet is a PAUSE (resp. WORK) timesheet that ended
    /// at this event's timestamp - exactly what StopAt(sheet, timestamp)
    /// writes in the successful first step before the following start fails
    /// transiently. The small tolerance only absorbs timestamp rounding;
    /// anything further away (a later live stop, another terminal's action)
    /// must NOT trigger a phantom start. A transient failure of the lookup
    /// itself propagates to the caller, so the event buffers and retries as
    /// usual. Returns the <paramref name="count"/> latest stopped timesheets
    /// (the matching one first), or null when the state does not match.
    /// </summary>
    private async Task<IReadOnlyList<KimaiRecentTimesheetDto>?> FindInterruptedTransitionAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        DateTimeOffset timestamp,
        bool stoppedPause,
        int count,
        CancellationToken cancellationToken)
    {
        var what = stoppedPause ? "pauseEnd" : "switch";
        if (stoppedPause && settings.PauseActivityId is null)
        {
            // Without a configured pause activity a stopped timesheet cannot
            // be identified as a pause - stay conservative (no-op + loud log).
            return null;
        }

        var recent = await kimai.GetRecentStoppedTimesheetsAsync(settings, employee, count, cancellationToken);
        if (recent.FirstOrDefault() is not { ActivityId: int activityId, EndedAt: DateTimeOffset ended }
            || (activityId == settings.PauseActivityId) != stoppedPause)
        {
            return null;
        }

        var differenceSeconds = Math.Abs((ended - timestamp).TotalSeconds);
        if (differenceSeconds > settings.PauseEndRecoveryToleranceSeconds)
        {
            // A live stop or another terminal's action ended the sheet - the
            // gap is too large for this to be our interrupted transaction.
            // Log the difference so a misconfigured tolerance is visible.
            logger.LogWarning(
                "Offline {What} at {Timestamp}: latest stop {Ended} is {DifferenceSeconds:N0}s away (tolerance {Tolerance}s) - not resuming work",
                what, timestamp, ended, differenceSeconds, settings.PauseEndRecoveryToleranceSeconds);
            return null;
        }

        logger.LogWarning(
            "Offline {What} at {Timestamp}: matching interrupted stop {Ended} (difference {DifferenceSeconds:N0}s)",
            what, timestamp, ended, differenceSeconds);
        return recent;
    }

    private const string ObsoleteEventMessage =
        "Veraltet - das aktive Timesheet wurde erst nach diesem Ereignis gestartet.";

    /// <summary>Vermerk für ein nachgetragenes Einstempeln, dessen Tätigkeit gelöscht war.</summary>
    private const string DeletedTaskNote =
        "offline gewählte Tätigkeit war beim Nachtrag gelöscht - auf die Haupttätigkeit gebucht";

    /// <summary>
    /// Tolerance for <see cref="IsSheetNewerThanEvent"/>; same clock-skew
    /// rationale as <see cref="RuntimeSettings.PauseEndRecoveryToleranceSeconds"/>.
    /// </summary>
    private const int StaleEventToleranceSeconds = 30;

    /// <summary>
    /// True when the ACTIVE timesheet began AFTER this event's timestamp.
    /// Such a sheet can never be the event's target: while this event waited
    /// in an outbox, the employee must have stamped live again (the live
    /// endpoints do not share <see cref="_syncLock"/>).
    /// </summary>
    private bool IsSheetNewerThanEvent(string? startedAt, DateTimeOffset timestamp)
    {
        if (!DateTimeOffset.TryParse(startedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var started))
        {
            // Missing or unparseable begin: keep today's behavior instead of
            // dropping legitimate stops over a parsing hiccup.
            return false;
        }

        return (started - timestamp).TotalSeconds > StaleEventToleranceSeconds;
    }

    /// <summary>
    /// Applies anything waiting in the outbox. Called opportunistically after
    /// each sync and periodically by <see cref="OfflineOutboxBackgroundService"/>,
    /// so events buffered during a Kimai outage are retried without a new
    /// request from the client. Every event is re-registered with the event-ID
    /// store before applying, so an event that was already applied by a
    /// client re-send in the meantime is skipped instead of double-applied.
    /// </summary>
    public async Task FlushOutboxAsync(CancellationToken cancellationToken = default)
    {
        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            await FlushOutboxCoreAsync(cancellationToken);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Drains the outbox in event-time order. The list is stable-sorted at
    /// flush start (a later batch may carry events older than the backlog
    /// tail, e.g. from a second kiosk); a transient failure puts the head back
    /// at the front and ends the round, so the next flush resumes in the same
    /// order. Callers must hold <see cref="_syncLock"/>.
    /// </summary>
    private async Task FlushOutboxCoreAsync(CancellationToken cancellationToken)
    {
        var drained = 0;
        SortChronologically(_kioskOutbox, entry => entry.PerformedAt);

        while (_kioskOutbox.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var kioskEntry = _kioskOutbox[0];
            _kioskOutbox.RemoveAt(0);

            if (!eventIdStore.TryRegister(kioskEntry.EventId))
            {
                // Already applied via the normal sync path (client re-sent the
                // batch) - nothing left to do.
                _kioskOutboxIds.Remove(kioskEntry.EventId);
                drained++;
                continue;
            }

            try
            {
                var (message, _) = await ApplyKioskEventAsync(kioskEntry, cancellationToken);
                logger.LogInformation("Outbox: offline kiosk event {EventId} applied ({Message})", kioskEntry.EventId, message);
                _kioskOutboxIds.Remove(kioskEntry.EventId);
                drained++;
            }
            catch (KimaiApiException ex) when (IsRetryable(ex))
            {
                // Still down - free the event ID for a later retry and put it
                // back at the front, then stop flushing this round.
                eventIdStore.Remove(kioskEntry.EventId);
                _kioskOutbox.Insert(0, kioskEntry);
                break;
            }
            catch (Exception ex) when (IsTransientNetworkError(ex))
            {
                // Kimai unreachable at the network level - put back and retry later.
                eventIdStore.Remove(kioskEntry.EventId);
                _kioskOutbox.Insert(0, kioskEntry);
                break;
            }
            catch (Exception ex)
            {
                // Permanent failure: log and drop so one bad event cannot block the outbox.
                logger.LogError(ex, "Outbox: dropping kiosk event {EventId} after permanent error", kioskEntry.EventId);
                _kioskOutboxIds.Remove(kioskEntry.EventId);
                drained++;
            }
        }

        if (drained > 0)
        {
            logger.LogInformation("Outbox flushed {Count} offline event(s)", drained);
        }
    }

    /// <summary>
    /// Stable in-place chronological sort. Must be called while holding
    /// <see cref="_syncLock"/>; stability preserves the scan order of events
    /// sharing a timestamp.
    /// </summary>
    private static void SortChronologically<T>(List<T> list, Func<T, DateTimeOffset> timestamp)
    {
        if (list.Count > 1)
        {
            var sorted = list.OrderBy(timestamp).ToList();
            list.Clear();
            list.AddRange(sorted);
        }
    }

    private static bool IsRetryable(KimaiApiException exception)
    {
        var statusCode = (int)exception.StatusCode;
        return statusCode >= 500 || statusCode == 408 || statusCode == 429;
    }

    /// <summary>
    /// True for network-level failures while talking to Kimai (host down,
    /// DNS failure, connection reset, timeout). These are transient - the
    /// event must be buffered, never rejected. HttpRequestException and
    /// TaskCanceledException are NOT KimaiApiExceptions, so they would
    /// otherwise fall into the "permanent" catch-all.
    /// </summary>
    private static bool IsTransientNetworkError(Exception exception)
    {
        return exception is HttpRequestException
            or System.Net.Sockets.SocketException
            or TaskCanceledException
            or TimeoutException;
    }
}
