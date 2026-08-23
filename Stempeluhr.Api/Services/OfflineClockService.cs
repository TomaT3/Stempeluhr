using System.Collections.Concurrent;
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
    private readonly ConcurrentQueue<OfflineNfcClockEventDto> _outbox = new();
    private readonly ConcurrentQueue<OfflineKioskClockEventDto> _kioskOutbox = new();
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public async Task<OfflineSyncResultDto> SyncAsync(IReadOnlyList<OfflineNfcClockEventDto> events, CancellationToken cancellationToken = default)
    {
        var accepted = 0;
        var duplicates = 0;
        var buffered = 0;
        var results = new List<OfflineSyncEventResultDto>();

        foreach (var group in events
                     .Where(e => !string.IsNullOrWhiteSpace(e.EventId) && !string.IsNullOrWhiteSpace(e.CardId))
                     .GroupBy(e => NfcCardIdNormalizer.Normalize(e.CardId) ?? e.CardId.Trim())
                     .OrderBy(g => g.Min(e => e.ScannedAt)))
        {
            var timeline = group.OrderBy(e => e.ScannedAt).ToList();

            await _syncLock.WaitAsync(cancellationToken);
            try
            {
                foreach (var entry in timeline)
                {
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
                        _outbox.Enqueue(entry);
                        buffered++;
                        results.Add(new OfflineSyncEventResultDto(entry.EventId, "buffered",
                            "Kimai nicht erreichbar - wird automatisch nachgetragen."));
                    }
                    catch (Exception ex) when (IsTransientNetworkError(ex))
                    {
                        // Kimai unreachable at the network level (connection refused,
                        // DNS failure, timeout): keep for retry - exactly the outage
                        // case the outbox exists for. HttpRequestException is NOT a
                        // KimaiApiException, so it must be caught explicitly.
                        eventIdStore.Remove(entry.EventId);
                        _outbox.Enqueue(entry);
                        buffered++;
                        results.Add(new OfflineSyncEventResultDto(entry.EventId, "buffered",
                            "Kimai nicht erreichbar - wird automatisch nachgetragen."));
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
            finally
            {
                _syncLock.Release();
            }
        }

        // Opportunistically flush anything waiting in the outbox once connectivity returns.
        await FlushOutboxAsync(cancellationToken);

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

        foreach (var entry in events
                     .Where(e => !string.IsNullOrWhiteSpace(e.EventId) && !string.IsNullOrWhiteSpace(e.EmployeeId))
                     .OrderBy(e => e.PerformedAt))
        {
            await _syncLock.WaitAsync(cancellationToken);
            try
            {
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
                    _kioskOutbox.Enqueue(entry);
                    buffered++;
                    results.Add(new OfflineSyncEventResultDto(entry.EventId, "buffered",
                        "Kimai nicht erreichbar - wird automatisch nachgetragen."));
                }
                catch (Exception ex) when (IsTransientNetworkError(ex))
                {
                    // Kimai unreachable at the network level - keep for retry.
                    eventIdStore.Remove(entry.EventId);
                    _kioskOutbox.Enqueue(entry);
                    buffered++;
                    results.Add(new OfflineSyncEventResultDto(entry.EventId, "buffered",
                        "Kimai nicht erreichbar - wird automatisch nachgetragen."));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Offline kiosk event {EventId} permanently rejected", entry.EventId);
                    results.Add(new OfflineSyncEventResultDto(entry.EventId, "rejected", ex.Message));
                }
            }
            finally
            {
                _syncLock.Release();
            }
        }

        await FlushOutboxAsync(cancellationToken);

        return new OfflineSyncResultDto(accepted, duplicates, buffered, results);
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
                if (status.State != "paused" || status.ActiveTimesheetId is not int endPauseId)
                {
                    return ("Keine laufende Pause - Nachtrag nicht moeglich.", status.State);
                }

                var resumeProject = employee.ProjectId
                    ?? settings.DefaultProjectId
                    ?? throw new InvalidOperationException("Projekt muss konfiguriert sein.");
                var resumeActivity = employee.ActivityId
                    ?? settings.DefaultActivityId
                    ?? throw new InvalidOperationException("Aktivitaet muss konfiguriert sein.");

                await kimai.StopAtAsync(settings, employee, endPauseId, timestamp, cancellationToken);
                await kimai.StartAtAsync(settings, employee, resumeProject, resumeActivity, timestamp, cancellationToken);
                return ($"Nachgetragen: Pausenende {timestamp.ToLocalTime():HH:mm}", "working");

            default:
                throw new InvalidOperationException($"Unbekannte Aktion: {action}");
        }
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
            var drained = 0;

            // NFC outbox: replay via card lookup + toggle.
            while (_outbox.TryDequeue(out var nfcEntry))
            {
                if (!eventIdStore.TryRegister(nfcEntry.EventId))
                {
                    // Already applied via the normal sync path (client re-sent
                    // the batch) - nothing left to do.
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
                    drained++;
                }
                catch (KimaiApiException ex) when (IsRetryable(ex))
                {
                    // Still down - free the event ID for a later retry and put
                    // it back, then stop flushing this round.
                    eventIdStore.Remove(nfcEntry.EventId);
                    _outbox.Enqueue(nfcEntry);
                    break;
                }
                catch (Exception ex) when (IsTransientNetworkError(ex))
                {
                    // Kimai unreachable at the network level - put back and retry later.
                    eventIdStore.Remove(nfcEntry.EventId);
                    _outbox.Enqueue(nfcEntry);
                    break;
                }
                catch (Exception ex)
                {
                    // Permanent failure: log and drop so one bad event cannot block the outbox.
                    logger.LogError(ex, "Outbox: dropping NFC event {EventId} after permanent error", nfcEntry.EventId);
                    drained++;
                }
            }

            // Kiosk outbox: replay with explicit action.
            while (_kioskOutbox.TryDequeue(out var kioskEntry))
            {
                if (!eventIdStore.TryRegister(kioskEntry.EventId))
                {
                    drained++;
                    continue;
                }

                try
                {
                    var (message, _) = await ApplyKioskEventAsync(kioskEntry, cancellationToken);
                    logger.LogInformation("Outbox: offline kiosk event {EventId} applied ({Message})", kioskEntry.EventId, message);
                    drained++;
                }
                catch (KimaiApiException ex) when (IsRetryable(ex))
                {
                    eventIdStore.Remove(kioskEntry.EventId);
                    _kioskOutbox.Enqueue(kioskEntry);
                    break;
                }
                catch (Exception ex) when (IsTransientNetworkError(ex))
                {
                    // Kimai unreachable at the network level - put back and retry later.
                    eventIdStore.Remove(kioskEntry.EventId);
                    _kioskOutbox.Enqueue(kioskEntry);
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Outbox: dropping kiosk event {EventId} after permanent error", kioskEntry.EventId);
                    drained++;
                }
            }

            if (drained > 0)
            {
                logger.LogInformation("Outbox flushed {Count} offline event(s)", drained);
            }
        }
        finally
        {
            _syncLock.Release();
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
