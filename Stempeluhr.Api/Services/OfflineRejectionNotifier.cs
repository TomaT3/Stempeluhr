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
    private const int DailyLimit = 20;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private DateTimeOffset _nextAttemptAt;

    /// <summary>Kick delivery after a new journal entry without delaying sync.</summary>
    public void Report(IReadOnlyList<RejectedOfflineEvent> newlyRejected)
    {
        if (newlyRejected.Count == 0) return;
        _ = Task.Run(ProcessPendingAsync);
    }

    /// <summary>Also called periodically, so a restart cannot lose held warnings.</summary>
    public async Task ProcessPendingAsync()
    {
        await _sendLock.WaitAsync();
        try
        {
            var settings = settingsStore.Load();
            if (!settings.TelegramEnabled) return;

            var all = rejectedEvents.List();
            var pending = all.Where(entry => entry.TelegramEligible && entry.TelegramNotifiedAt is null)
                .OrderBy(entry => entry.PerformedAt).ToArray();
            if (pending.Length == 0) return;

            var now = _clock.GetUtcNow();
            var sentToday = all.Where(entry => entry.TelegramNotifiedAt is { } at
                    && at.UtcDateTime.Date == now.UtcDateTime.Date)
                .Select(entry => entry.TelegramNotifiedAt!.Value)
                .Distinct().Count();
            if (sentToday >= DailyLimit || now < _nextAttemptAt) return;

            var lastSent = all.Where(entry => entry.TelegramNotifiedAt is not null)
                .Max(entry => entry.TelegramNotifiedAt);
            if (lastSent is { } last && now - last < MinimumInterval) return;

            var first = pending[0];
            var latest = pending[^1];
            var firstZone = await ResolveTimeZoneAsync(settings, first);
            var lastZone = latest.EmployeeId.Equals(first.EmployeeId, StringComparison.OrdinalIgnoreCase)
                ? firstZone : await ResolveTimeZoneAsync(settings, latest);
            var text = TelegramMessageFactory.BuildOfflineRejectionSummary(
                first, firstZone, latest, lastZone, pending.Length);

            // A failed send remains pending. A successful send is recorded
            // before the next batch can start, including across restarts.
            if (await telegram.SendMessageAsync(text))
            {
                rejectedEvents.MarkTelegramNotified(pending.Select(entry => entry.EventId).ToArray(),
                    _clock.GetUtcNow());
            }
            else
            {
                _nextAttemptAt = now + MinimumInterval;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Offline rejection Telegram notification failed");
            _nextAttemptAt = _clock.GetUtcNow() + MinimumInterval;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<TimeZoneInfo> ResolveTimeZoneAsync(RuntimeSettings settings, RejectedOfflineEvent entry)
    {
        var employee = settings.Employees.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, entry.EmployeeId, StringComparison.OrdinalIgnoreCase));
        if (employee is null) return TimeZoneInfo.Utc;

        try
        {
            var id = await kimai.GetCurrentUserTimezoneAsync(settings, employee, CancellationToken.None);
            return ClockService.ResolveTimezone(id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load timezone for rejected offline event {EventId}", entry.EventId);
            return TimeZoneInfo.Utc;
        }
    }
}
