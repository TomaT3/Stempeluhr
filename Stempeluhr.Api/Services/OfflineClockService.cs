using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

/// <summary>
/// Applies queued offline NFC scans to Kimai in scan order.
///
/// Design notes:
/// - Idempotency: each event ID is remembered (persisted JSON store). Replayed
///   batches after a network hiccup are acknowledged as duplicates.
/// - Toggle logic: the effective state is derived from the replayed timeline
///   itself. For each card we walk its queued events in scan order and apply
///   start/stop against Kimai using the stored timestamps (backdating).
/// - Kimai outage: if Kimai is unreachable while syncing, remaining events are
///   kept in an internal outbox. The service is registered as a singleton and
///   a background service flushes the outbox periodically, so a sync call
///   during a Kimai outage never loses events and they are retried without a
///   new request from the client.
/// - Global ordering: both outboxes (NFC + kiosk) are drained as ONE timeline
///   in event-time order. Draining them sequentially (all NFC first, then all
///   kiosk) would invert the toggle derivation when one employee mixes
///   terminals during an outage.
/// - Concurrency: every read/mutation of the outbox lists happens while
///   holding <see cref="_syncLock"/>, so sync requests serialize against each
///   other and against the periodic background flush.
/// - Permanent errors (unknown card, wrong PIN, missing config) are reported
///   per event as "rejected" instead of failing the whole batch.
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

    // Plain lists instead of queues: every mutation happens while holding
    // _syncLock, and an entry whose replay failed transiently must go back to
    // the FRONT (Insert at index 0). A queue would move it to the tail and the
    // next flush would start with a LATER scan of the same card, inverting the
    // toggle order.
    private readonly List<OfflineNfcClockEventDto> _outbox = new();
    private readonly List<OfflineKioskClockEventDto> _kioskOutbox = new();

    // Physical dedup mirroring the contents of each list: the client re-sends
    // buffered events on a slow safety-net timer (offline-queue.ts) while the
    // event IDs are already FREED (buffering removes them from the store), so
    // without this set every re-send would enqueue yet another COPY of the
    // same event - one per retry interval across the whole outage. Guarded by
    // _syncLock like the lists themselves.
    private readonly HashSet<string> _outboxIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _kioskOutboxIds = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public async Task<OfflineSyncResultDto> SyncAsync(IReadOnlyList<OfflineNfcClockEventDto> events, CancellationToken cancellationToken = default)
    {
        var accepted = 0;
        var duplicates = 0;
        var buffered = 0;
        var results = new List<OfflineSyncEventResultDto>();

        // Malformed entries must be reported back explicitly so the sender can
        // drop them from its queue instead of silently retrying them forever.
        foreach (var invalid in events.Where(e => string.IsNullOrWhiteSpace(e.EventId) || string.IsNullOrWhiteSpace(e.CardId)))
        {
            results.Add(new OfflineSyncEventResultDto(invalid.EventId ?? string.Empty, "rejected",
                "EventId und CardId sind erforderlich."));
        }

        void BufferTimelineFrom(IReadOnlyList<OfflineNfcClockEventDto> cardTimeline, int failedIndex)
        {
            // The failed event itself plus everything AFTER it must be replayed
            // together: applying later scans against a Kimai state that misses
            // the buffered ones would turn them into "Lief nicht" no-ops that
            // get acknowledged as applied - silently losing those stamps.
            for (var i = failedIndex; i < cardTimeline.Count; i++)
            {
                var pending = cardTimeline[i];
                AddToOutbox(_outbox, _outboxIds, pending, pending.EventId);
                buffered++;
                results.Add(new OfflineSyncEventResultDto(pending.EventId, BufferedStatus, BufferedMessage));
            }
        }

        var orderedGroups = events
            .Where(e => !string.IsNullOrWhiteSpace(e.EventId) && !string.IsNullOrWhiteSpace(e.CardId))
            .GroupBy(e => NfcCardIdNormalizer.Normalize(e.CardId) ?? e.CardId.Trim())
            .OrderBy(g => g.Min(e => e.ScannedAt))
            .ToList();

        // Everything from here on reads/mutates the outboxes or applies stamps,
        // so hold _syncLock for the whole operation: parallel sync requests and
        // the background flush must not interleave (List<T> is not thread-safe,
        // and the global ordering guarantee depends on serialization).
        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            // Global replay order across batches: never let a fresh batch jump
            // over events still waiting in the outbox - see
            // <see cref="BufferBatchBehindBacklogAsync{T}"/>.
            var queuedBehindBacklog = await BufferBatchBehindBacklogAsync(
                // Global scan order across ALL cards (not group-by-group): the
                // outbox lists must stay individually chronological for the
                // head-comparison merge in FlushOutboxCoreAsync to be exact.
                orderedGroups
                    .SelectMany(g => g.OrderBy(e => e.ScannedAt))
                    .OrderBy(e => e.ScannedAt)
                    .ToList(),
                _outbox,
                _outboxIds,
                entry => entry.EventId,
                entry => new OfflineSyncEventResultDto(entry.EventId, BufferedStatus, BufferedMessage),
                results,
                cancellationToken);
            if (queuedBehindBacklog > 0)
            {
                buffered += queuedBehindBacklog;
                return new OfflineSyncResultDto(accepted, duplicates, buffered, results);
            }

            foreach (var group in orderedGroups)
            {
                var timeline = group.OrderBy(e => e.ScannedAt).ToList();

                for (var i = 0; i < timeline.Count; i++)
                {
                    var entry = timeline[i];
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!eventIdStore.TryRegister(entry.EventId))
                    {
                        duplicates++;
                        results.Add(new OfflineSyncEventResultDto(entry.EventId, "duplicate", null));
                        continue;
                    }

                    try
                    {
                        var (message, state) = await ApplyScanAsync(group.Key, entry.ScannedAt, cancellationToken);
                        accepted++;
                        results.Add(new OfflineSyncEventResultDto(entry.EventId, "applied", message, state));
                    }
                    catch (KimaiApiException ex) when (IsRetryable(ex))
                    {
                        // Kimai temporarily unavailable: keep for retry, do not mark applied.
                        eventIdStore.Remove(entry.EventId);
                        BufferTimelineFrom(timeline, i);
                        break;
                    }
                    catch (Exception ex) when (IsTransientNetworkError(ex))
                    {
                        // Kimai unreachable at the network level (connection refused,
                        // DNS failure, timeout): keep for retry - exactly the outage
                        // case the outbox exists for. HttpRequestException is NOT a
                        // KimaiApiException, so it must be caught explicitly.
                        eventIdStore.Remove(entry.EventId);
                        BufferTimelineFrom(timeline, i);
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Permanent failure (unknown card, missing config, ...): report
                        // per event instead of failing the whole batch. The event ID
                        // stays registered, so a re-send is a duplicate and the client
                        // drops the event.
                        logger.LogWarning(ex, "Offline NFC event {EventId} permanently rejected", entry.EventId);
                        results.Add(new OfflineSyncEventResultDto(entry.EventId, "rejected", ex.Message));
                    }
                }
            }

            // Opportunistically flush anything waiting in the outbox once connectivity returns.
            await FlushOutboxCoreAsync(cancellationToken);
        }
        finally
        {
            _syncLock.Release();
        }

        return new OfflineSyncResultDto(accepted, duplicates, buffered, results);
    }

    /// <summary>
    /// Replays one scan: derives the target action from Kimai's current state,
    /// then start/stop with backdating so the recorded time matches the real
    /// scan time. Returns the human-readable result and the resulting clock
    /// state (for the client's local status cache).
    /// </summary>
    private async Task<(string Message, string State)> ApplyScanAsync(string normalizedCardId, DateTimeOffset scannedAt, CancellationToken cancellationToken)
    {
        var settings = settingsStore.Load();
        var employee = employees.FindEmployeeByNfcCardId(settings, normalizedCardId);
        if (employee is null)
        {
            throw new InvalidOperationException("NFC-Karte ist keinem Mitarbeiter zugeordnet.");
        }

        return await ApplyActionAsync(settings, employee, "toggle", scannedAt, cancellationToken);
    }

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

        // Same locking rule as the NFC path: serialize the whole operation
        // against parallel syncs and the background flush.
        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            // Same cross-batch rule as the NFC path: never let a fresh batch jump
            // over events that are still waiting in the outbox.
            var queuedBehindBacklog = await BufferBatchBehindBacklogAsync(
                orderedKioskEvents,
                _kioskOutbox,
                _kioskOutboxIds,
                entry => entry.EventId,
                entry => new OfflineSyncEventResultDto(entry.EventId, BufferedStatus, BufferedMessage),
                results,
                cancellationToken);
            if (queuedBehindBacklog > 0)
            {
                buffered += queuedBehindBacklog;
                return new OfflineSyncResultDto(accepted, duplicates, buffered, results);
            }

            void BufferKioskFrom(IReadOnlyList<OfflineKioskClockEventDto> pendingEvents, int failedIndex)
            {
                // Same rule as the NFC path: the failed event and everything after
                // it must replay together. Applying later actions against a Kimai
                // state that misses the buffered ones turns them into "Lief nicht"
                // no-ops that get acknowledged as applied - silently losing them.
                for (var i = failedIndex; i < pendingEvents.Count; i++)
                {
                    var pending = pendingEvents[i];
                    AddToOutbox(_kioskOutbox, _kioskOutboxIds, pending, pending.EventId);
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
    /// Shared cross-batch rule of both sync paths: when the outbox still holds
    /// events (e.g. the client lost its own queue - the exact case this
    /// safety-net exists for), a newly arriving batch must not race ahead of
    /// it. Otherwise later scans would be applied against a Kimai state that
    /// misses earlier ones, turning them into "Lief nicht" no-ops acknowledged
    /// as applied. Drains what it can; if a backlog survives (Kimai still
    /// unreachable), the incoming batch is appended behind it in scan order
    /// instead of being applied live, followed by one opportunistic flush.
    /// MUST be called while holding <see cref="_syncLock"/> - the count check
    /// and the appends are atomic against parallel sync requests and the
    /// background flush this way. Returns the number of buffered events
    /// (0 means no backlog existed and the caller processes live).
    /// </summary>
    private async Task<int> BufferBatchBehindBacklogAsync<T>(
        IReadOnlyList<T> orderedEvents,
        List<T> outbox,
        HashSet<string> outboxIds,
        Func<T, string> eventIdSelector,
        Func<T, OfflineSyncEventResultDto> createBufferedResult,
        List<OfflineSyncEventResultDto> results,
        CancellationToken cancellationToken)
    {
        await FlushOutboxCoreAsync(cancellationToken);
        if (_outbox.Count == 0 && _kioskOutbox.Count == 0)
        {
            return 0;
        }

        foreach (var entry in orderedEvents)
        {
            // Physical dedup: a re-sent event that is already waiting in the
            // outbox must not add another copy (see the _outboxIds comment).
            // The sender still gets "buffered" - the server DOES hold it and
            // will apply it on recovery.
            AddToOutbox(outbox, outboxIds, entry, eventIdSelector(entry));
            results.Add(createBufferedResult(entry));
        }

        await FlushOutboxCoreAsync(cancellationToken);
        return orderedEvents.Count;
    }

    /// <summary>
    /// Appends an entry to an outbox unless its event ID is already queued.
    /// Callers must hold <see cref="_syncLock"/>.
    /// </summary>
    private static void AddToOutbox<T>(List<T> outbox, HashSet<string> outboxIds, T entry, string eventId)
    {
        if (outboxIds.Add(eventId))
        {
            outbox.Add(entry);
        }
    }

    private async Task<(string Message, string State)> ApplyKioskEventAsync(OfflineKioskClockEventDto entry, CancellationToken cancellationToken)
    {
        var settings = settingsStore.Load();
        var employee = employees.FindEmployee(settings, new ClockRequest(entry.EmployeeId, entry.Pin));
        if (employee is null)
        {
            throw new InvalidOperationException("Mitarbeiter nicht gefunden oder PIN falsch.");
        }

        var action = NormalizeKioskAction(entry.Action);
        return await ApplyActionAsync(settings, employee, action, entry.PerformedAt, cancellationToken);
    }

    private static string NormalizeKioskAction(string? action)
    {
        return string.Equals(action, "start", StringComparison.OrdinalIgnoreCase) ? "start"
            : string.Equals(action, "stop", StringComparison.OrdinalIgnoreCase) ? "stop"
            : string.Equals(action, "pauseStart", StringComparison.OrdinalIgnoreCase) ? "pauseStart"
            : string.Equals(action, "pauseEnd", StringComparison.OrdinalIgnoreCase) ? "pauseEnd"
            : throw new InvalidOperationException($"Unbekannte Aktion: {action}");
    }

    /// <summary>
    /// Applies one clock action at a historical point in time. For NFC scans
    /// ("toggle") the action is derived from Kimai's current state. Returns the
    /// human-readable result plus the resulting clock state
    /// ("working"/"paused"/"clockedOut").
    /// </summary>
    private async Task<(string Message, string State)> ApplyActionAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        string action,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var status = await kimai.GetStatusAsync(settings, employee, cancellationToken);

        if (action == "toggle")
        {
            action = status.IsRunning ? "stop" : "start";
        }

        switch (action)
        {
            case "start":
                if (status.IsRunning)
                {
                    return ("Lief bereits - kein Nachtrag noetig.", status.State);
                }

                var projectId = employee.ProjectId
                    ?? settings.DefaultProjectId
                    ?? throw new InvalidOperationException("Projekt muss konfiguriert sein.");
                var activityId = employee.ActivityId
                    ?? settings.DefaultActivityId
                    ?? throw new InvalidOperationException("Aktivitaet muss konfiguriert sein.");

                await kimai.StartAtAsync(settings, employee, projectId, activityId, timestamp, cancellationToken);
                return ($"Nachgetragen: Einstempeln {timestamp.ToLocalTime():HH:mm}", "working");

            case "stop":
                if (!status.IsRunning || status.ActiveTimesheetId is not int stopId)
                {
                    return ("Lief nicht - kein Nachtrag noetig.", status.State);
                }

                await kimai.StopAtAsync(settings, employee, stopId, timestamp, cancellationToken);
                return ($"Nachgetragen: Ausstempeln {timestamp.ToLocalTime():HH:mm}", "clockedOut");

            case "pauseStart":
                if (!status.IsRunning || status.ActiveTimesheetId is not int pauseStopId)
                {
                    return ("Lief nicht - Pause nicht nachtragbar.", status.State);
                }

                if (settings.PauseActivityId is null)
                {
                    throw new InvalidOperationException("Pausen-Aktivitaet muss konfiguriert sein.");
                }

                // Two-step transaction: end work, then open the pause from that
                // moment on. If the second call fails retryably, the employee
                // is left stopped and a later replay sees IsRunning == false
                // ("Lief nicht") - the pause is then lost rather than applied
                // twice. Accepted trade-off: Kimai has no transactional
                // stop+start; the alternative (compensating restart of the work
                // timesheet) would risk double-applying on ambiguous failures.
                await kimai.StopAtAsync(settings, employee, pauseStopId, timestamp, cancellationToken);
                var pauseProject = employee.ProjectId
                    ?? settings.DefaultProjectId
                    ?? throw new InvalidOperationException("Projekt muss konfiguriert sein.");
                await kimai.StartAtAsync(
                    settings, employee, pauseProject, settings.PauseActivityId.Value, timestamp, cancellationToken);
                return ($"Nachgetragen: Pausenbeginn {timestamp.ToLocalTime():HH:mm}", "paused");

            case "pauseEnd":
                // Mirror the live path: only end a pause that is actually running.
                if (status.State == "paused" && status.ActiveTimesheetId is int endPauseId)
                {
                    var resumeProject = employee.ProjectId
                        ?? settings.DefaultProjectId
                        ?? throw new InvalidOperationException("Projekt muss konfiguriert sein.");
                    var resumeActivity = employee.ActivityId
                        ?? settings.DefaultActivityId
                        ?? throw new InvalidOperationException("Aktivitaet muss konfiguriert sein.");

                    await kimai.StopAtAsync(settings, employee, endPauseId, timestamp, cancellationToken);
                    await kimai.StartAtAsync(settings, employee, resumeProject, resumeActivity, timestamp, cancellationToken);
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
                    if (!await IsInterruptedPauseEndAsync(settings, employee, timestamp, cancellationToken))
                    {
                        logger.LogWarning(
                            "Offline pauseEnd at {Timestamp}: no pause running and no matching interrupted pause stop - not resuming work",
                            timestamp);
                        return ("Keine laufende Pause - Nachtrag nicht moeglich.", status.State);
                    }

                    logger.LogWarning(
                        "Offline pauseEnd at {Timestamp}: completing an interrupted pause end by resuming work",
                        timestamp);

                    var restartProject = employee.ProjectId
                        ?? settings.DefaultProjectId
                        ?? throw new InvalidOperationException("Projekt muss konfiguriert sein.");
                    var restartActivity = employee.ActivityId
                        ?? settings.DefaultActivityId
                        ?? throw new InvalidOperationException("Aktivitaet muss konfiguriert sein.");

                    await kimai.StartAtAsync(settings, employee, restartProject, restartActivity, timestamp, cancellationToken);
                    return ($"Nachgetragen: Pausenende {timestamp.ToLocalTime():HH:mm}", "working");
                }

                return ("Keine laufende Pause - Nachtrag nicht moeglich.", status.State);

            default:
                throw new InvalidOperationException($"Unbekannte Aktion: {action}");
        }
    }

    /// <summary>
    /// True when the Kimai state matches an interrupted pauseEnd transaction:
    /// the latest stopped timesheet is a PAUSE timesheet that ended at this
    /// event's timestamp - exactly what StopAt(pause, timestamp) writes in the
    /// successful first step before the resume-start fails transiently. The
    /// small tolerance only absorbs timestamp rounding; anything further away
    /// (a later live stop, another terminal's action) must NOT trigger a
    /// phantom resume. A transient failure of the lookup itself propagates to
    /// the caller, so the event buffers and retries as usual.
    /// </summary>
    private async Task<bool> IsInterruptedPauseEndAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        if (settings.PauseActivityId is null)
        {
            // Without a configured pause activity a stopped timesheet cannot
            // be identified as a pause - stay conservative (no-op + loud log).
            return false;
        }

        var latest = await kimai.GetLatestStoppedTimesheetAsync(settings, employee, cancellationToken);
        if (latest is not { ActivityId: int activityId, EndedAt: DateTimeOffset ended }
            || activityId != settings.PauseActivityId)
        {
            return false;
        }

        var differenceSeconds = Math.Abs((ended - timestamp).TotalSeconds);
        if (differenceSeconds > settings.PauseEndRecoveryToleranceSeconds)
        {
            // A live stop or another terminal's action ended the pause - the
            // gap is too large for this to be our interrupted transaction.
            // Log the difference so a misconfigured tolerance is visible.
            logger.LogWarning(
                "Offline pauseEnd at {Timestamp}: latest pause stop {Ended} is {DifferenceSeconds:N0}s away (tolerance {Tolerance}s) - not resuming work",
                timestamp, ended, differenceSeconds, settings.PauseEndRecoveryToleranceSeconds);
            return false;
        }

        logger.LogWarning(
            "Offline pauseEnd at {Timestamp}: matching interrupted pause stop {Ended} (difference {DifferenceSeconds:N0}s) - resuming work",
            timestamp, ended, differenceSeconds);
        return true;
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
    /// Drains BOTH outboxes as ONE timeline in event-time order. Replaying
    /// them sequentially (whole NFC outbox first, then kiosk) would invert the
    /// toggle derivation whenever one employee's events sit in both queues:
    /// a kiosk start@08:00 waiting in the kiosk outbox while an NFC
    /// toggle@17:00 replays first runs the toggle against the not-yet-started
    /// state, derives "start" at 17:00, and the real stamp is lost as a
    /// "Lief bereits" no-op. Each list is kept individually chronological
    /// (stable-sorted at flush start; transiently failed entries go back to
    /// the FRONT), so comparing heads merges both lists like merge-sort. A transient
    /// failure puts the head back at the front of ITS list and ends the round -
    /// the failed entry stays the globally oldest event, so the next flush
    /// resumes in the same order. Callers must hold <see cref="_syncLock"/>.
    /// </summary>
    private async Task FlushOutboxCoreAsync(CancellationToken cancellationToken)
    {
        var drained = 0;

        // Stabilize both lists chronologically before draining. Appends are
        // chronological in the common cases, but transient failures buffer
        // card-group TAILS sequentially - e.g. card A's tail [08:00, 12:00]
        // lands before card B's tail [10:00]. The head-comparison below
        // assumes chronological lists, so enforce that invariant here
        // regardless of which path appended (OrderBy is a STABLE sort, so
        // equal timestamps keep their scan order).
        SortChronologically(_outbox, entry => entry.ScannedAt);
        SortChronologically(_kioskOutbox, entry => entry.PerformedAt);

        while (_outbox.Count > 0 || _kioskOutbox.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var takeNfc = _outbox.Count > 0
                && (_kioskOutbox.Count == 0 || _outbox[0].ScannedAt <= _kioskOutbox[0].PerformedAt);

            if (takeNfc)
            {
                var nfcEntry = _outbox[0];
                _outbox.RemoveAt(0);

                if (!eventIdStore.TryRegister(nfcEntry.EventId))
                {
                    // Already applied via the normal sync path (client re-sent
                    // the batch) - nothing left to do.
                    _outboxIds.Remove(nfcEntry.EventId);
                    drained++;
                    continue;
                }

                try
                {
                    var (message, _) = await ApplyScanAsync(
                        NfcCardIdNormalizer.Normalize(nfcEntry.CardId) ?? nfcEntry.CardId.Trim(),
                        nfcEntry.ScannedAt,
                        cancellationToken);

                    logger.LogInformation("Outbox: offline event {EventId} applied ({Message})", nfcEntry.EventId, message);
                    _outboxIds.Remove(nfcEntry.EventId);
                    drained++;
                }
                catch (KimaiApiException ex) when (IsRetryable(ex))
                {
                    // Still down - free the event ID for a later retry and put
                    // it back at the front, then stop flushing this round.
                    eventIdStore.Remove(nfcEntry.EventId);
                    _outbox.Insert(0, nfcEntry);
                    break;
                }
                catch (Exception ex) when (IsTransientNetworkError(ex))
                {
                    // Kimai unreachable at the network level - put back and retry later.
                    eventIdStore.Remove(nfcEntry.EventId);
                    _outbox.Insert(0, nfcEntry);
                    break;
                }
                catch (Exception ex)
                {
                    // Permanent failure: log and drop so one bad event cannot block the outbox.
                    logger.LogError(ex, "Outbox: dropping NFC event {EventId} after permanent error", nfcEntry.EventId);
                    _outboxIds.Remove(nfcEntry.EventId);
                    drained++;
                }
            }
            else
            {
                // Kiosk outbox: replay with explicit action. Same front-reinsert
                // rule as the NFC branch above.
                var kioskEntry = _kioskOutbox[0];
                _kioskOutbox.RemoveAt(0);

                if (!eventIdStore.TryRegister(kioskEntry.EventId))
                {
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
                    logger.LogError(ex, "Outbox: dropping kiosk event {EventId} after permanent error", kioskEntry.EventId);
                    _kioskOutboxIds.Remove(kioskEntry.EventId);
                    drained++;
                }
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
