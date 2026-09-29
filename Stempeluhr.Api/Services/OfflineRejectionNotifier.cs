using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

/// <summary>
/// Sends journaled refusals in bounded batches. The journal is the durable
/// queue; the periodic outbox worker also checks it after process restarts.
/// </summary>
public sealed class OfflineRejectionNotifier(
    RejectedOfflineEventStore rejectedEvents,
    IRuntimeSettingsStore settingsStore,
    IKimaiClient kimai,
    ITelegramNotifier telegram,
    ILogger<OfflineRejectionNotifier> logger,
    TimeProvider? clock = null)
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan TimeZoneLookupTimeout = TimeSpan.FromSeconds(5);
    private const int DailyLimit = 20;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // Guarded by _sendLock. Sends Telegram confirmed but the journal could not
    // record yet (disk full, read-only): they must neither be sent again nor
    // vanish from the rate limit until MarkTelegramNotified succeeds.
    private readonly List<(string[] EventIds, DateTimeOffset At)> _unpersistedSends = [];

    // Monotonic, so a wall-clock step back cannot stretch the retry pause.
    private long? _failedAtTimestamp;
    private int _kickScheduled;

    /// <summary>Kick delivery after a new journal entry without delaying the caller.</summary>
    public void Report()
    {
        // Coalesce: one queued run covers every entry journaled before it starts.
        if (Interlocked.Exchange(ref _kickScheduled, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            Interlocked.Exchange(ref _kickScheduled, 0);
            await ProcessPendingAsync();
        });
    }

    /// <summary>Also called periodically, so a restart cannot lose held warnings.</summary>
    public async Task ProcessPendingAsync(CancellationToken cancellationToken = default)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            RetryUnpersistedSends();
            if (!rejectedEvents.HasPendingTelegram()) return;
            if (_failedAtTimestamp is { } failedAt && _clock.GetElapsedTime(failedAt) < MinimumInterval) return;

            var settings = settingsStore.Load();
            if (!settings.TelegramEnabled) return;

            var all = rejectedEvents.Snapshot();
            var alreadySent = _unpersistedSends.SelectMany(send => send.EventIds).ToHashSet(StringComparer.Ordinal);
            var pending = all.Where(entry => RejectedOfflineEventStore.AwaitsTelegram(entry)
                    && !alreadySent.Contains(entry.EventId))
                .OrderBy(entry => entry.PerformedAt).ToArray();
            if (pending.Length == 0) return;

            // Send times from the future (clock stepped back after a send)
            // are ignored instead of blocking every warning until then.
            var now = _clock.GetUtcNow();
            var sendTimes = all.Select(entry => entry.TelegramNotifiedAt).OfType<DateTimeOffset>()
                .Concat(_unpersistedSends.Select(send => send.At))
                .Where(at => at <= now)
                .Distinct().ToArray();
            if (sendTimes.Count(at => at.UtcDateTime.Date == now.UtcDateTime.Date) >= DailyLimit) return;
            if (sendTimes.Length > 0 && now - sendTimes.Max() < MinimumInterval) return;

            var first = pending[0];
            var latest = pending[^1];
            var firstZone = await ResolveTimeZoneAsync(settings, first, cancellationToken);
            var lastZone = latest.EmployeeId.Equals(first.EmployeeId, StringComparison.OrdinalIgnoreCase)
                ? firstZone : await ResolveTimeZoneAsync(settings, latest, cancellationToken);
            var text = TelegramMessageFactory.BuildOfflineRejectionSummary(
                first, firstZone, latest, lastZone, pending.Length);

            // A failed send remains pending. A successful send is recorded
            // before the next batch can start, including across restarts.
            if (!await telegram.SendMessageAsync(text))
            {
                _failedAtTimestamp = _clock.GetTimestamp();
                return;
            }

            _failedAtTimestamp = null;
            _unpersistedSends.Add((pending.Select(entry => entry.EventId).ToArray(), _clock.GetUtcNow()));
            RetryUnpersistedSends();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Offline rejection Telegram notification failed");
            _failedAtTimestamp = _clock.GetTimestamp();
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Callers must hold <see cref="_sendLock"/>.</summary>
    private void RetryUnpersistedSends()
    {
        while (_unpersistedSends.Count > 0)
        {
            var (eventIds, at) = _unpersistedSends[0];
            try
            {
                rejectedEvents.MarkTelegramNotified(eventIds, at);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not record Telegram delivery of {Count} rejected offline event(s)", eventIds.Length);
                return;
            }
            _unpersistedSends.RemoveAt(0);
        }
    }

    /// <summary>
    /// Same zone as live stamps. Anything that yields no usable Kimai zone -
    /// error response, unknown id, transport failure or timeout - falls back
    /// to UTC, which the message names explicitly.
    /// </summary>
    private async Task<TimeZoneInfo> ResolveTimeZoneAsync(
        RuntimeSettings settings, RejectedOfflineEvent entry, CancellationToken cancellationToken)
    {
        var employee = settings.Employees.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, entry.EmployeeId, StringComparison.OrdinalIgnoreCase));
        if (employee is null) return TimeZoneInfo.Utc;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeZoneLookupTimeout);
        try
        {
            var id = await kimai.GetCurrentUserTimezoneAsync(settings, employee, timeout.Token);
            // Not ClockService.ResolveTimezone: that falls back to the server zone.
            return string.IsNullOrWhiteSpace(id) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not load timezone for rejected offline event {EventId}", entry.EventId);
            return TimeZoneInfo.Utc;
        }
    }
}
