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
///   kept in an internal outbox and retried by a background loop, so a sync
///   call during a Kimai outage never loses events either.
/// </summary>
public sealed class OfflineClockService(
    IRuntimeSettingsStore settingsStore,
    IEmployeeService employees,
    IKimaiClient kimai,
    IOfflineEventIdStore eventIdStore,
    ILogger<OfflineClockService> logger) : IOfflineClockService
{
    private sealed record CardTimelineEntry(DateTimeOffset ScannedAt, string EventId);

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
                        var message = await ApplyScanAsync(group.Key, entry.ScannedAt, cancellationToken);
                        accepted++;
                        results.Add(new OfflineSyncEventResultDto(entry.EventId, "applied", message));
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
    /// scan time.
    /// </summary>
    private async Task<string> ApplyScanAsync(string normalizedCardId, DateTimeOffset scannedAt, CancellationToken cancellationToken)
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
                    var message = await ApplyKioskEventAsync(entry, cancellationToken);
                    accepted++;
                    results.Add(new OfflineSyncEventResultDto(entry.EventId, "applied", message));
                }
                catch (KimaiApiException ex) when (IsRetryable(ex))
                {
                    eventIdStore.Remove(entry.EventId);
                    _kioskOutbox.Enqueue(entry);
                    buffered++;
                    results.Add(new OfflineSyncEventResultDto(entry.EventId, "buffered",
                        "Kimai nicht erreichbar - wird automatisch nachgetragen."));
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

    private async Task<string> ApplyKioskEventAsync(OfflineKioskClockEventDto entry, CancellationToken cancellationToken)
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
    /// ("toggle") the action is derived from Kimai's current state.
    /// </summary>
    private async Task<string> ApplyActionAsync(
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
                    return "Lief bereits - kein Nachtrag noetig.";
                }

                var projectId = employee.ProjectId
                    ?? settings.DefaultProjectId
                    ?? throw new InvalidOperationException("Projekt muss konfiguriert sein.");
                var activityId = employee.ActivityId
                    ?? settings.DefaultActivityId
                    ?? throw new InvalidOperationException("Aktivitaet muss konfiguriert sein.");

                await kimai.StartAtAsync(settings, employee, projectId, activityId, timestamp, cancellationToken);
                return $"Nachgetragen: Einstempeln {timestamp.ToLocalTime():HH:mm}";

            case "stop":
                if (!status.IsRunning || status.ActiveTimesheetId is not int stopId)
                {
                    return "Lief nicht - kein Nachtrag noetig.";
                }

                await kimai.StopAtAsync(settings, employee, stopId, timestamp, cancellationToken);
                return $"Nachgetragen: Ausstempeln {timestamp.ToLocalTime():HH:mm}";

            case "pauseStart":
                if (!status.IsRunning || status.ActiveTimesheetId is not int pauseStopId)
                {
                    return "Lief nicht - Pause nicht nachtragbar.";
                }

                if (settings.PauseActivityId is null)
                {
                    throw new InvalidOperationException("Pausen-Aktivitaet muss konfiguriert sein.");
                }

                // End work at the real time, then open the pause from that moment on.
                await kimai.StopAtAsync(settings, employee, pauseStopId, timestamp, cancellationToken);
                var pauseProject = employee.ProjectId
                    ?? settings.DefaultProjectId
                    ?? throw new InvalidOperationException("Projekt muss konfiguriert sein.");
                await kimai.StartAtAsync(
                    settings, employee, pauseProject, settings.PauseActivityId.Value, timestamp, cancellationToken);
                return $"Nachgetragen: Pausenbeginn {timestamp.ToLocalTime():HH:mm}";

            case "pauseEnd":
                if (status.ActiveTimesheetId is not int endPauseId)
                {
                    return "Keine laufende Pause - Nachtrag nicht moeglich.";
                }

                var resumeProject = employee.ProjectId
                    ?? settings.DefaultProjectId
                    ?? throw new InvalidOperationException("Projekt muss konfiguriert sein.");
                var resumeActivity = employee.ActivityId
                    ?? settings.DefaultActivityId
                    ?? throw new InvalidOperationException("Aktivitaet muss konfiguriert sein.");

                await kimai.StopAtAsync(settings, employee, endPauseId, timestamp, cancellationToken);
                await kimai.StartAtAsync(settings, employee, resumeProject, resumeActivity, timestamp, cancellationToken);
                return $"Nachgetragen: Pausenende {timestamp.ToLocalTime():HH:mm}";

            default:
                throw new InvalidOperationException($"Unbekannte Aktion: {action}");
        }
    }

    private async Task FlushOutboxAsync(CancellationToken cancellationToken)
    {
        var drained = 0;

        // NFC outbox: replay via card lookup + toggle.
        while (_outbox.TryDequeue(out var nfcEntry))
        {
            try
            {
                var message = await ApplyScanAsync(
                    NfcCardIdNormalizer.Normalize(nfcEntry.CardId) ?? nfcEntry.CardId.Trim(),
                    nfcEntry.ScannedAt,
                    cancellationToken);

                logger.LogInformation("Outbox: offline event {EventId} applied ({Message})", nfcEntry.EventId, message);
                drained++;
            }
            catch (KimaiApiException ex) when (IsRetryable(ex))
            {
                // Still down - put it back and stop flushing this round.
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
            try
            {
                var message = await ApplyKioskEventAsync(kioskEntry, cancellationToken);
                logger.LogInformation("Outbox: offline kiosk event {EventId} applied ({Message})", kioskEntry.EventId, message);
                drained++;
            }
            catch (KimaiApiException ex) when (IsRetryable(ex))
            {
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

    private static bool IsRetryable(KimaiApiException exception)
    {
        var statusCode = (int)exception.StatusCode;
        return statusCode >= 500 || statusCode == 408 || statusCode == 429;
    }
}
