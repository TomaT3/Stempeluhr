using Microsoft.Extensions.Logging.Abstractions;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

public sealed class WorkTimeAlertServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 14, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"stempeluhr-work-time-{Guid.NewGuid():N}");

    private string AlertPath => Path.Combine(_directory, "work-time-alerts.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task SendsEachWarningOnce_AlsoAfterARestart()
    {
        var kimai = new StubKimaiClient();
        kimai.Timesheets["max"] = [Running(Now.AddHours(-7))];
        var telegram = new RecordingNotifier();

        await CreateService(kimai, telegram).CheckAsync();
        await CreateService(kimai, telegram).CheckAsync();
        await CreateService(kimai, telegram, new WorkTimeAlertStore(AlertPath)).CheckAsync();

        var text = Assert.Single(telegram.Messages);
        Assert.Equal("⚠️ Max · über 6 Std. ohne Pause (ab 09:00, 7:00 Std.)", text);
    }

    [Theory]
    [InlineData(5)]         // block: offline entry replayed before the reported start
    [InlineData(7 * 60)]    // shift only: split shift, earlier part entered afterwards
    public async Task EntryAddedBeforeAReportedGroup_DoesNotRepeatTheWarning(int gapMinutes)
    {
        var kimai = new StubKimaiClient();
        var reportedStart = Now.AddHours(-10).AddMinutes(-30);
        kimai.Timesheets["max"] = [Running(reportedStart)];
        var telegram = new RecordingNotifier();
        var clock = new ManualClock(Now);
        var alerts = new WorkTimeAlertStore(AlertPath);

        await CreateService(kimai, telegram, alerts, clock: clock).CheckAsync();
        Assert.Equal(2, telegram.Messages.Count);

        var earlierEnd = reportedStart.AddMinutes(-gapMinutes);
        kimai.Timesheets["max"] =
        [
            new KimaiTimesheetEntryDto(2, earlierEnd.AddHours(-1), earlierEnd, 3600, 5),
            Running(reportedStart),
        ];
        clock.Now = Now.AddMinutes(5);
        await CreateService(kimai, telegram, new WorkTimeAlertStore(AlertPath), clock: clock).CheckAsync();

        Assert.Equal(2, telegram.Messages.Count);
    }

    [Fact]
    public async Task PauseReplayedIntoAWarnedBlock_WarnsTheNewBlockOnItsOwn()
    {
        // Terminal offline over noon: Kimai first shows work without a break
        // and warns; the replay then inserts the pause.
        var kimai = new StubKimaiClient();
        var start = Now.AddHours(-6).AddMinutes(-5); // 09:55 local
        kimai.Timesheets["max"] = [Running(start)];
        var telegram = new RecordingNotifier();
        var clock = new ManualClock(Now);
        var alerts = new WorkTimeAlertStore(AlertPath);
        async Task CheckAt(DateTimeOffset at)
        {
            clock.Now = at;
            await CreateService(kimai, telegram, alerts, clock: clock).CheckAsync();
        }

        await CheckAt(Now);
        Assert.Single(telegram.Messages);

        var resumed = start.AddHours(4).AddMinutes(30); // 14:25 local
        kimai.Timesheets["max"] =
        [
            new KimaiTimesheetEntryDto(1, start, start.AddHours(4), 4 * 3600, 5),
            new KimaiTimesheetEntryDto(2, start.AddHours(4), resumed, 1800, 99),
            Running(resumed),
        ];
        await CheckAt(Now.AddMinutes(5));
        await CheckAt(resumed.AddHours(6).AddMinutes(-1));
        Assert.Single(telegram.Messages);

        await CheckAt(resumed.AddHours(6).AddMinutes(1));
        Assert.Contains("⚠️ Max · über 6 Std. ohne Pause (ab 14:25, 6:01 Std.)", telegram.Messages);
    }

    [Fact]
    public async Task PauseReplayedAfterADelayedWarning_WarnsTheNewBlockRightAway()
    {
        // The first check only runs after an outage, 11 h into the shift.
        var kimai = new StubKimaiClient();
        var start = Now.AddHours(-11); // 05:00 local
        kimai.Timesheets["max"] = [Running(start)];
        var telegram = new RecordingNotifier();
        var clock = new ManualClock(Now);
        var alerts = new WorkTimeAlertStore(AlertPath);

        await CreateService(kimai, telegram, alerts, clock: clock).CheckAsync();
        Assert.Equal(2, telegram.Messages.Count); // continuous and shift

        var resumed = start.AddHours(4).AddMinutes(30); // 09:30 local
        kimai.Timesheets["max"] =
        [
            new KimaiTimesheetEntryDto(1, start, start.AddHours(4), 4 * 3600, 5),
            new KimaiTimesheetEntryDto(2, start.AddHours(4), resumed, 1800, 99),
            Running(resumed),
        ];
        clock.Now = Now.AddMinutes(5);
        await CreateService(kimai, telegram, alerts, clock: clock).CheckAsync();

        // The shift stays warned; only the split-off block is new.
        Assert.Equal(3, telegram.Messages.Count);
        Assert.Equal("⚠️ Max · über 6 Std. ohne Pause (ab 09:30, 6:35 Std.)", telegram.Messages[2]);
    }

    [Fact]
    public async Task ShiftWarningFollowsLater_AsItsOwnMessage()
    {
        var kimai = new StubKimaiClient();
        kimai.Timesheets["max"] = [Running(Now.AddHours(-7))];
        var telegram = new RecordingNotifier();
        var clock = new ManualClock(Now);
        var alerts = new WorkTimeAlertStore(AlertPath);

        await CreateService(kimai, telegram, alerts, clock: clock).CheckAsync();
        clock.Now = Now.AddHours(3).AddMinutes(1);
        await CreateService(kimai, telegram, alerts, clock: clock).CheckAsync();

        Assert.Equal(2, telegram.Messages.Count);
        Assert.StartsWith("⚠️ Max · über 10 Std. in der Schicht seit 30.09. 09:00", telegram.Messages[1]);
    }

    [Fact]
    public async Task FailedSend_IsRetriedInTheNextCheck()
    {
        var kimai = new StubKimaiClient();
        kimai.Timesheets["max"] = [Running(Now.AddHours(-7))];
        var telegram = new RecordingNotifier { Accepts = false };
        var alerts = new WorkTimeAlertStore(AlertPath);

        await CreateService(kimai, telegram, alerts).CheckAsync();
        Assert.Empty(alerts.Snapshot());

        telegram.Accepts = true;
        await CreateService(kimai, telegram, alerts).CheckAsync();

        Assert.Equal(2, telegram.Messages.Count);
        Assert.Single(alerts.Snapshot());
    }

    [Fact]
    public async Task WithoutTelegram_KimaiIsNotAsked()
    {
        var kimai = new StubKimaiClient();
        kimai.Timesheets["max"] = [Running(Now.AddHours(-7))];
        var telegram = new RecordingNotifier();

        await CreateService(kimai, telegram, settings: Settings(telegram: false)).CheckAsync();

        Assert.Empty(kimai.TimesheetCalls);
        Assert.Empty(telegram.Messages);
    }

    [Fact]
    public async Task DisabledEmployeesAndEmployeesWithoutToken_AreSkipped()
    {
        var kimai = new StubKimaiClient();
        var settings = Settings(extra:
        [
            new EmployeeSettings { Id = "off", DisplayName = "Off", ApiToken = "t", IsEnabled = false },
            new EmployeeSettings { Id = "tokenless", DisplayName = "Tokenless", ApiToken = "" },
        ]);

        await CreateService(kimai, new RecordingNotifier(), settings: settings).CheckAsync();

        Assert.Equal(["max"], kimai.TimesheetCalls);
    }

    [Fact]
    public async Task KimaiFailureForOneEmployee_DoesNotStopTheOthers()
    {
        var kimai = new StubKimaiClient();
        kimai.Failing.Add("max");
        kimai.Timesheets["anna"] = [Running(Now.AddHours(-7))];
        var telegram = new RecordingNotifier();
        var settings = Settings(extra: [new EmployeeSettings { Id = "anna", DisplayName = "Anna", ApiToken = "t" }]);

        await CreateService(kimai, telegram, settings: settings).CheckAsync();

        Assert.StartsWith("⚠️ Anna ·", Assert.Single(telegram.Messages));
    }

    [Fact]
    public async Task QueriesKimaiWithTheLocalLookbackWindow()
    {
        var kimai = new StubKimaiClient();

        await CreateService(kimai, new RecordingNotifier()).CheckAsync();

        // Europe/Berlin (CEST): naive bounds in the token owner's zone.
        Assert.Equal(new DateTime(2026, 9, 28, 16, 0, 0), kimai.LastBegin);
        Assert.Equal(new DateTime(2026, 9, 30, 16, 0, 0), kimai.LastEnd);
    }

    private WorkTimeAlertService CreateService(
        StubKimaiClient kimai,
        RecordingNotifier telegram,
        WorkTimeAlertStore? alerts = null,
        RuntimeSettings? settings = null,
        TimeProvider? clock = null) =>
        new(
            new StubSettingsStore(settings ?? Settings()),
            kimai,
            telegram,
            alerts ?? new WorkTimeAlertStore(AlertPath),
            NullLogger<WorkTimeAlertService>.Instance,
            clock ?? new ManualClock(Now));

    private static RuntimeSettings Settings(bool telegram = true, EmployeeSettings[]? extra = null) => new()
    {
        BaseUrl = "http://kimai.test",
        PauseActivityId = 99,
        TelegramBotToken = telegram ? "token" : null,
        TelegramChatId = telegram ? "-100" : null,
        Employees = [new EmployeeSettings { Id = "max", DisplayName = "Max", ApiToken = "t" }, .. extra ?? []],
    };

    private static KimaiTimesheetEntryDto Running(DateTimeOffset begin) => new(1, begin, null, 0, 5);

    private sealed class StubSettingsStore(RuntimeSettings settings) : IRuntimeSettingsStore
    {
        public RuntimeSettings Load() => settings;

        public Task SaveAsync(RuntimeSettings settings, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingNotifier : ITelegramNotifier
    {
        public bool Accepts { get; set; } = true;
        public List<string> Messages { get; } = [];

        public Task SendStampNotificationAsync(
            string employeeName, string action, DateTimeOffset stampUtc, TimeZoneInfo timeZone, string? taskLabel = null) =>
            Task.CompletedTask;

        public Task<bool> SendMessageAsync(string text)
        {
            Messages.Add(text);
            return Task.FromResult(Accepts);
        }

        public Task<bool> SendAlertAsync(string text) => throw new NotSupportedException();
    }

    private sealed class StubKimaiClient : IKimaiClient
    {
        public Task<KimaiTimesheetDetailDto?> GetTimesheetAsync(RuntimeSettings s, EmployeeSettings e, int id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> CreateTimesheetAsync(RuntimeSettings s, EmployeeSettings e, KimaiTimesheetTarget t, DateTimeOffset begin, DateTimeOffset end, string? description, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateTimesheetTimesAsync(RuntimeSettings s, EmployeeSettings e, int id, DateTimeOffset? begin, DateTimeOffset? end, CancellationToken ct = default) => throw new NotSupportedException();

        public Dictionary<string, KimaiTimesheetEntryDto[]> Timesheets { get; } = [];
        public HashSet<string> Failing { get; } = [];
        public List<string> TimesheetCalls { get; } = [];
        public DateTime LastBegin { get; private set; }
        public DateTime LastEnd { get; private set; }

        public Task<IReadOnlyCollection<KimaiTimesheetEntryDto>> GetTimesheetsAsync(
            RuntimeSettings settings, EmployeeSettings employee, DateTime begin, DateTime end, CancellationToken ct = default)
        {
            TimesheetCalls.Add(employee.Id);
            (LastBegin, LastEnd) = (begin, end);
            if (Failing.Contains(employee.Id))
            {
                return Task.FromException<IReadOnlyCollection<KimaiTimesheetEntryDto>>(new HttpRequestException("down"));
            }
            return Task.FromResult<IReadOnlyCollection<KimaiTimesheetEntryDto>>(
                Timesheets.TryGetValue(employee.Id, out var entries) ? entries : []);
        }

        public Task<string?> GetCurrentUserTimezoneAsync(RuntimeSettings s, EmployeeSettings e, CancellationToken ct = default) =>
            Task.FromResult<string?>("Europe/Berlin");

        public Task<ClockStatusDto> GetStatusAsync(RuntimeSettings s, EmployeeSettings e, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task StartAsync(RuntimeSettings s, EmployeeSettings e, KimaiTimesheetTarget t, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task StartAtAsync(RuntimeSettings s, EmployeeSettings e, KimaiTimesheetTarget t, DateTimeOffset at, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task StopAsync(RuntimeSettings s, EmployeeSettings e, int id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task StopAtAsync(RuntimeSettings s, EmployeeSettings e, int id, DateTimeOffset at, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task BackdateEndAsync(RuntimeSettings s, EmployeeSettings e, int id, DateTimeOffset at, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<KimaiRecentTimesheetDto>> GetRecentStoppedTimesheetsAsync(
            RuntimeSettings s, EmployeeSettings e, int count, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<KimaiUserDto>> GetUsersAsync(string baseUrl, string apiToken, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<KimaiActivityDto>> GetActivitiesAsync(string baseUrl, string apiToken, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<KimaiProjectDto>> GetProjectsAsync(string baseUrl, string apiToken, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
