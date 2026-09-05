using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

/// <summary>
/// Telegram notifications must fire ONLY on real clock transitions (start,
/// stop, pauseStart, pauseEnd). No-op presses ("schon eingestempelt", "nicht
/// eingestempelt", "schon in Pause") must stay silent - otherwise double-taps
/// spam the customer's chat. Offline replays are covered separately (they run
/// through OfflineClockService, which never calls these helpers).
/// </summary>
public sealed class ClockServiceNotificationTests
{
    private static readonly ClockStatusDto Working = new(true, 5, "2026-09-05T06:00:00Z", 600, "working", "Eingestempelt");
    private static readonly ClockStatusDto Paused = new(true, 5, "2026-09-05T06:00:00Z", 600, "paused", "In Pause");
    private static readonly ClockStatusDto ClockedOut = new(false, null, null, 0, "clockedOut", "Nicht eingestempelt");

    private static RuntimeSettings Settings(bool telegramEnabled = true) => new()
    {
        BaseUrl = "http://kimai.test",
        DefaultProjectId = 1,
        DefaultActivityId = 2,
        PauseActivityId = 99,
        TelegramBotToken = telegramEnabled ? "123456:test" : null,
        TelegramChatId = telegramEnabled ? "-1001" : null,
        Employees =
        {
            new EmployeeSettings { Id = "max", Pin = "1234", ApiToken = "t", DisplayName = "Max Mustermann" }
        }
    };

    private static KioskClockRequest Request(string action) => new("max", "1234", action, null);

    private static (ClockService Service, ScriptedKimaiClient Kimai, RecordingNotifier Notifier) Create(RuntimeSettings? settings = null)
    {
        settings ??= Settings();
        var kimai = new ScriptedKimaiClient();
        var notifier = new RecordingNotifier();
        var service = new ClockService(new StubSettingsStore(settings), new EmployeeService(), kimai, notifier);
        return (service, kimai, notifier);
    }

    [Fact]
    public async Task ClockAsync_StartWhenClockedOut_NotifiesStart()
    {
        var (service, kimai, notifier) = Create();
        kimai.EnqueueStatus(ClockedOut);
        kimai.EnqueueStatus(Working);

        var response = await service.ClockAsync(Request("start"));

        Assert.Equal(ClockActionResult.Success, response.Result);
        var call = Assert.Single(notifier.Calls);
        Assert.Equal("Max Mustermann", call.DisplayName);
        Assert.Equal("start", call.Action);
    }

    [Fact]
    public async Task ClockAsync_StartWhenAlreadyRunning_DoesNotNotify()
    {
        var (service, kimai, notifier) = Create();
        kimai.EnqueueStatus(Working);

        var response = await service.ClockAsync(Request("start"));

        Assert.Equal(ClockActionResult.Success, response.Result);
        Assert.Empty(notifier.Calls);
        Assert.Equal(0, kimai.StartCalls);
    }

    [Fact]
    public async Task ClockAsync_StopWhenRunning_NotifiesStop()
    {
        var (service, kimai, notifier) = Create();
        kimai.EnqueueStatus(Working);
        kimai.EnqueueStatus(ClockedOut);

        var response = await service.ClockAsync(Request("stop"));

        Assert.Equal(ClockActionResult.Success, response.Result);
        var call = Assert.Single(notifier.Calls);
        Assert.Equal("Max Mustermann", call.DisplayName);
        Assert.Equal("stop", call.Action);
    }

    [Fact]
    public async Task ClockAsync_StopWhenNotRunning_DoesNotNotify()
    {
        var (service, kimai, notifier) = Create();
        kimai.EnqueueStatus(ClockedOut);

        var response = await service.ClockAsync(Request("stop"));

        Assert.Equal(ClockActionResult.Success, response.Result);
        Assert.Empty(notifier.Calls);
        Assert.Equal(0, kimai.StopCalls);
    }

    [Fact]
    public async Task ClockAsync_PauseStartWhenWorking_NotifiesPauseStart()
    {
        var (service, kimai, notifier) = Create();
        kimai.EnqueueStatus(Working);
        kimai.EnqueueStatus(Paused);

        var response = await service.ClockAsync(Request("pauseStart"));

        Assert.Equal(ClockActionResult.Success, response.Result);
        var call = Assert.Single(notifier.Calls);
        Assert.Equal("Max Mustermann", call.DisplayName);
        Assert.Equal("pauseStart", call.Action);
    }

    [Fact]
    public async Task ClockAsync_PauseStartWhenNotRunning_DoesNotNotify()
    {
        var (service, kimai, notifier) = Create();
        kimai.EnqueueStatus(ClockedOut);

        var response = await service.ClockAsync(Request("pauseStart"));

        Assert.Equal(ClockActionResult.Success, response.Result);
        Assert.Empty(notifier.Calls);
    }

    [Fact]
    public async Task ClockAsync_PauseStartWhenAlreadyPaused_DoesNotNotify()
    {
        var (service, kimai, notifier) = Create();
        kimai.EnqueueStatus(Paused);

        var response = await service.ClockAsync(Request("pauseStart"));

        Assert.Equal(ClockActionResult.Success, response.Result);
        Assert.Empty(notifier.Calls);
        Assert.Equal(0, kimai.StartPauseCalls);
    }

    [Fact]
    public async Task ClockAsync_PauseEndWhenPaused_NotifiesPauseEnd()
    {
        var (service, kimai, notifier) = Create();
        kimai.EnqueueStatus(Paused);
        kimai.EnqueueStatus(Working);

        var response = await service.ClockAsync(Request("pauseEnd"));

        Assert.Equal(ClockActionResult.Success, response.Result);
        var call = Assert.Single(notifier.Calls);
        Assert.Equal("Max Mustermann", call.DisplayName);
        Assert.Equal("pauseEnd", call.Action);
    }

    [Fact]
    public async Task ClockAsync_PauseEndWhenNotPaused_DoesNotNotify()
    {
        var (service, kimai, notifier) = Create();
        kimai.EnqueueStatus(Working);

        var response = await service.ClockAsync(Request("pauseEnd"));

        Assert.Equal(ClockActionResult.Success, response.Result);
        Assert.Empty(notifier.Calls);
    }

    [Fact]
    public async Task ClockAsync_TimezoneLookupFails_FallsBackAndStillNotifies()
    {
        var (service, kimai, notifier) = Create();
        kimai.TimezoneReturnsNull = true;
        kimai.EnqueueStatus(ClockedOut);
        kimai.EnqueueStatus(Working);

        var response = await service.ClockAsync(Request("start"));

        Assert.Equal(ClockActionResult.Success, response.Result);
        var call = Assert.Single(notifier.Calls);
        Assert.Equal("start", call.Action);
    }

    [Fact]
    public async Task ClockAsync_TelegramDisabled_DoesNotNotifyAndSkipsTimezoneLookup()
    {
        var (service, kimai, notifier) = Create(Settings(telegramEnabled: false));
        kimai.EnqueueStatus(ClockedOut);
        kimai.EnqueueStatus(Working);

        var response = await service.ClockAsync(Request("start"));

        Assert.Equal(ClockActionResult.Success, response.Result);
        Assert.Empty(notifier.Calls);
        Assert.Equal(0, kimai.TimezoneLookupCount);
    }

    private sealed class RecordingNotifier : ITelegramNotifier
    {
        public List<(string DisplayName, string Action)> Calls { get; } = [];

        public Task SendStampNotificationAsync(
            string employeeName, string action, DateTimeOffset stampUtc, TimeZoneInfo timeZone)
        {
            Calls.Add((employeeName, action));
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedKimaiClient : IKimaiClient
    {
        private readonly Queue<ClockStatusDto> _statuses = new();

        public bool TimezoneReturnsNull { get; set; }
        public int TimezoneLookupCount { get; private set; }
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int StartPauseCalls { get; private set; }

        public void EnqueueStatus(ClockStatusDto status) => _statuses.Enqueue(status);

        public Task<ClockStatusDto> GetStatusAsync(RuntimeSettings s, EmployeeSettings e, CancellationToken ct = default) =>
            Task.FromResult(_statuses.Count > 0 ? _statuses.Dequeue() : ClockedOut);

        public Task<string?> GetCurrentUserTimezoneAsync(RuntimeSettings s, EmployeeSettings e, CancellationToken ct = default)
        {
            TimezoneLookupCount++;
            return Task.FromResult<string?>(TimezoneReturnsNull ? null : "Europe/Berlin");
        }

        public Task StartAsync(RuntimeSettings s, EmployeeSettings e, CancellationToken ct = default)
        {
            StartCalls++;
            return Task.CompletedTask;
        }

        public Task StartPauseAsync(RuntimeSettings s, EmployeeSettings e, CancellationToken ct = default)
        {
            StartPauseCalls++;
            return Task.CompletedTask;
        }

        public Task StopAsync(RuntimeSettings s, EmployeeSettings e, int timesheetId, CancellationToken ct = default)
        {
            StopCalls++;
            return Task.CompletedTask;
        }

        public Task StartAtAsync(RuntimeSettings s, EmployeeSettings e, int p, int a, DateTimeOffset d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StopAtAsync(RuntimeSettings s, EmployeeSettings e, int id, DateTimeOffset d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KimaiRecentTimesheetDto?> GetLatestStoppedTimesheetAsync(RuntimeSettings s, EmployeeSettings e, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<KimaiUserDto>> GetUsersAsync(string b, string t, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<KimaiActivityDto>> GetActivitiesAsync(string b, string t, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<KimaiProjectDto>> GetProjectsAsync(string b, string t, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<KimaiTimesheetEntryDto>> GetTimesheetsAsync(RuntimeSettings settings, EmployeeSettings employee, DateTime begin, DateTime end, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubSettingsStore(RuntimeSettings settings) : IRuntimeSettingsStore
    {
        public RuntimeSettings Load() => settings;

        public Task SaveAsync(RuntimeSettings settings, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
