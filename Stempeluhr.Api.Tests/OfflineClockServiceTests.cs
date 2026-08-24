using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

/// <summary>
/// Regression tests for the offline replay ordering semantics introduced by
/// the review round on 2026-08-24:
/// - a transient failure buffers the failed event AND everything after it
/// - the outbox replays strictly in scan order across flush rounds
///   (failed entries go back to the FRONT)
/// - a new batch never jumps over a backlog that is still in the outbox
///   (both sync paths)
/// - NFC and kiosk backlogs replay as ONE timeline in event-time order,
///   never "all NFC first, then all kiosk"
///
/// Note on the failure counters: every sync runs an opportunistic flush at
/// its end, so simulating an ongoing outage needs one failing status call
/// for the batch processing plus one for that trailing flush.
/// </summary>
public sealed class OfflineClockServiceTests
{
    private static readonly DateTimeOffset T08 = Parse("2026-08-24T08:00:00Z");
    private static readonly DateTimeOffset T10 = Parse("2026-08-24T10:00:00Z");
    private static readonly DateTimeOffset T12 = Parse("2026-08-24T12:00:00Z");

    [Fact]
    public async Task TransientFailure_BuffersWholeBatch_AndReplaysInScanOrder()
    {
        var (service, kimai) = CreateService();
        kimai.FailNextStatusCalls = 2;

        var result = await service.SyncKioskAsync(
        [
            Kiosk("e1", "start", T08),
            Kiosk("e2", "stop", T12),
        ]);

        // Nothing applied yet - both events buffered including the one AFTER
        // the failure (previously only the failed event was buffered and the
        // later stop could run against an outdated state).
        Assert.Equal(0, result.Accepted);
        Assert.Equal(2, result.Buffered);
        Assert.All(result.Results, r => Assert.Equal("buffered", r.Status));
        Assert.Empty(kimai.Operations);

        await service.FlushOutboxAsync();

        // Replay must arrive exactly in scan order.
        Assert.Equal(2, kimai.Operations.Count);
        Assert.Equal("start", kimai.Operations[0].Kind);
        Assert.Equal(T08, kimai.Operations[0].At);
        Assert.Equal("stop", kimai.Operations[1].Kind);
        Assert.Equal(T12, kimai.Operations[1].At);
        Assert.False(kimai.IsRunning);
    }

    [Fact]
    public async Task Flush_KeepsScanOrder_AcrossFailedRounds()
    {
        var (service, kimai) = CreateService();

        // Fill the outbox while Kimai is down (batch + trailing flush fail).
        kimai.FailNextStatusCalls = 2;
        await service.SyncKioskAsync([Kiosk("e1", "start", T08), Kiosk("e2", "stop", T12)]);
        Assert.Empty(kimai.Operations);

        // One more failing round: this flush must fail at e1 and keep it at
        // the FRONT of the outbox.
        kimai.FailNextStatusCalls = 1;
        await service.FlushOutboxAsync();
        Assert.Empty(kimai.Operations);

        // Next round: e1 then e2, in order. With a tail-reinsert regression
        // this would apply e2 first ("Lief nicht" no-op) and lose the stop.
        await service.FlushOutboxAsync();

        Assert.Equal(2, kimai.Operations.Count);
        Assert.Equal("start", kimai.Operations[0].Kind);
        Assert.Equal(T08, kimai.Operations[0].At);
        Assert.Equal("stop", kimai.Operations[1].Kind);
        Assert.Equal(T12, kimai.Operations[1].At);
        Assert.False(kimai.IsRunning);
    }

    [Fact]
    public async Task NewBatch_AfterRecovery_FlushesBacklogFirst_ThenAppliesInOrder()
    {
        var (service, kimai) = CreateService();

        // First request: Kimai down (processing + trailing flush fail), the
        // start event lands in the outbox.
        kimai.FailNextStatusCalls = 2;
        var first = await service.SyncKioskAsync([Kiosk("e1", "start", T08)]);
        Assert.Equal(1, first.Buffered);
        Assert.Empty(kimai.Operations);

        // Kimai recovered before the second request arrives: the sync's
        // leading flush drains the backlog first, so the stop is applied
        // against the freshly started timesheet instead of becoming a no-op.
        var second = await service.SyncKioskAsync([Kiosk("e2", "stop", T12)]);

        Assert.Equal(1, second.Accepted);
        Assert.Equal(0, second.Buffered);

        Assert.Equal(2, kimai.Operations.Count);
        Assert.Equal("start", kimai.Operations[0].Kind);
        Assert.Equal(T08, kimai.Operations[0].At);
        Assert.Equal("stop", kimai.Operations[1].Kind);
        Assert.Equal(T12, kimai.Operations[1].At);
        Assert.False(kimai.IsRunning);
    }

    [Fact]
    public async Task NewBatch_IsQueuedBehindBacklog_WhenKimaiStillDown()
    {
        var (service, kimai) = CreateService();

        // First request: Kimai down (processing + trailing flush fail).
        kimai.FailNextStatusCalls = 2;
        await service.SyncKioskAsync([Kiosk("e1", "start", T08)]);
        Assert.Empty(kimai.Operations);

        // Second request while Kimai is STILL down: its leading flush must
        // fail so the backlog survives - then the new stop event has to be
        // queued BEHIND it (buffered), never applied ahead of the start.
        kimai.FailNextStatusCalls = 1;
        var second = await service.SyncKioskAsync([Kiosk("e2", "stop", T12)]);

        Assert.Equal(0, second.Accepted);
        Assert.Equal(1, second.Buffered);
        Assert.Equal("buffered", second.Results.Single().Status);

        // The trailing flush of this very request applies everything in
        // scan order once the fake recovers.
        Assert.Equal(2, kimai.Operations.Count);
        Assert.Equal("start", kimai.Operations[0].Kind);
        Assert.Equal(T08, kimai.Operations[0].At);
        Assert.Equal("stop", kimai.Operations[1].Kind);
        Assert.Equal(T12, kimai.Operations[1].At);
        Assert.False(kimai.IsRunning);
    }

    [Fact]
    public async Task ResendWhileBuffered_DoesNotDuplicateOutboxEntries()
    {
        var (service, kimai, logger) = CreateServiceWithLogger();

        // First send: Kimai down (processing + trailing flush fail), both
        // events land in the outbox.
        kimai.FailNextStatusCalls = 2;
        var events = new[] { Kiosk("k1", "start", T08), Kiosk("k2", "stop", T12) };
        var first = await service.SyncKioskAsync(events);
        Assert.Equal(2, first.Buffered);
        Assert.Empty(kimai.Operations);

        // Safety-net re-send while Kimai is STILL down: buffering freed the
        // event IDs, so without physical dedup this would enqueue a second
        // COPY of each event - one more with every retry across the outage.
        kimai.FailNextStatusCalls = 1;
        var second = await service.SyncKioskAsync(events);

        Assert.Equal(0, second.Accepted);
        Assert.Equal(2, second.Buffered);
        Assert.All(second.Results, r => Assert.Equal("buffered", r.Status));

        // Recovery drains EXACTLY one copy per event: the flushed-count log
        // line says 2. A duplicate-copy regression would report 4 there (the
        // redundant entries are skipped via TryRegister but still counted).
        await service.FlushOutboxAsync();

        Assert.Equal(2, kimai.Operations.Count);
        Assert.Equal(("start", T08), kimai.Operations[0]);
        Assert.Equal(("stop", T12), kimai.Operations[1]);
        Assert.Contains(logger.Messages, m => m.Contains("Outbox flushed 2 offline event(s)"));
    }

    [Fact]
    public async Task NfcToggle_BuffersWholeTimeline_OnTransientFailure()
    {
        var (service, kimai) = CreateService();
        kimai.FailNextStatusCalls = 2;

        var result = await service.SyncAsync(
        [
            new OfflineNfcClockEventDto("n1", "04AB", "term", T08),
            new OfflineNfcClockEventDto("n2", "04AB", "term", T12),
        ]);

        Assert.Equal(0, result.Accepted);
        Assert.Equal(2, result.Buffered);

        await service.FlushOutboxAsync();

        // Both scans toggled in order: start at 08:00, stop at 12:00.
        Assert.Equal(2, kimai.Operations.Count);
        Assert.Equal("start", kimai.Operations[0].Kind);
        Assert.Equal(T08, kimai.Operations[0].At);
        Assert.Equal("stop", kimai.Operations[1].Kind);
        Assert.Equal(T12, kimai.Operations[1].At);
    }

    [Fact]
    public async Task NfcBatch_IsQueuedBehindNfcBacklog_WhenKimaiStillDown()
    {
        var (service, kimai) = CreateService();

        // Kimai down: the first scan lands in the outbox (processing +
        // trailing flush fail).
        kimai.FailNextStatusCalls = 2;
        await service.SyncAsync([new OfflineNfcClockEventDto("n1", "04AB", "term", T08)]);
        Assert.Empty(kimai.Operations);

        // Second batch while Kimai is STILL down: its leading flush must fail
        // so the backlog survives - then the new scan has to be queued BEHIND
        // it, never applied ahead of the earlier one.
        kimai.FailNextStatusCalls = 1;
        var second = await service.SyncAsync([new OfflineNfcClockEventDto("n2", "04AB", "term", T12)]);

        Assert.Equal(0, second.Accepted);
        Assert.Equal(1, second.Buffered);
        Assert.Equal("buffered", second.Results.Single().Status);

        // The trailing flush of this very request applies everything in
        // scan order once the fake recovers.
        Assert.Equal(2, kimai.Operations.Count);
        Assert.Equal("start", kimai.Operations[0].Kind);
        Assert.Equal(T08, kimai.Operations[0].At);
        Assert.Equal("stop", kimai.Operations[1].Kind);
        Assert.Equal(T12, kimai.Operations[1].At);
        Assert.False(kimai.IsRunning);
    }

    [Fact]
    public async Task MixedNfcAndKioskBacklog_ReplaysInEventOrder_AcrossBothQueues()    {
        var (service, kimai) = CreateService();

        // During the outage a kiosk START @08:00 lands in the kiosk outbox.
        kimai.FailNextStatusCalls = 2;
        await service.SyncKioskAsync([Kiosk("k1", "start", T08)]);
        Assert.Empty(kimai.Operations);

        // Later the same employee scans his NFC card @12:00; Kimai is still
        // down (leading flush fails), so the toggle is queued into the NFC
        // outbox while the start waits in the kiosk outbox.
        kimai.FailNextStatusCalls = 1;
        var second = await service.SyncAsync([new OfflineNfcClockEventDto("n1", "04AB", "term", T12)]);
        Assert.Equal(1, second.Buffered);

        // Recovery: the flush must replay strictly by event time ACROSS both
        // queues. Draining NFC-first would apply the toggle against the
        // not-yet-started state, derive "start" at 12:00 and turn the kiosk
        // start@08:00 into a "Lief bereits" no-op - losing the real stamp.
        await service.FlushOutboxAsync();

        Assert.Equal(2, kimai.Operations.Count);
        Assert.Equal("start", kimai.Operations[0].Kind);
        Assert.Equal(T08, kimai.Operations[0].At);
        Assert.Equal("stop", kimai.Operations[1].Kind);
        Assert.Equal(T12, kimai.Operations[1].At);
        Assert.False(kimai.IsRunning);
    }

    [Fact]
    public async Task NfcMultiCardBatch_BehindBacklog_ReplaysInGlobalScanOrder()
    {
        var (service, kimai) = CreateService();
        // Keep Kimai down for the whole REQUEST: one failing status per card
        // group plus the request's trailing flush. The leading flush costs
        // nothing here (the outboxes are still empty), and the explicit
        // FlushOutboxAsync below then runs against a recovered fake.
        kimai.FailNextStatusCalls = 3;

        var result = await service.SyncAsync(
        [
            new OfflineNfcClockEventDto("n1", "04AB", "term", T08),
            new OfflineNfcClockEventDto("n2", "04AB", "term", T12),
            new OfflineNfcClockEventDto("n3", "04CD", "term", T10),
        ]);

        Assert.Equal(0, result.Accepted);
        Assert.Equal(3, result.Buffered);

        await service.FlushOutboxAsync();

        // Global scan order across ALL cards (08 < 10 < 12), not flattened
        // group-by-group ([08, 12] then [10]) - the outbox lists must stay
        // individually chronological for the head-comparison merge.
        Assert.Equal(3, kimai.Operations.Count);
        Assert.Equal(("start", T08), kimai.Operations[0]);
        Assert.Equal(("stop", T10), kimai.Operations[1]);
        Assert.Equal(("start", T12), kimai.Operations[2]);
    }

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static OfflineKioskClockEventDto Kiosk(string eventId, string action, DateTimeOffset at) =>
        new(eventId, "max", "1234", action, at);

    private static (OfflineClockService Service, FakeKimaiClient Kimai) CreateService()
    {
        var (service, kimai, _) = CreateServiceWithLogger();
        return (service, kimai);
    }

    private static (OfflineClockService Service, FakeKimaiClient Kimai, RecordingLogger Logger) CreateServiceWithLogger()
    {
        var settings = new RuntimeSettings
        {
            BaseUrl = "http://kimai.test",
            DefaultProjectId = 1,
            DefaultActivityId = 1,
            Employees =
            [
                new EmployeeSettings
                {
                    Id = "max",
                    DisplayName = "Max Mustermann",
                    Pin = "1234",
                    NfcCardId = "04AB",
                    ApiToken = "token",
                    ProjectId = 7,
                    ActivityId = 9,
                },
                new EmployeeSettings
                {
                    Id = "anna",
                    DisplayName = "Anna Beispiel",
                    Pin = "5678",
                    NfcCardId = "04CD",
                    ApiToken = "token-anna",
                    ProjectId = 7,
                    ActivityId = 9,
                },
            ],
        };

        var kimai = new FakeKimaiClient();
        var logger = new RecordingLogger();
        var service = new OfflineClockService(
            new InMemorySettingsStore(settings),
            new InMemoryEmployeeService(),
            kimai,
            new InMemoryEventIdStore(),
            logger);

        return (service, kimai, logger);
    }

    /// <summary>
    /// Captures formatted log messages so tests can assert on operational
    /// side effects that are not visible through the public API (e.g. the
    /// outbox flushed-count distinguishes physical duplicate entries from
    /// deduplicated ones).
    /// </summary>
    private sealed class RecordingLogger : ILogger<OfflineClockService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class InMemorySettingsStore(RuntimeSettings settings) : IRuntimeSettingsStore
    {
        public RuntimeSettings Load() => settings;

        public Task SaveAsync(RuntimeSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class InMemoryEventIdStore : IOfflineEventIdStore
    {
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

        public bool TryRegister(string eventId) => _ids.Add(eventId);

        public void Remove(string eventId) => _ids.Remove(eventId);
    }

    private sealed class InMemoryEmployeeService : IEmployeeService
    {
        public IReadOnlyCollection<EmployeeDto> GetEnabledEmployees(RuntimeSettings settings) => [];

        public EmployeeSettings? FindEmployee(RuntimeSettings settings, ClockRequest request) =>
            settings.Employees.FirstOrDefault(employee =>
                employee.Id == request.EmployeeId && (request.Pin is null || employee.Pin == request.Pin));

        public EmployeeSettings? FindEmployeeByPin(RuntimeSettings settings, string? pin) => null;

        public EmployeeSettings? FindEmployeeByNfcCardId(RuntimeSettings settings, string? cardId) =>
            settings.Employees.FirstOrDefault(employee =>
                string.Equals(employee.NfcCardId, cardId, StringComparison.OrdinalIgnoreCase));

        public EmployeeDto ToEmployeeDto(EmployeeSettings employee) =>
            new(employee.Id, employee.DisplayName, string.Empty, employee.Color, null, !string.IsNullOrEmpty(employee.Pin));
    }

    /// <summary>
    /// Minimal Kimai fake: tracks start/stop operations in order and can fail
    /// the next N status calls with a transient network error to simulate an
    /// outage/recovery window.
    ///
    /// Simplification to keep in mind when reading multi-employee assertions:
    /// the timesheet state is GLOBAL across all employees (one shared active
    /// timesheet), unlike real Kimai where each employee has their own. In
    /// NfcMultiCardBatch_BehindBacklog_ReplaysInGlobalScanOrder this is why
    /// anna's scan@10:00 derives a "stop" after max's start@08:00 - in
    /// production it would be an independent per-employee toggle. The test
    /// pins the DRAIN ORDER (the timestamps); the action kinds merely follow
    /// from this shared-state simplification.
    /// </summary>
    private sealed class FakeKimaiClient : IKimaiClient
    {
        public List<(string Kind, DateTimeOffset At)> Operations { get; } = [];

        public int FailNextStatusCalls { get; set; }

        private int _timesheetCounter;
        private int? _activeTimesheetId;

        public bool IsRunning => _activeTimesheetId is not null;

        public Task<ClockStatusDto> GetStatusAsync(
            RuntimeSettings settings,
            EmployeeSettings employee,
            CancellationToken cancellationToken = default)
        {
            if (FailNextStatusCalls > 0)
            {
                FailNextStatusCalls--;
                throw new HttpRequestException("simulated Kimai outage");
            }

            var running = _activeTimesheetId is not null;
            return Task.FromResult(new ClockStatusDto(
                running,
                _activeTimesheetId,
                running ? "2026-08-24T00:00:00Z" : null,
                0,
                running ? "working" : "clockedOut",
                running ? "Eingestempelt" : "Nicht eingestempelt"));
        }

        public Task StartAtAsync(
            RuntimeSettings settings,
            EmployeeSettings employee,
            int projectId,
            int activityId,
            DateTimeOffset startedAt,
            CancellationToken cancellationToken = default)
        {
            Operations.Add(("start", startedAt));
            _activeTimesheetId = ++_timesheetCounter;
            return Task.CompletedTask;
        }

        public Task StopAtAsync(
            RuntimeSettings settings,
            EmployeeSettings employee,
            int timesheetId,
            DateTimeOffset stoppedAt,
            CancellationToken cancellationToken = default)
        {
            Operations.Add(("stop", stoppedAt));
            _activeTimesheetId = null;
            return Task.CompletedTask;
        }

        public Task StartAsync(RuntimeSettings settings, EmployeeSettings employee, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task StopAsync(RuntimeSettings settings, EmployeeSettings employee, int timesheetId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task StartPauseAsync(RuntimeSettings settings, EmployeeSettings employee, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<KimaiUserDto>> GetUsersAsync(string baseUrl, string apiToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<KimaiActivityDto>> GetActivitiesAsync(string baseUrl, string apiToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<KimaiProjectDto>> GetProjectsAsync(string baseUrl, string apiToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
