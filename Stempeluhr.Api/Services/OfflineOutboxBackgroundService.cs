namespace Stempeluhr.Api.Services;

/// <summary>
/// Periodically flushes the offline outbox so events that were buffered while
/// Kimai was unreachable are applied without needing a new request from a
/// client. The outbox lives in the singleton <see cref="IOfflineClockService"/>;
/// this service only nudges it.
/// </summary>
public sealed class OfflineOutboxBackgroundService(
    IOfflineClockService offlineClockService,
    OfflineRejectionNotifier rejectionNotifier,
    ILogger<OfflineOutboxBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Kicked, not awaited: slow Kimai timezone lookups or Telegram must not
        // delay the outbox replay. Kicked every tick, even after a failed
        // flush, so warnings held by the throttle are never stranded.
        rejectionNotifier.Report();
        using var timer = new PeriodicTimer(FlushInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await offlineClockService.FlushOutboxAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Offline outbox flush failed");
            }

            rejectionNotifier.Report();
        }
    }
}
