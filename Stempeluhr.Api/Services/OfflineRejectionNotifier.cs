namespace Stempeluhr.Api.Services;

/// <summary>Process-wide Telegram throttle for newly journaled offline refusals.</summary>
public sealed class OfflineRejectionNotifier(
    IRuntimeSettingsStore settingsStore,
    ITelegramNotifier telegram,
    ILogger<OfflineRejectionNotifier> logger,
    TimeProvider? clock = null) : IDisposable
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);
    private const int DailyLimit = 20;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private RejectedOfflineEvent? _first;
    private RejectedOfflineEvent? _last;
    private int _pending;
    private DateTimeOffset _nextSend;
    private DateOnly _day;
    private int _sentToday;
    private ITimer? _timer;

    public void Report(IReadOnlyList<RejectedOfflineEvent> rejected)
    {
        if (rejected.Count == 0) return;
        // Unknown identities and malformed requests cannot produce Telegram traffic.
        // Check configuration before doing any formatting or other work.
        if (!IsEnabled())
        {
            return;
        }

        lock (_gate)
        {
            foreach (var entry in rejected.Where(entry => !string.IsNullOrWhiteSpace(entry.EmployeeName)))
            {
                _first ??= entry;
                _last = entry;
                _pending++;
            }
            DispatchOrSchedule();
        }
    }

    private void DispatchOrSchedule()
    {
        if (_pending == 0) return;
        var now = _clock.GetUtcNow();
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        if (_day != day)
        {
            _day = day;
            _sentToday = 0;
        }

        var allowedAt = _sentToday >= DailyLimit
            ? new DateTimeOffset(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero)
            : _nextSend;
        if (now < allowedAt)
        {
            _timer?.Dispose();
            _timer = _clock.CreateTimer(_ => OnTimer(), null, allowedAt - now, Timeout.InfiniteTimeSpan);
            return;
        }

        _timer?.Dispose();
        _timer = null;
        var text = Format(_first!, _last!, _pending);
        _first = _last = null;
        _pending = 0;
        _sentToday++;
        _nextSend = now + MinimumInterval;
        _ = SendSafelyAsync(text);
    }

    private void OnTimer()
    {
        lock (_gate)
        {
            if (IsEnabled()) DispatchOrSchedule();
            else { _first = _last = null; _pending = 0; _timer?.Dispose(); _timer = null; }
        }
    }

    private bool IsEnabled()
    {
        try { return settingsStore.Load().TelegramEnabled; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not check Telegram configuration for offline rejection");
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate) { _timer?.Dispose(); _timer = null; }
    }

    private async Task SendSafelyAsync(string text)
    {
        try { await telegram.SendMessageAsync(text); }
        catch (Exception ex) { logger.LogWarning(ex, "Offline rejection Telegram notification failed"); }
    }

    private static string Format(RejectedOfflineEvent first, RejectedOfflineEvent last, int count)
    {
        static string Describe(RejectedOfflineEvent entry)
        {
            var action = entry.Action switch
            {
                "start" => "Einstempeln", "stop" => "Ausstempeln",
                "pauseStart" => "Pausenbeginn", "pauseEnd" => "Pausenende",
                "switch" => "Tätigkeitswechsel", _ => "Stempeln"
            };
            // The event timestamp can come from an unauthenticated client. UTC is
            // explicit here so a container's local timezone cannot shift the time.
            return $"{Short(entry.EmployeeName)} · {action} · {entry.PerformedAt.UtcDateTime:dd.MM. HH:mm} UTC\nGrund: {Short(entry.Message)}";
        }

        return count == 1
            ? $"⚠️ Offline-Stempel nicht übernommen\n{Describe(first)}\nBitte in Kimai nachtragen."
            : $"⚠️ {count} Offline-Stempel nicht übernommen\nErster Fall: {Describe(first)}\nLetzter Fall: {Describe(last)}\nBitte in Kimai nachtragen.";
    }

    private static string Short(string value) => value.Length <= 180 ? value : value[..180] + "…";
}
