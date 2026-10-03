namespace Stempeluhr.Api.Services;

/// <summary>
/// Checks every minute whether the configured terminals still report and
/// whether their reports show problems (see <see cref="TerminalHealthEvaluator"/>).
/// Sends one Telegram message when a problem starts and one when it ends,
/// no reminders in between. Evaluates even without Telegram, so the admin
/// status page shows the same state. Never touches stamping.
/// </summary>
public sealed class TerminalHealthWatcher(
    IRuntimeSettingsStore settingsStore,
    TerminalHealthStore health,
    ITelegramNotifier telegram,
    ILogger<TerminalHealthWatcher> logger,
    TimeProvider? clock = null,
    TimeZoneInfo? timeZone = null) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    // The container runs with TZ=Europe/Berlin (Dockerfile).
    private readonly TimeZoneInfo _timeZone = timeZone ?? TimeZoneInfo.Local;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval, _clock);
        do
        {
            try
            {
                await CheckAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Terminal health check failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task CheckAsync()
    {
        var settings = settingsStore.Load();
        var terminalIds = settings.TerminalTokens.Keys.ToArray();
        health.Retain(terminalIds);

        foreach (var terminalId in terminalIds)
        {
            var now = _clock.GetUtcNow();
            var changes = health.Update(terminalId, now);
            if (changes.Count == 0 || !settings.TelegramAlertsEnabled) continue;

            var report = health.Snapshot(terminalId, now).Report;
            var text = TelegramMessageFactory.BuildTerminalAlert(terminalId, changes, report, now, _timeZone);
            // Not accepted: the next check tries again.
            if (await telegram.SendAlertAsync(text))
            {
                health.MarkNotified(terminalId, changes);
            }
        }
    }
}
