using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

/// <summary>
/// Ein nachgetragener Offline-Stempel, der Kimai tatsächlich geändert hat.
/// <paramref name="PerformedAt"/> ist der Zeitpunkt am Terminal, nicht der
/// des Nachtrags. <paramref name="TaskLabel"/> wie beim Live-Stempel
/// (<see cref="ITelegramNotifier.SendStampNotificationAsync"/>).
/// </summary>
public sealed record AppliedOfflineStamp(
    string EmployeeId,
    string EmployeeName,
    string Action,
    DateTimeOffset PerformedAt,
    string? TaskLabel = null);

public interface IOfflineStampNotifier
{
    /// <summary>
    /// Meldet die Stempel einer Verarbeitungsrunde, ohne den Aufrufer zu
    /// verzögern. Wirft nie.
    /// </summary>
    void Report(IReadOnlyList<AppliedOfflineStamp> stamps);
}

/// <summary>
/// Telegram-Nachricht für nachgetragene Offline-Stempel (issue #117): eine
/// Nachricht je Mitarbeiter und Runde, damit ein langer Ausfall den Chat
/// nicht flutet. Wie beim Live-Stempel best effort ohne Retry - ein
/// Sendefehler darf den Nachtrag nie scheitern lassen.
/// </summary>
public sealed class OfflineStampNotifier(
    IRuntimeSettingsStore settingsStore,
    IKimaiClient kimai,
    ITelegramNotifier telegram,
    ILogger<OfflineStampNotifier> logger,
    TimeProvider? clock = null) : IOfflineStampNotifier
{
    private static readonly TimeSpan TimeZoneLookupTimeout = TimeSpan.FromSeconds(5);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    // Nacheinander senden, damit die Nachrichten zweier Runden in der
    // Reihenfolge der Nachträge im Chat stehen.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public void Report(IReadOnlyList<AppliedOfflineStamp> stamps)
    {
        if (stamps.Count == 0) return;
        _ = SendAsync(stamps);
    }

    public async Task SendAsync(IReadOnlyList<AppliedOfflineStamp> stamps)
    {
        await _sendLock.WaitAsync();
        try
        {
            var settings = settingsStore.Load();
            if (!settings.TelegramEnabled) return;

            foreach (var group in stamps.GroupBy(stamp => stamp.EmployeeId, StringComparer.OrdinalIgnoreCase))
            {
                var employeeStamps = group.OrderBy(stamp => stamp.PerformedAt).ToArray();
                try
                {
                    var zone = await ResolveTimeZoneAsync(settings, group.Key);
                    var text = TelegramMessageFactory.BuildOfflineStampNotice(
                        employeeStamps[^1].EmployeeName, employeeStamps, zone, _clock.GetUtcNow());
                    if (!await telegram.SendMessageAsync(text))
                    {
                        logger.LogWarning("Telegram did not accept the notice for {Count} replayed offline stamp(s)", employeeStamps.Length);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Telegram notice for {Count} replayed offline stamp(s) could not be sent", employeeStamps.Length);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Telegram notice for replayed offline stamps could not be prepared");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Wie beim Live-Stempel: ohne Kimai-Zeitzone die des Servers.</summary>
    private async Task<TimeZoneInfo> ResolveTimeZoneAsync(RuntimeSettings settings, string employeeId)
    {
        var employee = settings.Employees.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, employeeId, StringComparison.OrdinalIgnoreCase));
        if (employee is null) return TimeZoneInfo.Local;

        using var timeout = new CancellationTokenSource(TimeZoneLookupTimeout);
        try
        {
            return ClockService.ResolveTimezone(await kimai.GetCurrentUserTimezoneAsync(settings, employee, timeout.Token));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load the timezone for replayed offline stamps of {EmployeeId}", employeeId);
            return TimeZoneInfo.Local;
        }
    }
}
