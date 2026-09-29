using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task ClockAsync_TimezoneLookupThrows_StampStillSucceedsAndLossIsLogged()
    {
        // Transportfehler (Timeout/Netzwerk) werden von KimaiClient nicht in
        // null übersetzt - sie propagieren in den Catch und die Nachricht
        // entfällt. Der Stempel selbst muss erfolgreich bleiben und der
        // Verlust darf nicht spurlos sein (LogWarning).
        var settings = Settings();
        var kimai = new ScriptedKimaiClient { TimezoneThrows = true };
        kimai.EnqueueStatus(ClockedOut);
        kimai.EnqueueStatus(Working);
        var logger = new RecordingLogger();
        var service = new ClockService(
            new StubSettingsStore(settings),
            new EmployeeService(),
            kimai,
            new RecordingNotifier(),
            logger);

        var response = await service.ClockAsync(Request("start"));

        Assert.Equal(ClockActionResult.Success, response.Result);
        var message = Assert.Single(logger.Messages);
        Assert.Contains("could not be prepared", message);
        // Die Exception selbst muss erhalten bleiben (LogWarning mit ex) -
        // sonst wäre die Verlust-Stelle ohne Stack-Trace-Spur.
        Assert.IsType<HttpRequestException>(Assert.Single(logger.Exceptions));
    }

    // --- Task switch (live path) ---------------------------------------

    private static readonly ClockStatusDto WorkingOnTask =
        new(true, 6, "2026-09-05T08:00:00Z", 60, "working", "Eingestempelt", "kx", "Kunde X");

    private static RuntimeSettings SettingsWithTask() => new()
    {
        BaseUrl = "http://kimai.test",
        DefaultProjectId = 1,
        DefaultActivityId = 2,
        PauseActivityId = 99,
        TelegramBotToken = "123456:test",
        TelegramChatId = "-1001",
        Employees =
        {
            new EmployeeSettings
            {
                Id = "max",
                Pin = "1234",
                ApiToken = "t",
                DisplayName = "Max Mustermann",
                Tasks = [new EmployeeTaskSettings { Id = "kx", Label = "Kunde X", ProjectId = 20, ActivityId = 21, Billable = false }]
            }
        }
    };

    private static KioskClockRequest SwitchRequest(string? taskId) => new("max", "1234", "switch", null, taskId);

    [Fact]
    public async Task Switch_FromDefaultToTask_StopsAndStartsOnTask()
    {
        var (service, kimai, notifier) = Create(SettingsWithTask());
        kimai.EnqueueStatus(Working);
        kimai.EnqueueStatus(WorkingOnTask);

        var response = await service.ClockAsync(SwitchRequest("kx"));

        Assert.Equal(ClockActionResult.Success, response.Result);
        Assert.Equal("Wechsel zu Kunde X", response.Status!.StateText);
        Assert.Equal("kx", response.Status.ActiveTaskId);
        Assert.Equal(1, kimai.StopCalls);
        var target = Assert.Single(kimai.StartedTargets);
        Assert.Equal((20, 21, "Kunde X", false), (target.ProjectId, target.ActivityId, target.Description, target.Billable));
        Assert.Equal("switch", Assert.Single(notifier.Calls).Action);
        Assert.Equal("Kunde X", Assert.Single(notifier.TaskLabels));
    }

    [Fact]
    public async Task Switch_BackToDefault_StartsDefaultTask()
    {
        var (service, kimai, notifier) = Create(SettingsWithTask());
        kimai.EnqueueStatus(WorkingOnTask);
        kimai.EnqueueStatus(Working);

        var response = await service.ClockAsync(SwitchRequest(null));

        Assert.Equal("Zurueck zur Standard-Taetigkeit", response.Status!.StateText);
        var target = Assert.Single(kimai.StartedTargets);
        Assert.Equal((1, 2), (target.ProjectId, target.ActivityId));
        Assert.Null(Assert.Single(notifier.TaskLabels));
    }

    [Fact]
    public async Task Switch_BackToDefault_UsesDefaultTaskLabel()
    {
        var settings = SettingsWithTask();
        var max = settings.Employees[0];
        settings.Employees[0] = new EmployeeSettings
        {
            Id = max.Id, Pin = max.Pin, ApiToken = max.ApiToken, DisplayName = max.DisplayName,
            Tasks = max.Tasks, DefaultTaskLabel = "Büro"
        };
        var (service, kimai, notifier) = Create(settings);
        kimai.EnqueueStatus(WorkingOnTask);
        kimai.EnqueueStatus(Working);

        var response = await service.ClockAsync(SwitchRequest(null));

        Assert.Equal("Wechsel zu Büro", response.Status!.StateText);
        Assert.Equal("Büro", Assert.Single(notifier.TaskLabels));
    }

    [Theory]
    [InlineData("onTarget")]
    [InlineData("paused")]
    [InlineData("clockedOut")]
    public async Task Switch_WithoutPossibleTransition_IsSilentNoOp(string state)
    {
        var (service, kimai, notifier) = Create(SettingsWithTask());
        kimai.EnqueueStatus(state switch { "onTarget" => WorkingOnTask, "paused" => Paused, _ => ClockedOut });

        var response = await service.ClockAsync(SwitchRequest("kx"));

        Assert.Equal(ClockActionResult.Success, response.Result);
        Assert.Equal(0, kimai.StopCalls);
        Assert.Empty(kimai.StartedTargets);
        Assert.Empty(notifier.Calls);
    }

    [Fact]
    public async Task Switch_UnknownTask_IsBadRequest()
    {
        var (service, kimai, _) = Create(SettingsWithTask());
        kimai.EnqueueStatus(Working);

        var response = await service.ClockAsync(SwitchRequest("gone"));

        Assert.Equal(ClockActionResult.BadRequest, response.Result);
        Assert.Equal(0, kimai.StopCalls);
    }

    // --- Switch rejected by Kimai after the stop (issue #56) -------------

    private static KimaiApiException KimaiError(int statusCode) =>
        new((System.Net.HttpStatusCode)statusCode, "simulated");

    [Fact]
    public async Task Switch_TargetRejectedByKimai_ContinuesOnThePreviousTask()
    {
        // Kimai refuses the target (no team access, archived project, ...)
        // after the stop went through: the employee must not end up clocked
        // out, and the kiosk has to say that the switch did not happen.
        var (service, kimai, notifier) = Create(SettingsWithTask());
        kimai.EnqueueStatus(Working with { ActiveIsDefaultTask = true });
        kimai.EnqueueStatus(Working with { ActiveIsDefaultTask = true });
        kimai.StartFailures.Enqueue(KimaiError(400));

        var response = await service.ClockAsync(SwitchRequest("kx"));

        Assert.Equal(ClockActionResult.Success, response.Result);
        Assert.Equal("Kunde X nicht moeglich - weiter auf Standard-Taetigkeit", response.Status!.Warning);
        Assert.Equal("Eingestempelt", response.Status.StateText);
        Assert.Equal(1, kimai.StopCalls);
        var continued = Assert.Single(kimai.StartedTargets);
        Assert.Equal((1, 2), (continued.ProjectId, continued.ActivityId));
        Assert.Empty(notifier.Calls);
    }

    [Fact]
    public async Task Switch_BackToDefaultRejected_ContinuesOnTheTaskThatRan()
    {
        var (service, kimai, notifier) = Create(SettingsWithTask());
        kimai.EnqueueStatus(WorkingOnTask);
        kimai.EnqueueStatus(WorkingOnTask);
        kimai.StartFailures.Enqueue(KimaiError(403));

        var response = await service.ClockAsync(SwitchRequest(null));

        Assert.Equal("Standard-Taetigkeit nicht moeglich - weiter auf Kunde X", response.Status!.Warning);
        var continued = Assert.Single(kimai.StartedTargets);
        Assert.Equal((20, 21), (continued.ProjectId, continued.ActivityId));
        Assert.Empty(notifier.Calls);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(503)]
    public async Task Switch_TargetAndContinuationFail_KeepsTheOriginalError(int continuationStatus)
    {
        var (service, kimai, notifier) = Create(SettingsWithTask());
        kimai.EnqueueStatus(Working with { ActiveIsDefaultTask = true });
        kimai.StartFailures.Enqueue(KimaiError(400));
        kimai.StartFailures.Enqueue(KimaiError(continuationStatus));

        var error = await Assert.ThrowsAsync<KimaiApiException>(() => service.ClockAsync(SwitchRequest("kx")));

        // A 4xx: the kiosk shows "Kimai konnte nicht speichern" instead of
        // queueing a switch that Kimai will never accept.
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Equal(2, kimai.StartAttempts);
        Assert.Empty(notifier.Calls);
    }

    [Fact]
    public async Task Switch_ToDefaultRejectedFromUnknownSheet_HasNothingToContinueOn()
    {
        // The stopped sheet matches no task: continuing would mean the default
        // task again - the very target Kimai just refused.
        var (service, kimai, _) = Create(SettingsWithTask());
        kimai.EnqueueStatus(Working);
        kimai.StartFailures.Enqueue(KimaiError(400));

        await Assert.ThrowsAsync<KimaiApiException>(() => service.ClockAsync(SwitchRequest(null)));

        Assert.Equal(1, kimai.StartAttempts);
    }

    [Theory]
    [InlineData(502)]
    [InlineData(408)]
    [InlineData(429)]
    public async Task Switch_TransientStartFailure_IsNotCompensated(int statusCode)
    {
        // 5xx, 408, 429: the kiosk queues the switch, the replay decides -
        // continuing on the old task here would pre-empt it.
        var (service, kimai, _) = Create(SettingsWithTask());
        kimai.EnqueueStatus(Working with { ActiveIsDefaultTask = true });
        kimai.StartFailures.Enqueue(KimaiError(statusCode));

        var error = await Assert.ThrowsAsync<KimaiApiException>(() => service.ClockAsync(SwitchRequest("kx")));

        Assert.Equal(statusCode, (int)error.StatusCode);
        Assert.True(error.IsTransient);
        Assert.Equal(1, kimai.StartAttempts);
    }

    [Fact]
    public async Task Start_OnTask_BooksTheTaskAndNamesIt()
    {
        var (service, kimai, notifier) = Create(SettingsWithTask());
        kimai.EnqueueStatus(ClockedOut);
        kimai.EnqueueStatus(WorkingOnTask);

        var response = await service.ClockAsync(new KioskClockRequest("max", "1234", "start", null, "kx"));

        Assert.Equal(ClockActionResult.Success, response.Result);
        Assert.Equal("Eingestempelt", response.Status!.StateText);
        Assert.Equal("kx", response.Status.ActiveTaskId);
        var target = Assert.Single(kimai.StartedTargets);
        Assert.Equal((20, 21, "Kunde X", false), (target.ProjectId, target.ActivityId, target.Description, target.Billable));
        Assert.Equal("start", Assert.Single(notifier.Calls).Action);
        Assert.Equal("Kunde X", Assert.Single(notifier.TaskLabels));
    }

    [Fact]
    public async Task Start_WithoutTask_BooksTheDefaultTask()
    {
        var (service, kimai, notifier) = Create(SettingsWithTask());
        kimai.EnqueueStatus(ClockedOut);
        kimai.EnqueueStatus(Working);

        await service.ClockAsync(Request("start"));

        var target = Assert.Single(kimai.StartedTargets);
        Assert.Equal((1, 2), (target.ProjectId, target.ActivityId));
        // With further tasks the message says which one - here the default,
        // spelled like the switch message ("zurück zur Standard-Tätigkeit").
        Assert.Equal("Standard-Tätigkeit", Assert.Single(notifier.TaskLabels));
    }

    [Fact]
    public async Task Start_WithoutFurtherTasks_KeepsThePlainMessage()
    {
        var (service, kimai, notifier) = Create();
        kimai.EnqueueStatus(ClockedOut);
        kimai.EnqueueStatus(Working);

        await service.ClockAsync(Request("start"));

        Assert.Null(Assert.Single(notifier.TaskLabels));
    }

    [Fact]
    public async Task Start_UnknownTaskWhenClockedOut_IsBadRequest()
    {
        var (service, kimai, notifier) = Create(SettingsWithTask());
        kimai.EnqueueStatus(ClockedOut);

        var response = await service.ClockAsync(new KioskClockRequest("max", "1234", "start", null, "gone"));

        Assert.Equal(ClockActionResult.BadRequest, response.Result);
        Assert.Empty(kimai.StartedTargets);
        Assert.Empty(notifier.Calls);
    }

    [Theory]
    [InlineData("working", "Schon eingestempelt")]
    [InlineData("paused", "Aktuell in Pause")]
    public async Task Start_UnknownTaskWhileRunning_IsTheUsualNoOp(string state, string stateText)
    {
        // A stale task list (task deleted meanwhile) must not turn the double
        // tap or the second terminal into an error: running beats the task.
        var (service, kimai, notifier) = Create(SettingsWithTask());
        kimai.EnqueueStatus(state == "paused" ? Paused : Working);

        var response = await service.ClockAsync(new KioskClockRequest("max", "1234", "start", null, "gone"));

        Assert.Equal(ClockActionResult.Success, response.Result);
        Assert.Equal(stateText, response.Status!.StateText);
        Assert.Empty(kimai.StartedTargets);
        Assert.Empty(notifier.Calls);
    }

    [Fact]
    public async Task Start_OnTaskWhileAlreadyWorking_IsSilentNoOp()
    {
        // Double tap or a second terminal: a running sheet is never replaced
        // by a start - that is what the switch is for.
        var (service, kimai, notifier) = Create(SettingsWithTask());
        kimai.EnqueueStatus(Working);

        var response = await service.ClockAsync(new KioskClockRequest("max", "1234", "start", null, "kx"));

        Assert.Equal("Schon eingestempelt", response.Status!.StateText);
        Assert.Equal(0, kimai.StopCalls);
        Assert.Empty(kimai.StartedTargets);
        Assert.Empty(notifier.Calls);
    }

    [Fact]
    public async Task PauseEnd_ResumesTheTaskThatRanBeforeThePause()
    {
        var (service, kimai, _) = Create(SettingsWithTask());
        kimai.LatestStopped = new KimaiRecentTimesheetDto(21, DateTimeOffset.UtcNow, 20);
        kimai.EnqueueStatus(Paused);
        kimai.EnqueueStatus(WorkingOnTask);

        await service.ClockAsync(Request("pauseEnd"));

        var target = Assert.Single(kimai.StartedTargets);
        Assert.Equal((20, 21), (target.ProjectId, target.ActivityId));
    }

    [Fact]
    public async Task Switch_ToDefault_WhileOnDefault_IsSilentNoOp()
    {
        var (service, kimai, notifier) = Create(SettingsWithTask());
        kimai.EnqueueStatus(Working with { ActiveIsDefaultTask = true });

        var response = await service.ClockAsync(SwitchRequest(null));

        Assert.Contains("laeuft bereits", response.Status!.StateText);
        Assert.Equal(0, kimai.StopCalls);
        Assert.Empty(notifier.Calls);
    }

    [Fact]
    public async Task Switch_ToDefault_WhileOnUnknownSheet_StartsDefaultTask()
    {
        // Working = a sheet that matches neither a task nor the default task
        // (e.g. its task was deleted): switching back must not be a no-op.
        var (service, kimai, _) = Create(SettingsWithTask());
        kimai.EnqueueStatus(Working);
        kimai.EnqueueStatus(Working with { ActiveIsDefaultTask = true });

        await service.ClockAsync(SwitchRequest(null));

        Assert.Equal(1, kimai.StopCalls);
        var target = Assert.Single(kimai.StartedTargets);
        Assert.Equal((1, 2), (target.ProjectId, target.ActivityId));
    }

    [Fact]
    public async Task PauseEnd_WithoutTasks_SkipsResumeLookup()
    {
        var (service, kimai, _) = Create();
        kimai.EnqueueStatus(Paused);
        kimai.EnqueueStatus(Working);

        await service.ClockAsync(Request("pauseEnd"));

        Assert.Equal(0, kimai.RecentStoppedCalls);
        Assert.Single(kimai.StartedTargets);
    }

    [Fact]
    public async Task PauseEnd_AfterDefaultWork_ResumesDefaultTask()
    {
        var (service, kimai, _) = Create(SettingsWithTask());
        kimai.LatestStopped = new KimaiRecentTimesheetDto(2, DateTimeOffset.UtcNow, 1);
        kimai.EnqueueStatus(Paused);
        kimai.EnqueueStatus(Working);

        await service.ClockAsync(Request("pauseEnd"));

        var target = Assert.Single(kimai.StartedTargets);
        Assert.Equal((1, 2), (target.ProjectId, target.ActivityId));
    }

    private sealed class RecordingNotifier : ITelegramNotifier
    {
        public List<(string DisplayName, string Action)> Calls { get; } = [];
        public List<string?> TaskLabels { get; } = [];

        public Task SendStampNotificationAsync(
            string employeeName, string action, DateTimeOffset stampUtc, TimeZoneInfo timeZone, string? taskLabel = null)
        {
            Calls.Add((employeeName, action));
            TaskLabels.Add(taskLabel);
            return Task.CompletedTask;
        }

        public Task<bool> SendMessageAsync(string text) => Task.FromResult(true);
    }

    private sealed class RecordingLogger : ILogger<ClockService>
    {
        public List<string> Messages { get; } = [];
        public List<Exception> Exceptions { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (exception is not null)
            {
                Exceptions.Add(exception);
            }
        }
    }

    private sealed class ScriptedKimaiClient : IKimaiClient
    {
        private readonly Queue<ClockStatusDto> _statuses = new();

        public bool TimezoneReturnsNull { get; set; }
        public bool TimezoneThrows { get; set; }
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
            if (TimezoneThrows)
            {
                // Transportfehler-Verhalten von KimaiClient: wirft statt null.
                return Task.FromException<string?>(new HttpRequestException("connection timeout"));
            }

            return Task.FromResult<string?>(TimezoneReturnsNull ? null : "Europe/Berlin");
        }

        /// <summary>Every started target in order (work, task and pause).</summary>
        public List<KimaiTimesheetTarget> StartedTargets { get; } = [];

        /// <summary>Latest stopped sheet GetRecentStoppedTimesheetsAsync answers (pauseEnd resume lookup).</summary>
        public KimaiRecentTimesheetDto? LatestStopped { get; set; }

        /// <summary>How often the resume lookup reached Kimai.</summary>
        public int RecentStoppedCalls { get; private set; }

        /// <summary>The next start calls fail with these exceptions, in order.</summary>
        public Queue<Exception> StartFailures { get; } = new();

        /// <summary>Every start call, failed ones included.</summary>
        public int StartAttempts { get; private set; }

        public Task StartAsync(RuntimeSettings s, EmployeeSettings e, KimaiTimesheetTarget t, CancellationToken ct = default)
        {
            StartAttempts++;
            if (StartFailures.TryDequeue(out var failure))
            {
                return Task.FromException(failure);
            }

            StartedTargets.Add(t);
            if (t.ActivityId == s.PauseActivityId)
            {
                StartPauseCalls++;
            }
            else
            {
                StartCalls++;
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(RuntimeSettings s, EmployeeSettings e, int timesheetId, CancellationToken ct = default)
        {
            StopCalls++;
            return Task.CompletedTask;
        }

        public Task StartAtAsync(RuntimeSettings s, EmployeeSettings e, KimaiTimesheetTarget t, DateTimeOffset d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StopAtAsync(RuntimeSettings s, EmployeeSettings e, int id, DateTimeOffset d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task BackdateEndAsync(RuntimeSettings s, EmployeeSettings e, int id, DateTimeOffset d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<KimaiRecentTimesheetDto>> GetRecentStoppedTimesheetsAsync(RuntimeSettings s, EmployeeSettings e, int count, CancellationToken ct = default)
        {
            RecentStoppedCalls++;
            return Task.FromResult<IReadOnlyList<KimaiRecentTimesheetDto>>(LatestStopped is null ? [] : [LatestStopped]);
        }
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
