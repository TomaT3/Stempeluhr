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
/// Telegram-Nachricht für nachgetragene Offline-Stempel (issue #117): ein
/// Block je Mitarbeiter und Runde. Wie beim Live-Stempel best effort ohne
/// Retry - ein Sendefehler darf den Nachtrag nie scheitern lassen. Weil es
/// keinen Retry gibt, darf Telegram auch nach einem langen Ausfall nicht
/// drosseln (429): Wartende Blöcke werden zu Nachrichten bis zur
/// Telegram-Höchstlänge zusammengefasst, und zwischen zwei Nachrichten liegt
/// mindestens <see cref="MinimumInterval"/>, auch über Rundengrenzen hinweg.
/// </summary>
public sealed class OfflineStampNotifier(
    IRuntimeSettingsStore settingsStore,
    IKimaiClient kimai,
    ITelegramNotifier telegram,
    ILogger<OfflineStampNotifier> logger,
    TimeProvider? clock = null,
    Func<TimeSpan, Task>? delay = null) : IOfflineStampNotifier
{
    /// <summary>
    /// Höchstens 10 Nachrichten pro Minute: die Hälfte des Telegram-Limits
    /// für Gruppen (20 pro Minute), der Rest bleibt für Live-Stempel und
    /// Warnungen im selben Chat.
    /// </summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(6);

    private static readonly TimeSpan TimeZoneLookupTimeout = TimeSpan.FromSeconds(5);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Func<TimeSpan, Task> _delay = delay ?? (wait => Task.Delay(wait, clock ?? TimeProvider.System));

    // Runden nacheinander vorbereiten, damit ihre Blöcke in der Reihenfolge
    // der Nachträge in die Warteschlange kommen.
    private readonly SemaphoreSlim _prepareLock = new(1, 1);

    // Nur ein Sender: Er nimmt bei jeder Nachricht alles mit, was bis dahin
    // wartet, auch Blöcke späterer Runden.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // Guarded by lock (_pending).
    private readonly Queue<string> _pending = new();

    // Monoton, damit ein Zurückstellen der Uhr den Abstand nicht verlängert.
    private long? _lastSentAt;

    public void Report(IReadOnlyList<AppliedOfflineStamp> stamps)
    {
        if (stamps.Count == 0) return;
        // Direkt statt über Task.Run: So stellt sich die Runde noch synchron
        // am _prepareLock an, in der Reihenfolge der Runden.
        _ = SendAsync(stamps);
    }

    /// <summary>Stellt die Blöcke einer Runde ein und sendet, was wartet.</summary>
    public async Task SendAsync(IReadOnlyList<AppliedOfflineStamp> stamps)
    {
        await _prepareLock.WaitAsync();
        try
        {
            var settings = settingsStore.Load();
            if (!settings.TelegramEnabled) return;

            foreach (var group in stamps.GroupBy(stamp => stamp.EmployeeId, StringComparer.OrdinalIgnoreCase))
            {
                var employeeStamps = group.OrderBy(stamp => stamp.PerformedAt).ToArray();
                var zone = await ResolveTimeZoneAsync(settings, group.Key);
                var text = TelegramMessageFactory.BuildOfflineStampNotice(
                    employeeStamps[^1].EmployeeName, employeeStamps, zone, _clock.GetUtcNow());
                lock (_pending) _pending.Enqueue(text);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Telegram notice for replayed offline stamps could not be prepared");
        }
        finally
        {
            _prepareLock.Release();
        }

        await DrainAsync();
    }

    private async Task DrainAsync()
    {
        await _sendLock.WaitAsync();
        try
        {
            while (HasPending())
            {
                // Erst warten, dann nehmen: Was in der Pause dazukommt, geht
                // in dieselbe Nachricht.
                if (_lastSentAt is { } last && MinimumInterval - _clock.GetElapsedTime(last) is var wait && wait > TimeSpan.Zero)
                {
                    await _delay(wait);
                }

                if (TakeMessage() is not { } text) return;
                try
                {
                    if (!await telegram.SendMessageAsync(text))
                    {
                        logger.LogWarning("Telegram did not accept a notice for replayed offline stamps");
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Telegram notice for replayed offline stamps could not be sent");
                }
                _lastSentAt = _clock.GetTimestamp();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Telegram notices for replayed offline stamps stopped");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private bool HasPending()
    {
        lock (_pending) return _pending.Count > 0;
    }

    /// <summary>
    /// So viele wartende Blöcke, wie in eine Nachricht passen; jeder Block
    /// hält die Höchstlänge für sich schon ein. Null, wenn nichts wartet.
    /// </summary>
    private string? TakeMessage()
    {
        lock (_pending)
        {
            if (!_pending.TryDequeue(out var message)) return null;
            while (_pending.TryPeek(out var next)
                   && message.Length + 2 + next.Length <= TelegramMessageFactory.MaxMessageLength)
            {
                message += "\n\n" + _pending.Dequeue();
            }
            return message;
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
