using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

/// <summary>
/// Warns via Telegram when an employee works longer than the limits of
/// <see cref="WorkTimeLimitCalculator"/>. Checks Kimai periodically instead of
/// reacting to stamps: the warning arrives while the employee is still clocked
/// in, and backdated offline replays and other terminals count the same way.
/// Never touches stamping - no request, no sync lock.
/// </summary>
public sealed class WorkTimeAlertService(
    IRuntimeSettingsStore settingsStore,
    IKimaiClient kimai,
    ITelegramNotifier telegram,
    WorkTimeAlertStore alerts,
    ILogger<WorkTimeAlertService> logger,
    TimeProvider? clock = null) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval, _clock);
        do
        {
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Work time check failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        var settings = settingsStore.Load();
        // Without Telegram nobody would read the result: no Kimai calls either.
        if (!settings.TelegramEnabled || string.IsNullOrWhiteSpace(settings.BaseUrl)) return;

        foreach (var employee in settings.Employees.Where(employee => employee.CanClock))
        {
            try
            {
                await CheckEmployeeAsync(settings, employee, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One unreachable token must not hide the others' warnings.
                logger.LogWarning(ex, "Work time check failed for employee {EmployeeId}", employee.Id);
            }
        }
    }

    private async Task CheckEmployeeAsync(
        RuntimeSettings settings, EmployeeSettings employee, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var windowStart = now - WorkTimeLimitCalculator.Lookback;
        // Kimai reads the naive window bounds in the token owner's zone; the
        // same zone formats the warning, like live stamp notifications.
        var timeZone = ClockService.ResolveTimezone(
            await kimai.GetCurrentUserTimezoneAsync(settings, employee, cancellationToken));
        var entries = await kimai.GetTimesheetsAsync(
            settings,
            employee,
            TimeZoneInfo.ConvertTime(windowStart, timeZone).DateTime,
            TimeZoneInfo.ConvertTime(now, timeZone).DateTime,
            cancellationToken);

        foreach (var violation in WorkTimeLimitCalculator.Evaluate(entries, settings.PauseActivityId, windowStart, now))
        {
            if (alerts.HasSent(employee.Id, violation.Key)) continue;

            var text = violation.Kind == WorkTimeViolationKind.Continuous
                ? TelegramMessageFactory.BuildContinuousWorkWarning(
                    employee.DisplayName, violation.Start, violation.WorkedSeconds, timeZone)
                : TelegramMessageFactory.BuildShiftWorkWarning(
                    employee.DisplayName, violation.Start, violation.WorkedSeconds, timeZone);

            // Not accepted: the next check tries again.
            if (!await telegram.SendMessageAsync(text)) return;
            alerts.MarkSent(employee.Id, violation.Key, _clock.GetUtcNow());
        }
    }
}
