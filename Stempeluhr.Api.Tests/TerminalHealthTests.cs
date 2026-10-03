using Microsoft.Extensions.Logging.Abstractions;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

public sealed class TerminalHealthTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Dictionary<TerminalCondition, DateTimeOffset> None = [];

    private static readonly TerminalHealthReport Healthy = new(
        AgentVersion: "0.18.0", AppVersion: "0.18.0", UiStatus: "alive", HeartbeatAgeSeconds: 5,
        Screen: "idle", Blocked: "none", Busy: false, Offline: false, Pending: 0, Rejected: 0,
        CpuPercent: 10, AvailableMemoryKb: 500_000, ChromiumRssSumKb: 200_000, TemperatureC: 50,
        ThrottledFlags: 0, DiskFreeMb: 10_000, UptimeSeconds: 86_400, Load1: 0.5);

    [Fact]
    public void Unreachable_OnlyAfterFiveMinutesWithoutReport()
    {
        var atLimit = TerminalHealthEvaluator.Evaluate(Healthy, Start, Start, Start.AddMinutes(5), None);
        var after = TerminalHealthEvaluator.Evaluate(Healthy, Start, Start, Start.AddMinutes(5).AddSeconds(1), None);

        Assert.Empty(atLimit);
        Assert.Equal(Start, after[TerminalCondition.Unreachable]);
    }

    [Fact]
    public void NeverReported_CountsFromTheServerStart()
    {
        Assert.Empty(TerminalHealthEvaluator.Evaluate(null, null, Start, Start.AddMinutes(5), None));
        var after = TerminalHealthEvaluator.Evaluate(null, null, Start, Start.AddMinutes(6), None);

        Assert.Equal(Start, after[TerminalCondition.Unreachable]);
    }

    [Fact]
    public void Temperature_NeedsItsHoldTimeAndClearsBelowTheLowerLimit()
    {
        var hot = Healthy with { TemperatureC = 82 };
        var present = TerminalHealthEvaluator.Evaluate(hot, Start, Start, Start, None);
        var since = present[TerminalCondition.Temperature];

        Assert.False(TerminalHealthEvaluator.IsAlarm(TerminalCondition.Temperature, since, Start.AddMinutes(4)));
        Assert.True(TerminalHealthEvaluator.IsAlarm(TerminalCondition.Temperature, since, Start.AddMinutes(5)));

        var warm = TerminalHealthEvaluator.Evaluate(Healthy with { TemperatureC = 77 }, Start, Start, Start.AddMinutes(1), present);
        Assert.Equal(since, warm[TerminalCondition.Temperature]);
        Assert.Empty(TerminalHealthEvaluator.Evaluate(Healthy with { TemperatureC = 74 }, Start, Start, Start.AddMinutes(1), warm));
        Assert.Empty(TerminalHealthEvaluator.Evaluate(Healthy with { TemperatureC = 77 }, Start, Start, Start, None));
    }

    [Theory]
    [InlineData(nameof(TerminalCondition.Memory))]
    [InlineData(nameof(TerminalCondition.Cpu))]
    [InlineData(nameof(TerminalCondition.Disk))]
    [InlineData(nameof(TerminalCondition.Power))]
    [InlineData(nameof(TerminalCondition.QueueBacklog))]
    public void CriticalValues_Trigger(string name)
    {
        var condition = Enum.Parse<TerminalCondition>(name);
        var report = condition switch
        {
            TerminalCondition.Memory => Healthy with { AvailableMemoryKb = 90 * 1024 },
            TerminalCondition.Cpu => Healthy with { CpuPercent = 95 },
            TerminalCondition.Disk => Healthy with { DiskFreeMb = 400 },
            TerminalCondition.Power => Healthy with { ThrottledFlags = 0x1 },
            _ => Healthy with { Pending = 2 },
        };

        Assert.Equal([condition], TerminalHealthEvaluator.Evaluate(report, Start, Start, Start, None).Keys);
    }

    [Fact]
    public void ThrottlingSinceBootOnly_IsNoAlarmButIsShown()
    {
        var report = Healthy with { ThrottledFlags = 0x50000 };

        Assert.Empty(TerminalHealthEvaluator.Evaluate(report, Start, Start, Start, None));
        Assert.Equal(
            ["Unterspannung (seit Start)", "gedrosselt (seit Start)"],
            TerminalHealthEvaluator.DescribeThrottling(report.ThrottledFlags));
    }

    [Fact]
    public void MissingValues_KeepThePreviousState()
    {
        var present = new Dictionary<TerminalCondition, DateTimeOffset> { [TerminalCondition.Cpu] = Start };
        var unknown = Healthy with { CpuPercent = null };

        Assert.Equal(Start, TerminalHealthEvaluator.Evaluate(unknown, Start, Start, Start.AddMinutes(1), present)[TerminalCondition.Cpu]);
        Assert.Empty(TerminalHealthEvaluator.Evaluate(unknown, Start, Start, Start.AddMinutes(1), None));
    }

    [Fact]
    public void PageHung_CountsFromTheLastHeartbeat()
    {
        var report = Healthy with { UiStatus = "missing", HeartbeatAgeSeconds = 90 };

        var present = TerminalHealthEvaluator.Evaluate(report, Start, Start, Start, None);

        Assert.Equal(Start.AddSeconds(-90), present[TerminalCondition.PageHung]);
    }

    [Fact]
    public async Task Unreachable_AlarmOnceAndAllClearWithRestartHint()
    {
        var (clock, store, telegram, watcher) = Create();
        store.Record("pi-01", Healthy);

        clock.Now = Start.AddMinutes(6);
        await watcher.CheckAsync();
        clock.Now = Start.AddMinutes(7);
        await watcher.CheckAsync();
        clock.Now = Start.AddMinutes(10);
        store.Record("pi-01", Healthy with { UptimeSeconds = 120 });
        await watcher.CheckAsync();
        await watcher.CheckAsync();

        Assert.Equal(
        [
            "🖥️ Terminal pi-01\n🔴 Meldet sich nicht (letzter Bericht 12:00). Pi hängt, ohne Strom oder ohne Netz.",
            "🖥️ Terminal pi-01\n✅ Wieder erreichbar (ohne Bericht 12:00–12:10, Pi wurde neu gestartet).",
        ], telegram.Alerts);
    }

    [Fact]
    public async Task AllClearAndNewProblem_GoOutAsOneMessage()
    {
        var (clock, store, telegram, watcher) = Create();
        store.Record("pi-01", Healthy);
        clock.Now = Start.AddMinutes(6);
        await watcher.CheckAsync();

        store.Record("pi-01", Healthy with { DiskFreeMb = 120, UptimeSeconds = 90_000 });
        await watcher.CheckAsync();

        Assert.Equal(
            "🖥️ Terminal pi-01\n✅ Wieder erreichbar (ohne Bericht 12:00–12:06, Pi lief durch: Netz oder Tailscale prüfen).\n"
            + "🔴 Nur 120 MB Speicherplatz frei.",
            telegram.Alerts[^1]);
    }

    [Fact]
    public async Task RejectedMessage_IsSentAgainOnTheNextCheck()
    {
        var (clock, store, telegram, watcher) = Create();
        store.Record("pi-01", Healthy with { DiskFreeMb = 120 });
        telegram.Accepts = false;
        await watcher.CheckAsync();
        telegram.Accepts = true;
        await watcher.CheckAsync();
        await watcher.CheckAsync();

        Assert.Equal(2, telegram.Alerts.Count);
        Assert.Equal(telegram.Alerts[0], telegram.Alerts[1]);
    }

    [Fact]
    public async Task OutdatedValues_RaiseNoAlarmWhileUnreachable()
    {
        var (clock, store, telegram, watcher) = Create();
        store.Record("pi-01", Healthy with { TemperatureC = 85 });
        await watcher.CheckAsync();

        clock.Now = Start.AddMinutes(10);
        await watcher.CheckAsync();

        var alert = Assert.Single(telegram.Alerts);
        Assert.DoesNotContain("Temperatur", alert);
        Assert.Equal([TerminalCondition.Unreachable], store.Snapshot("pi-01", clock.Now).Alarms.Keys);
    }

    [Fact]
    public async Task NeverReportedTerminal_IsReportedFiveMinutesAfterTheStart()
    {
        var (clock, _, telegram, watcher) = Create();
        clock.Now = Start.AddMinutes(5);
        await watcher.CheckAsync();
        clock.Now = Start.AddMinutes(6);
        await watcher.CheckAsync();

        Assert.Equal(["🖥️ Terminal pi-01\n🔴 Hat sich seit dem Serverstart (12:00) nicht gemeldet."], telegram.Alerts);
    }

    [Fact]
    public async Task WithoutTelegram_StateIsStillVisible()
    {
        var (clock, store, telegram, watcher) = Create(new RuntimeSettings { TerminalTokens = new() { ["pi-01"] = "token" } });
        store.Record("pi-01", Healthy with { DiskFreeMb = 120 });
        await watcher.CheckAsync();

        Assert.Empty(telegram.Alerts);
        var status = AdminTerminalStatusDto.From("pi-01", store.Snapshot("pi-01", clock.Now), clock.Now, TimeZoneInfo.Utc);
        Assert.Equal("problem", status.State);
        Assert.Equal("disk", Assert.Single(status.Problems).Kind);
    }

    [Fact]
    public async Task TerminalWithoutToken_IsIgnoredAndForgotten()
    {
        var (clock, store, telegram, watcher) = Create();
        store.Record("pi-01", Healthy);
        store.Record("old", Healthy with { DiskFreeMb = 120 });

        await watcher.CheckAsync();

        Assert.Empty(telegram.Alerts);
        Assert.Null(store.Snapshot("old", clock.Now).Report);
    }

    [Fact]
    public void AdminStatus_DistinguishesNeverOnlineAndUnreachable()
    {
        var store = new TerminalHealthStore(new ManualClock(Start));
        Assert.Equal("never", AdminTerminalStatusDto.From("pi-01", store.Snapshot("pi-01", Start), Start, TimeZoneInfo.Utc).State);
        var silent = Start.AddMinutes(6);
        Assert.Equal("unreachable", AdminTerminalStatusDto.From("pi-01", store.Snapshot("pi-01", silent), silent, TimeZoneInfo.Utc).State);

        store.Record("pi-01", Healthy);
        Assert.Equal("online", AdminTerminalStatusDto.From("pi-01", store.Snapshot("pi-01", Start), Start, TimeZoneInfo.Utc).State);

        var later = Start.AddMinutes(6);
        var status = AdminTerminalStatusDto.From("pi-01", store.Snapshot("pi-01", later), later, TimeZoneInfo.Utc);
        Assert.Equal("unreachable", status.State);
        Assert.Equal("unreachable", Assert.Single(status.Problems).Kind);
    }

    private static (ManualClock Clock, TerminalHealthStore Store, RecordingNotifier Telegram, TerminalHealthWatcher Watcher)
        Create(RuntimeSettings? settings = null)
    {
        var clock = new ManualClock(Start);
        var store = new TerminalHealthStore(clock);
        var telegram = new RecordingNotifier();
        settings ??= new RuntimeSettings
        {
            TelegramBotToken = "token",
            TelegramChatId = "-100",
            TerminalTokens = new() { ["pi-01"] = "secret" },
        };
        var watcher = new TerminalHealthWatcher(
            new StubSettingsStore(settings), store, telegram, NullLogger<TerminalHealthWatcher>.Instance,
            clock, TimeZoneInfo.Utc);
        return (clock, store, telegram, watcher);
    }

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
        public List<string> Alerts { get; } = [];

        public Task SendStampNotificationAsync(
            string employeeName, string action, DateTimeOffset stampUtc, TimeZoneInfo timeZone, string? taskLabel = null) =>
            throw new NotSupportedException();

        public Task<bool> SendMessageAsync(string text) => throw new NotSupportedException();

        public Task<bool> SendAlertAsync(string text)
        {
            Alerts.Add(text);
            return Task.FromResult(Accepts);
        }
    }
}
