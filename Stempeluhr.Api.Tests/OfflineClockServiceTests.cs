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
/// - a pauseEnd or switch whose second step failed after the stop resumes on
///   the retry instead of dying as a no-op - but ONLY when this server
///   stopped that sheet for this very event and it is still Kimai's latest
///   stopped one (no phantom starts after stops on other terminals, issue
///   #55; a failed end backdate is repaired, issue #10)
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
    private static readonly DateTimeOffset T1230 = Parse("2026-08-24T12:30:00Z");

    [Fact]
    public async Task RejectedReplay_SendsOneSummaryPerBatch_AndThrottlesLaterBatches()
    {
        var (service, telegram, throttle) = CreateServiceWithTelegram();
        {
            var batch = await service.SyncKioskAsync([
                Kiosk("bad-1", "unknown", T08),
                Kiosk("bad-2", "unknown", T10)
            ]);
            Assert.All(batch.Results, result => Assert.Equal("rejected", result.Status));
            await throttle.ProcessPendingAsync();
            var text = Assert.Single(telegram.Messages);
            Assert.Contains("2 Offline-Stempel", text);
            Assert.Contains("Erster Fall", text);
            Assert.Contains("Letzter Fall", text);
            Assert.Contains("Max Mustermann", text);
            Assert.DoesNotContain("1234", text);

            await service.SyncKioskAsync([Kiosk("bad-3", "unknown", T12)]);
            await throttle.ProcessPendingAsync();
            Assert.Single(telegram.Messages);
            await service.SyncKioskAsync([Kiosk("bad-1", "unknown", T08)]);
            Assert.Single(telegram.Messages);
        }
    }

    [Fact]
    public async Task DisabledTelegram_AndUnknownEmployee_DoNotSend()
    {
        var disabledJournal = new RejectedOfflineEventStore(
            Path.Combine(Path.GetTempPath(), $"stempeluhr-disabled-{Guid.NewGuid():N}.json"));
        var (disabledService, disabledTelegram, disabledThrottle, disabledKimai) =
            CreateServiceWithTelegramAndKimai(enabled: false, existingJournal: disabledJournal);
        {
            Assert.Equal("rejected", Assert.Single((await disabledService.SyncKioskAsync([
                Kiosk("disabled", "unknown", T08)])).Results).Status);
            await disabledThrottle.ProcessPendingAsync();
            Assert.Empty(disabledTelegram.Messages);
            Assert.Equal(0, disabledKimai.TimezoneCalls);
            // Otherwise it would wait forever and surface once Telegram is enabled.
            Assert.False(disabledJournal.Find("disabled")!.TelegramEligible);
        }

        var (service, telegram, throttle) = CreateServiceWithTelegram();
        {
            Assert.Equal("rejected", Assert.Single((await service.SyncKioskAsync([
                Kiosk("unknown-employee", "start", T08) with { EmployeeId = "missing" }])).Results).Status);
            await throttle.ProcessPendingAsync();
            Assert.Empty(telegram.Messages);
        }
    }

    [Fact]
    public async Task RejectionDuringBackgroundFlush_SendsTelegram()
    {
        var (service, telegram, throttle, kimai) = CreateServiceWithTelegramAndKimai();
        {
            kimai.FailNextStatusCalls = 2;
            var entry = Kiosk("late-rejection", "start", T08);
            Assert.Equal("buffered", Assert.Single((await service.SyncKioskAsync([entry])).Results).Status);
            Assert.Empty(telegram.Messages);
            kimai.StartFailures.Enqueue(new KimaiApiException(System.Net.HttpStatusCode.BadRequest, "Kimai refused"));
            await service.FlushOutboxAsync();
            await throttle.ProcessPendingAsync();
            Assert.Contains("Kimai refused", Assert.Single(telegram.Messages));
        }
    }

    [Fact]
    public async Task WrongPinCannotGenerateTelegramWarning_ButCaseInsensitiveEmployeeCan()
    {
        var journal = new RejectedOfflineEventStore(Path.Combine(Path.GetTempPath(), $"stempeluhr-auth-{Guid.NewGuid():N}.json"));
        var (service, telegram, notifier, _) = CreateServiceWithTelegramAndKimai(existingJournal: journal);

        Assert.Equal("rejected", Assert.Single((await service.SyncKioskAsync([
            Kiosk("wrong-pin", "start", T08, "wrong")])).Results).Status);
        await notifier.ProcessPendingAsync();
        Assert.Empty(telegram.Messages);
        Assert.False(journal.Find("wrong-pin")!.TelegramEligible);

        Assert.Equal("rejected", Assert.Single((await service.SyncKioskAsync([
            Kiosk("mixed-case", "unknown", T10) with { EmployeeId = "MAX" }])).Results).Status);
        await notifier.ProcessPendingAsync();
        Assert.Contains("Max Mustermann", Assert.Single(telegram.Messages));
        Assert.True(journal.Find("mixed-case")!.TelegramEligible);
    }

    [Fact]
    public async Task PendingWarningsSurviveRestart_AndLaterSendIsChronological()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stempeluhr-restart-{Guid.NewGuid():N}.json");
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
        var journal = new RejectedOfflineEventStore(path);
        var (_, telegram, notifier, _) = CreateServiceWithTelegramAndKimai(existingJournal: journal, clock: clock);
        journal.Record(NotificationEntry("first", T10));
        await notifier.ProcessPendingAsync();
        Assert.Single(telegram.Messages);

        journal.Record(NotificationEntry("later", T12));
        journal.Record(NotificationEntry("earlier", T08));
        await notifier.ProcessPendingAsync();
        Assert.Single(telegram.Messages);

        // Reopen the journal and notifier as a restarted API would.
        var reopened = new RejectedOfflineEventStore(path);
        var (_, restartedTelegram, restartedNotifier, _) = CreateServiceWithTelegramAndKimai(
            existingJournal: reopened, clock: clock);
        clock.Advance(TimeSpan.FromMinutes(1));
        await restartedNotifier.ProcessPendingAsync();
        var summary = Assert.Single(restartedTelegram.Messages);
        Assert.Contains("2 Offline-Stempel", summary);
        Assert.Contains("Ausstempeln", summary);
        Assert.True(summary.IndexOf("Erster Fall", StringComparison.Ordinal)
            < summary.IndexOf("Letzter Fall", StringComparison.Ordinal));
        Assert.Contains("24.08. 10:00 Europe/Berlin", summary); // 08:00 UTC
        Assert.Contains("24.08. 14:00 Europe/Berlin", summary); // 12:00 UTC
        Assert.All(reopened.List(), entry => Assert.NotNull(entry.TelegramNotifiedAt));
        await restartedNotifier.ProcessPendingAsync();
        Assert.Single(restartedTelegram.Messages);
    }

    [Fact]
    public async Task FailedSendRemainsPending_AndDailyBudgetResetsAtUtcMidnight()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 29, 23, 59, 0, TimeSpan.Zero));
        var journal = new RejectedOfflineEventStore(Path.Combine(Path.GetTempPath(), $"stempeluhr-budget-{Guid.NewGuid():N}.json"));
        var (_, telegram, notifier, _) = CreateServiceWithTelegramAndKimai(existingJournal: journal, clock: clock);
        for (var i = 0; i < 20; i++)
        {
            journal.Record(NotificationEntry($"sent-{i}", T08) with
            {
                TelegramNotifiedAt = new DateTimeOffset(2026, 9, 29, 10, i, 0, TimeSpan.Zero)
            });
        }
        journal.Record(NotificationEntry("waiting", T10));
        await notifier.ProcessPendingAsync();
        Assert.Empty(telegram.Messages);
        clock.Advance(TimeSpan.FromMinutes(2));
        telegram.SendSucceeds = false;
        await notifier.ProcessPendingAsync();
        Assert.Single(telegram.Messages);
        Assert.Null(journal.Find("waiting")!.TelegramNotifiedAt);
        telegram.SendSucceeds = true;
        clock.Advance(TimeSpan.FromMinutes(1));
        await notifier.ProcessPendingAsync();
        Assert.Equal(2, telegram.Messages.Count);
        Assert.NotNull(journal.Find("waiting")!.TelegramNotifiedAt);
    }

    [Fact]
    public async Task AlreadyJournaledRefusalIsReported_WhenLaterBatchEntryThrows()
    {
        var journal = new RejectedOfflineEventStore(Path.Combine(Path.GetTempPath(), $"stempeluhr-abort-{Guid.NewGuid():N}.json"));
        var (service, telegram, notifier, _) = CreateServiceWithTelegramAndKimai(
            existingJournal: journal, eventIds: new FailOnSecondEventIdStore());
        await Assert.ThrowsAsync<IOException>(() => service.SyncKioskAsync([
            Kiosk("first-refusal", "unknown", T08),
            Kiosk("later-fails", "start", T10)
        ]));
        await notifier.ProcessPendingAsync();
        Assert.Contains("Max Mustermann", Assert.Single(telegram.Messages));
        Assert.NotNull(journal.Find("first-refusal")!.TelegramNotifiedAt);
    }

    [Fact]
    public async Task ResolvedRefusalIsNeverAnnounced()
    {
        var journal = new RejectedOfflineEventStore(Path.Combine(Path.GetTempPath(), $"stempeluhr-resolved-{Guid.NewGuid():N}.json"));
        var (_, telegram, notifier, _) = CreateServiceWithTelegramAndKimai(existingJournal: journal);
        journal.Record(NotificationEntry("already-entered", T08));
        journal.Resolve("already-entered");

        await notifier.ProcessPendingAsync();

        Assert.Empty(telegram.Messages);
    }

    [Fact]
    public async Task RefusalResolvedDuringTimezoneLookup_IsDroppedFromTheBatch()
    {
        var journal = new RejectedOfflineEventStore(Path.Combine(Path.GetTempPath(), $"stempeluhr-race-{Guid.NewGuid():N}.json"));
        var (_, telegram, notifier, kimai) = CreateServiceWithTelegramAndKimai(existingJournal: journal);
        journal.Record(NotificationEntry("entered-meanwhile", T08));
        journal.Record(NotificationEntry("still-open", T10));
        kimai.DuringTimezoneLookup = () => journal.Resolve("entered-meanwhile");

        await notifier.ProcessPendingAsync();

        var text = Assert.Single(telegram.Messages);
        Assert.StartsWith("⚠️ Offline-Stempel nicht übernommen", text);
        Assert.Contains("24.08. 12:00 Europe/Berlin", text); // still-open, 10:00 UTC
        Assert.DoesNotContain("24.08. 10:00", text);
        Assert.Null(journal.Find("entered-meanwhile")!.TelegramNotifiedAt);
        Assert.NotNull(journal.Find("still-open")!.TelegramNotifiedAt);
    }

    [Fact]
    public async Task TrimmingResolvedEntries_DoesNotFreeTheDailyTelegramLimit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stempeluhr-trim-{Guid.NewGuid():N}.json");
        // The journal trims against the real clock, so the notifier uses it too.
        var now = DateTimeOffset.UtcNow;
        var clock = new ManualClock(now);
        var entries = new List<RejectedOfflineEvent>();
        for (var i = 0; i < 20; i++)
        {
            // 20 sends today, resolved long ago relative to the newer entries below.
            entries.Add(NotificationEntry($"sent-{i}", T08) with
            {
                TelegramNotifiedAt = now.AddTicks(-(i + 1)),
                ResolvedAt = now.AddHours(-2)
            });
        }
        for (var i = 0; i < 1000; i++)
        {
            entries.Add(NotificationEntry($"resolved-{i}", T08) with { ResolvedAt = now.AddMinutes(-1) });
        }
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(entries,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));

        var journal = new RejectedOfflineEventStore(path);
        var (_, telegram, notifier, _) = CreateServiceWithTelegramAndKimai(existingJournal: journal, clock: clock);
        journal.Record(NotificationEntry("waiting", T10)); // trims to 1000 resolved entries
        clock.Advance(TimeSpan.FromMinutes(2));

        await notifier.ProcessPendingAsync();

        Assert.Empty(telegram.Messages);
    }

    [Fact]
    public async Task DeliveryThatCannotBeJournaled_IsNotResent_AndIsRecordedLater()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stempeluhr-readonly-{Guid.NewGuid():N}.json");
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
        var journal = new RejectedOfflineEventStore(path);
        var (_, telegram, notifier, _) = CreateServiceWithTelegramAndKimai(existingJournal: journal, clock: clock);
        journal.Record(NotificationEntry("sent-once", T08));

        // A directory in place of the temp file makes every Persist fail.
        Directory.CreateDirectory(path + ".tmp");
        await notifier.ProcessPendingAsync();
        Assert.Single(telegram.Messages);
        Assert.Null(journal.Find("sent-once")!.TelegramNotifiedAt);

        for (var round = 0; round < 3; round++)
        {
            clock.Advance(TimeSpan.FromMinutes(2));
            await notifier.ProcessPendingAsync();
        }
        Assert.Single(telegram.Messages);

        Directory.Delete(path + ".tmp");
        clock.Advance(TimeSpan.FromMinutes(2));
        await notifier.ProcessPendingAsync();
        Assert.Single(telegram.Messages);
        Assert.NotNull(journal.Find("sent-once")!.TelegramNotifiedAt);
    }

    [Fact]
    public async Task SendTimeFromTheFuture_DoesNotBlockWarnings()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
        var journal = new RejectedOfflineEventStore(Path.Combine(Path.GetTempPath(), $"stempeluhr-future-{Guid.NewGuid():N}.json"));
        var (_, telegram, notifier, _) = CreateServiceWithTelegramAndKimai(existingJournal: journal, clock: clock);
        journal.Record(NotificationEntry("sent-while-clock-was-fast", T08) with
        {
            TelegramNotifiedAt = new DateTimeOffset(2026, 9, 29, 13, 0, 0, TimeSpan.Zero)
        });
        journal.Record(NotificationEntry("waiting", T10));

        await notifier.ProcessPendingAsync();

        Assert.Single(telegram.Messages);
    }

    [Fact]
    public async Task FailedSendRetryPause_SurvivesWallClockStepBack()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
        var journal = new RejectedOfflineEventStore(Path.Combine(Path.GetTempPath(), $"stempeluhr-stepback-{Guid.NewGuid():N}.json"));
        var (_, telegram, notifier, _) = CreateServiceWithTelegramAndKimai(existingJournal: journal, clock: clock);
        journal.Record(NotificationEntry("waiting", T10));
        telegram.SendSucceeds = false;
        await notifier.ProcessPendingAsync();
        Assert.Single(telegram.Messages);

        telegram.SendSucceeds = true;
        clock.Set(new DateTimeOffset(2026, 9, 29, 7, 0, 0, TimeSpan.Zero));
        clock.Advance(TimeSpan.FromMinutes(1));
        await notifier.ProcessPendingAsync();

        Assert.Equal(2, telegram.Messages.Count);
        Assert.NotNull(journal.Find("waiting")!.TelegramNotifiedAt);
    }

    [Fact]
    public async Task KimaiTimezoneErrorResponse_FallsBackToUtc()
    {
        var journal = new RejectedOfflineEventStore(Path.Combine(Path.GetTempPath(), $"stempeluhr-utc-{Guid.NewGuid():N}.json"));
        var (_, telegram, notifier, kimai) = CreateServiceWithTelegramAndKimai(existingJournal: journal);
        kimai.TimezoneId = null;
        journal.Record(NotificationEntry("no-zone", T08));

        await notifier.ProcessPendingAsync();

        Assert.Contains("24.08. 08:00 UTC", Assert.Single(telegram.Messages));
    }

    [Fact]
    public async Task DisabledEmployeeOnAuthenticatedTerminal_DoesNotWarn()
    {
        var journal = new RejectedOfflineEventStore(Path.Combine(Path.GetTempPath(), $"stempeluhr-inactive-{Guid.NewGuid():N}.json"));
        var (service, telegram, notifier, _) = CreateServiceWithTelegramAndKimai(existingJournal: journal,
            extraEmployees: [new EmployeeSettings
            {
                Id = "former", DisplayName = "Frieda Früher", ApiToken = "token-former", IsEnabled = false
            }]);

        var result = await service.SyncKioskAsync([
            Kiosk("former-start", "start", T08, "") with { EmployeeId = "former", AuthenticatedTerminalId = "pi-1" }
        ]);
        await notifier.ProcessPendingAsync();

        Assert.Equal("rejected", Assert.Single(result.Results).Status);
        Assert.False(journal.Find("former-start")!.TelegramEligible);
        Assert.Empty(telegram.Messages);
    }

    private static RejectedOfflineEvent NotificationEntry(string id, DateTimeOffset performedAt) =>
        new(id, "max", "Max Mustermann", "STOP", performedAt, performedAt,
            "Kimai refused", TelegramEligible: true);

    [Fact]
    public async Task RemovedTerminalEmployeesDoNotContainOrReorderOtherEvents()
    {
        var (service, kimai) = CreateService();
        var result = await service.SyncKioskAsync([
            Kiosk("start", "start", T08, "") with { AuthenticatedTerminalId = "pi-1" },
            Kiosk("removed-1", "start", T10, "") with { EmployeeId = "removed", AuthenticatedTerminalId = "pi-1" },
            Kiosk("removed-2", "stop", T10.AddMinutes(1), "") with { EmployeeId = "removed", AuthenticatedTerminalId = "pi-1" },
            Kiosk("stop", "stop", T12, "") with { AuthenticatedTerminalId = "pi-1" }
        ]);
        Assert.Equal(new[] { "applied", "rejected", "rejected", "applied" }, result.Results.Select(r => r.Status));
        Assert.Equal(0, result.Buffered);
        Assert.Equal(new[] { "start", "stop" }, kimai.Operations.Select(op => op.Kind));
        Assert.Equal(T12, kimai.Operations.Last().At);
    }

    [Fact]
    public async Task LockedEmployeeEvents_StayBufferedWithoutBlockingOthers_AndReplayAfterTheLock()
    {
        var clock = new ManualClock(T08);
        var guard = new PinAttemptGuard(clock: clock);
        var (service, kimai, journal) = CreateServiceWithPinGuard(guard);
        for (var i = 0; i < PinAttemptGuard.EmployeeThreshold; i++) guard.RecordFailure(PinAttemptGuard.EmployeeKey("max"));

        var batch = new[]
        {
            Kiosk("max-start", "start", T08),
            Kiosk("anna-start", "start", T10, "5678") with { EmployeeId = "anna" },
            Kiosk("max-stop", "stop", T12),
        };
        var locked = await service.SyncKioskAsync(batch);

        Assert.Equal(new[] { "buffered", "applied", "buffered" }, locked.Results.Select(r => r.Status));
        Assert.Equal(2, locked.Buffered);
        Assert.Equal(new[] { ("start", T10) }, kimai.Operations);
        Assert.Empty(journal.List());

        clock.Advance(TimeSpan.FromMinutes(1));
        var retry = await service.SyncKioskAsync(batch);

        Assert.Equal(new[] { "applied", "duplicate", "applied" }, retry.Results.Select(r => r.Status));
        Assert.Equal(("stop", T12), kimai.Operations.Last());
    }

    [Fact]
    public async Task WrongPinReplays_CountTowardTheLock_ThenEventsAreBufferedInsteadOfRejected()
    {
        var guard = new PinAttemptGuard(clock: new ManualClock(T08));
        var (service, kimai, _) = CreateServiceWithPinGuard(guard);

        for (var i = 0; i < PinAttemptGuard.EmployeeThreshold; i++)
        {
            Assert.Equal("rejected", Assert.Single((await service.SyncKioskAsync([
                Kiosk($"wrong-{i}", "start", T08, "0000")])).Results).Status);
        }

        var right = await service.SyncKioskAsync([Kiosk("right", "start", T10)]);

        Assert.Equal("buffered", Assert.Single(right.Results).Status);
        Assert.Empty(kimai.Operations);
    }

    [Fact]
    public async Task OutboxEventsOfALockedEmployee_WaitWithoutHoldingUpOthers()
    {
        var clock = new ManualClock(T08);
        var guard = new PinAttemptGuard(clock: clock);
        var (service, kimai, _) = CreateServiceWithPinGuard(guard);
        kimai.FailNextStatusCalls = 2;
        var maxStart = Kiosk("max-start", "start", T08);
        Assert.Equal("buffered", Assert.Single((await service.SyncKioskAsync([maxStart])).Results).Status);
        for (var i = 0; i < PinAttemptGuard.EmployeeThreshold; i++) guard.RecordFailure(PinAttemptGuard.EmployeeKey("max"));

        // Queued behind the backlog, but applied by the flush right away.
        await service.SyncKioskAsync([Kiosk("anna-start", "start", T10, "5678") with { EmployeeId = "anna" }]);
        Assert.Equal(new[] { ("start", T10) }, kimai.Operations);
        await service.FlushOutboxAsync();
        Assert.Equal("buffered", Assert.Single((await service.SyncKioskAsync([maxStart])).Results).Status);

        clock.Advance(TimeSpan.FromMinutes(1));
        await service.FlushOutboxAsync();

        Assert.Equal("duplicate", Assert.Single((await service.SyncKioskAsync([maxStart])).Results).Status);
    }

    private static (OfflineClockService Service, FakeKimaiClient Kimai, RejectedOfflineEventStore Journal) CreateServiceWithPinGuard(
        PinAttemptGuard guard)
    {
        var kimai = new FakeKimaiClient();
        var journal = new RejectedOfflineEventStore(Path.Combine(Path.GetTempPath(), $"stempeluhr-pin-lock-{Guid.NewGuid():N}.json"));
        var service = new OfflineClockService(new InMemorySettingsStore(TestSettings()), new InMemoryEmployeeService(), kimai,
            new InMemoryEventIdStore(), new KioskEventCoordinator(), journal, new RecordingLogger(), pinAttempts: guard);
        return (service, kimai, journal);
    }

    [Fact]
    public async Task TerminalAuthenticationSurvivesOutboxAndReplaysInOrder()
    {
        var (service, kimai) = CreateService();
        kimai.FailNextStatusCalls = 2;
        var result = await service.SyncKioskAsync([
            Kiosk("terminal-stop", "stop", T12, "") with { AuthenticatedTerminalId = "pi-1" },
            Kiosk("terminal-start", "start", T08, "") with { AuthenticatedTerminalId = "pi-1" }
        ]);
        Assert.Equal(2, result.Buffered);
        await service.FlushOutboxAsync();
        Assert.Equal(new[] { "start", "stop" }, kimai.Operations.Select(op => op.Kind));
        Assert.False(kimai.IsRunning);
    }

    [Fact]
    public async Task AuthenticatedTerminalReplaysWithoutPinAndRemainsIdempotent()
    {
        var (service, kimai) = CreateService();
        var entry = Kiosk("terminal-event", "start", T08, "") with { AuthenticatedTerminalId = "pi-1" };
        var result = await service.SyncKioskAsync([entry]);
        Assert.Equal("applied", Assert.Single(result.Results).Status);
        Assert.Equal("duplicate", Assert.Single((await service.SyncKioskAsync([entry])).Results).Status);
    }

    [Fact]
    public async Task TerminalCannotReplayForRemovedEmployee()
    {
        var (service, _, _, settings) = CreateServiceWithSettings();
        settings.Employees.Clear();
        var entry = Kiosk("terminal-removed", "start", T08, "") with { AuthenticatedTerminalId = "pi-1" };
        Assert.Equal("rejected", Assert.Single((await service.SyncKioskAsync([entry])).Results).Status);
    }

    [Fact]
    public async Task RejectedReplay_IsSavedForAdminWithoutCredentials()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"stempeluhr-rejected-sync-{Guid.NewGuid():N}");
        try
        {
            var path = Path.Combine(directory, "rejected.json");
            var journal = new RejectedOfflineEventStore(path);
            var service = new OfflineClockService(new InMemorySettingsStore(TestSettings()),
                new InMemoryEmployeeService(), new FakeKimaiClient(), new InMemoryEventIdStore(),
                new KioskEventCoordinator(), journal, new RecordingLogger());

            var result = await service.SyncKioskAsync([Kiosk("rejected-1", "start", T08, "wrong-pin")]);

            Assert.Equal("rejected", Assert.Single(result.Results).Status);
            var entry = Assert.Single(new RejectedOfflineEventStore(path).List());
            Assert.Equal("rejected-1", entry.EventId);
            Assert.Equal(T08, entry.PerformedAt);
            Assert.DoesNotContain("wrong-pin", File.ReadAllText(path));

            var retry = await service.SyncKioskAsync([Kiosk("rejected-1", "start", T08, "wrong-pin")]);
            Assert.Equal("rejected", Assert.Single(retry.Results).Status);
            Assert.Equal(0, retry.Duplicates);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task JournalWriteFailure_DoesNotHideOtherRefusalsInTheBatch()
    {
        var blockingFile = Path.GetTempFileName();
        try
        {
            var journal = new RejectedOfflineEventStore(Path.Combine(blockingFile, "rejected.json"));
            var service = new OfflineClockService(new InMemorySettingsStore(TestSettings()),
                new InMemoryEmployeeService(), new FakeKimaiClient(), new InMemoryEventIdStore(),
                new KioskEventCoordinator(), journal, new RecordingLogger());

            var result = await service.SyncKioskAsync([
                Kiosk("rejected-1", "unknown", T08),
                Kiosk("rejected-2", "unknown", T10),
            ]);

            Assert.Equal(["rejected-1", "rejected-2"],
                result.Results.Where(item => item.Status == "rejected").Select(item => item.EventId));
        }
        finally
        {
            File.Delete(blockingFile);
        }
    }

    [Fact]
    public async Task MissingEventId_IsNotWrittenToAdminJournal()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"stempeluhr-invalid-journal-{Guid.NewGuid():N}");
        try
        {
            var journal = new RejectedOfflineEventStore(Path.Combine(directory, "rejected.json"));
            var service = new OfflineClockService(new InMemorySettingsStore(TestSettings()),
                new InMemoryEmployeeService(), new FakeKimaiClient(), new InMemoryEventIdStore(),
                new KioskEventCoordinator(), journal, new RecordingLogger());

            var result = await service.SyncKioskAsync([Kiosk("", "start", T08)]);

            Assert.Equal("rejected", Assert.Single(result.Results).Status);
            Assert.Empty(journal.List());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidAndUnknownEmployeeEvents_AreRejectedWithoutJournalEntry()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"stempeluhr-unknown-journal-{Guid.NewGuid():N}");
        try
        {
            var journal = new RejectedOfflineEventStore(Path.Combine(directory, "rejected.json"));
            var service = new OfflineClockService(new InMemorySettingsStore(TestSettings()),
                new InMemoryEmployeeService(), new FakeKimaiClient(), new InMemoryEventIdStore(),
                new KioskEventCoordinator(), journal, new RecordingLogger());
            var batch = Enumerable.Range(0, 50)
                .Select(i => Kiosk($"invalid-{i}", "start", T08) with { EmployeeId = "" })
                .Append(Kiosk("unknown-employee", "start", T10) with { EmployeeId = "missing" })
                .ToArray();

            var result = await service.SyncKioskAsync(batch);

            Assert.All(result.Results, item => Assert.Equal("rejected", item.Status));
            Assert.Empty(journal.List());
            Assert.False(File.Exists(Path.Combine(directory, "rejected.json")));

            // Not journaled, so the retry must get a fresh verdict, never "duplicate".
            var retry = await service.SyncKioskAsync(batch);
            Assert.All(retry.Results, item => Assert.Equal("rejected", item.Status));
            Assert.Equal(0, retry.Duplicates);
            Assert.Empty(journal.List());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OutboxRefusalForRemovedEmployee_IsNotJournaledAndStaysRejectedOnRetry()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"stempeluhr-outbox-removed-{Guid.NewGuid():N}");
        try
        {
            var journal = new RejectedOfflineEventStore(Path.Combine(directory, "rejected.json"));
            var settings = TestSettings();
            var kimai = new FakeKimaiClient { FailNextStatusCalls = 2 };
            var service = new OfflineClockService(new InMemorySettingsStore(settings),
                new InMemoryEmployeeService(), kimai, new InMemoryEventIdStore(),
                new KioskEventCoordinator(), journal, new RecordingLogger());
            var eventToReplay = Kiosk("outbox-removed", "start", T08, "") with { AuthenticatedTerminalId = "pi-1" };

            Assert.Equal("buffered", Assert.Single((await service.SyncKioskAsync([eventToReplay])).Results).Status);
            settings.Employees.Clear();
            await service.FlushOutboxAsync();

            Assert.Empty(journal.List());
            var retry = await service.SyncKioskAsync([eventToReplay]);
            Assert.Equal("rejected", Assert.Single(retry.Results).Status);
            Assert.Equal(0, retry.Duplicates);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OutboxPermanentRefusal_IsJournaledAndReportedOnKioskRetry()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"stempeluhr-outbox-journal-{Guid.NewGuid():N}");
        try
        {
            var journal = new RejectedOfflineEventStore(Path.Combine(directory, "rejected.json"));
            var kimai = new FakeKimaiClient { FailNextStatusCalls = 2 };
            var service = new OfflineClockService(new InMemorySettingsStore(TestSettings()),
                new InMemoryEmployeeService(), kimai, new InMemoryEventIdStore(),
                new KioskEventCoordinator(), journal, new RecordingLogger());
            var eventToReplay = Kiosk("outbox-1", "start", T08);

            var initial = await service.SyncKioskAsync([eventToReplay]);
            Assert.Equal("buffered", Assert.Single(initial.Results).Status);
            kimai.StartFailures.Enqueue(new KimaiApiException(System.Net.HttpStatusCode.BadRequest, "simulated permanent"));
            await service.FlushOutboxAsync();

            Assert.Equal("outbox-1", Assert.Single(journal.List()).EventId);
            var retry = await service.SyncKioskAsync([eventToReplay]);
            Assert.Equal("rejected", Assert.Single(retry.Results).Status);
            Assert.Equal(0, retry.Duplicates);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OutboxRefusalWithBrokenJournal_IsRetriedInsteadOfSilentlyDuplicated()
    {
        var blockingFile = Path.GetTempFileName();
        try
        {
            var journal = new RejectedOfflineEventStore(Path.Combine(blockingFile, "rejected.json"));
            var kimai = new FakeKimaiClient { FailNextStatusCalls = 2 };
            var service = new OfflineClockService(new InMemorySettingsStore(TestSettings()),
                new InMemoryEmployeeService(), kimai, new InMemoryEventIdStore(),
                new KioskEventCoordinator(), journal, new RecordingLogger());
            var eventToReplay = Kiosk("outbox-1", "start", T08);
            Assert.Equal("buffered", Assert.Single((await service.SyncKioskAsync([eventToReplay])).Results).Status);
            kimai.StartFailures.Enqueue(new KimaiApiException(System.Net.HttpStatusCode.BadRequest, "still rejected"));
            await service.FlushOutboxAsync();

            kimai.StartFailures.Enqueue(new KimaiApiException(System.Net.HttpStatusCode.BadRequest, "still rejected"));
            var retry = await service.SyncKioskAsync([eventToReplay]);

            Assert.Equal("rejected", Assert.Single(retry.Results).Status);
            Assert.Equal(0, retry.Duplicates);
        }
        finally
        {
            File.Delete(blockingFile);
        }
    }

    [Fact]
    public async Task DuplicateWithUnreadableJournal_WaitsForRecoveryBeforeReplying()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"stempeluhr-locked-duplicate-{Guid.NewGuid():N}");
        try
        {
            var path = Path.Combine(directory, "rejected.json");
            var eventToReplay = Kiosk("outbox-1", "start", T08);
            new RejectedOfflineEventStore(path).Record(new RejectedOfflineEvent(
                eventToReplay.EventId, eventToReplay.EmployeeId, "Max", eventToReplay.Action,
                eventToReplay.PerformedAt, DateTimeOffset.UtcNow, "abgelehnt"));
            var ids = new InMemoryEventIdStore();
            ids.TryRegister(eventToReplay.EventId);
            var service = new OfflineClockService(new InMemorySettingsStore(TestSettings()),
                new InMemoryEmployeeService(), new FakeKimaiClient(), ids,
                new KioskEventCoordinator(), new RejectedOfflineEventStore(path), new RecordingLogger());

            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var error = await Record.ExceptionAsync(() => service.SyncKioskAsync([eventToReplay]));
                Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
            }

            var retry = await service.SyncKioskAsync([eventToReplay]);
            Assert.Equal("rejected", Assert.Single(retry.Results).Status);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LaterJournalReadFailure_DoesNotTurnEarlierRefusalIntoDuplicate()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"stempeluhr-locked-batch-{Guid.NewGuid():N}");
        try
        {
            var path = Path.Combine(directory, "rejected.json");
            var duplicate = Kiosk("dup", "start", T10);
            new RejectedOfflineEventStore(path).Record(new RejectedOfflineEvent(
                duplicate.EventId, duplicate.EmployeeId, "Max", duplicate.Action,
                duplicate.PerformedAt, DateTimeOffset.UtcNow, "abgelehnt"));
            var ids = new InMemoryEventIdStore();
            ids.TryRegister(duplicate.EventId);
            var service = new OfflineClockService(new InMemorySettingsStore(TestSettings()),
                new InMemoryEmployeeService(), new FakeKimaiClient(), ids,
                new KioskEventCoordinator(), new RejectedOfflineEventStore(path), new RecordingLogger());
            var batch = new[] { Kiosk("refused", "unknown", T08), duplicate };

            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var error = await Record.ExceptionAsync(() => service.SyncKioskAsync(batch));
                Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
            }

            var retry = await service.SyncKioskAsync(batch);
            Assert.Equal("rejected", retry.Results.Single(result => result.EventId == "refused").Status);
            Assert.Equal("rejected", retry.Results.Single(result => result.EventId == "dup").Status);
            Assert.Equal(0, retry.Duplicates);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

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
    public async Task PauseEnd_TransientFailureAfterPauseStop_ResumesWorkOnRetry()
    {
        var (service, kimai) = CreateService();

        // Build up the timeline while Kimai is healthy: start, then a pause.
        await service.SyncKioskAsync([Kiosk("p1", "start", T08)]);
        await service.SyncKioskAsync([Kiosk("p2", "pauseStart", T12)]);
        Assert.True(kimai.IsRunning);
        Assert.True(kimai.ActiveIsPause);

        // pauseEnd is a two-step transaction (stop the pause timesheet, then
        // resume work). The resume-start fails transiently AFTER the pause
        // stop already succeeded - exactly the recovery window after an
        // outage. The event goes back into the outbox.
        kimai.FailNextStartCalls = 1;
        var failed = await service.SyncKioskAsync([Kiosk("p3", "pauseEnd", T1230)]);

        Assert.Equal(0, failed.Accepted);
        Assert.Equal(1, failed.Buffered);
        Assert.Equal("buffered", failed.Results.Single().Status);

        // Retry round: nothing is running anymore (the pause was already
        // stopped). Without the partial-application recovery this retry was
        // answered as a no-op and acknowledged as applied - leaving the
        // employee clocked out for the rest of the day.
        await service.FlushOutboxAsync();

        Assert.Equal(5, kimai.Operations.Count);
        Assert.Equal(("start", T08), kimai.Operations[0]);      // start work
        Assert.Equal(("stop", T12), kimai.Operations[1]);       // pauseStart: end work
        Assert.Equal(("start", T12), kimai.Operations[2]);      // pauseStart: open pause
        Assert.Equal(("stop", T1230), kimai.Operations[3]);     // pauseEnd: stop pause
        Assert.Equal(("start", T1230), kimai.Operations[4]);    // pauseEnd retry: resume work
        Assert.True(kimai.IsRunning);
        Assert.False(kimai.ActiveIsPause);
    }

    [Fact]
    public async Task PauseStart_WhilePauseAlreadyRuns_IsNoOp()
    {
        var (service, kimai) = CreateService();

        // The kiosk sent pauseStart live, the request timed out on the client
        // AFTER the server had applied it, so the same action is also queued.
        await service.SyncKioskAsync([Kiosk("s1", "start", T08)]);
        await service.SyncKioskAsync([Kiosk("s2", "pauseStart", T12)]);
        var operationsAfterPause = kimai.Operations.Count;

        // Replaying it must not stop the pause and open a second one.
        var result = await service.SyncKioskAsync([Kiosk("s3", "pauseStart", T12)]);

        Assert.Equal(1, result.Accepted);
        Assert.Equal("Pause lief bereits - kein Nachtrag noetig.", result.Results.Single().Message);
        Assert.Equal(operationsAfterPause, kimai.Operations.Count);
        Assert.True(kimai.ActiveIsPause);
    }

    [Fact]
    public async Task PauseEnd_LiveStopBeforeFlush_DoesNotPhantomStart()
    {
        var (service, kimai) = CreateService();

        // Live timeline while online: start, then a pause.
        await service.SyncKioskAsync([Kiosk("q1", "start", T08)]);
        await service.SyncKioskAsync([Kiosk("q2", "pauseStart", T12)]);

        // The network drops; the pauseEnd@12:30 is queued. Kimai comes back
        // and the employee presses STOP live at 13:00 - BEFORE the 15 s retry
        // timer flushes the queue. The live stop ends the PAUSE timesheet.
        var stopAt1300 = Parse("2026-08-24T13:00:00Z");
        await service.SyncKioskAsync([Kiosk("q3", "stop", stopAt1300)]);
        Assert.False(kimai.IsRunning);
        var operationsAfterLiveStop = kimai.Operations.Count;

        // Now the offline pauseEnd@12:30 replays against "nothing running".
        // The latest stopped timesheet ended at 13:00, NOT at the event time,
        // so this was no interrupted transaction: starting work@12:30 here
        // would book a phantom timesheet running until the next stamp.
        var result = await service.SyncKioskAsync([Kiosk("q4", "pauseEnd", T1230)]);

        Assert.Equal(1, result.Accepted);
        Assert.Equal("Keine laufende Pause - Nachtrag nicht moeglich.", result.Results.Single().Message);
        Assert.Equal(operationsAfterLiveStop, kimai.Operations.Count);
        Assert.False(kimai.IsRunning);
    }

    [Fact]
    public async Task PauseEnd_LiveStopWithinOldTolerance_DoesNotPhantomStart()
    {
        var (service, kimai) = CreateService();

        // Live timeline while online: start, then a pause.
        await service.SyncKioskAsync([Kiosk("r1", "start", T08)]);
        await service.SyncKioskAsync([Kiosk("r2", "pauseStart", T12)]);

        // The pauseEnd@12:30 is queued offline. Kimai comes back and the
        // employee presses STOP live at 12:31 - just 60 s after the queued
        // event's timestamp, INSIDE the old 120 s tolerance but OUTSIDE the
        // current 30 s one. This is a genuine live stop (the employee ended
        // the pause manually), NOT our interrupted transaction: resuming work
        // here would book a phantom timesheet.
        var stopAt1231 = Parse("2026-08-24T12:31:00Z");
        await service.SyncKioskAsync([Kiosk("r3", "stop", stopAt1231)]);
        Assert.False(kimai.IsRunning);
        var operationsAfterLiveStop = kimai.Operations.Count;

        var result = await service.SyncKioskAsync([Kiosk("r4", "pauseEnd", T1230)]);

        Assert.Equal(1, result.Accepted);
        Assert.Equal("Keine laufende Pause - Nachtrag nicht moeglich.", result.Results.Single().Message);
        Assert.Equal(operationsAfterLiveStop, kimai.Operations.Count);
        Assert.False(kimai.IsRunning);
    }

    [Fact]
    public async Task PauseEnd_ClockOutElsewhereRightAfterQueuedPauseEnd_IsRejectedWithoutPhantomStart()
    {
        // Issue #55: terminal A queued the pause end at 12:30:00, 20 s later
        // the employee clocked out on terminal B. Kimai then looks exactly
        // like a half-done pause end (latest stopped = pause ending at the
        // event), but this server never stopped it - resuming would book work
        // until the next morning.
        var (service, kimai) = CreateService();
        await service.SyncKioskAsync([Kiosk("u1", "start", T08), Kiosk("u2", "pauseStart", T12)]);
        kimai.SimulateLiveStop(T1230.AddSeconds(20));
        var before = kimai.Operations.Count;

        var result = await service.SyncKioskAsync([Kiosk("u3", "pauseEnd", T1230)]);

        var single = Assert.Single(result.Results);
        Assert.Equal("rejected", single.Status);
        Assert.Contains("nicht eindeutig", single.Message);
        Assert.Equal(before, kimai.Operations.Count);
        Assert.False(kimai.IsRunning);
    }

    // --- Live request half-booked, then queued (issue #67) --------------

    private static KioskClockRequest Live(string action, string? eventId, string? taskId = null) =>
        new("max", "1234", action, null, taskId, eventId);

    [Fact]
    public async Task PauseEnd_HalfBookedLive_ResumesOnReplayOfTheSameEvent()
    {
        // Live "Pause beenden": the pause stop went through, the start failed
        // (Kimai 5xx, network, kiosk timeout). The kiosk queues the pause end
        // under the event ID it already sent live - the marker proves this
        // server stopped the pause for exactly that event.
        var (live, replay, kimai, markers) = CreateLiveAndReplay();
        await replay.SyncKioskAsync([Kiosk("h1", "start", T08), Switch("h2", "kx", T10), Kiosk("h3", "pauseStart", T12)]);
        var pauseId = kimai.ActiveTimesheetId!.Value;
        kimai.StopWallClock = T1230.AddSeconds(2);
        kimai.FailNextStartCalls = 1;

        await Assert.ThrowsAsync<HttpRequestException>(() => live.ClockAsync(Live("pauseEnd", "h4")));
        Assert.False(kimai.IsRunning);
        Assert.True(markers.TryGet("h4", out var marked));
        Assert.Equal(pauseId, marked);

        var result = await replay.SyncKioskAsync([Kiosk("h4", "pauseEnd", T1230)]);

        Assert.Equal("applied", result.Results.Single().Status);
        // The live stop ended the pause at the server's clock: aligned with
        // the resume at the kiosk's timestamp - no gap, no overlap.
        Assert.Equal(T1230, kimai.EndOf(pauseId));
        Assert.Equal(("start", T1230), kimai.Operations[^1]);
        Assert.Equal((20, 21), (kimai.StartedTargets[^1].ProjectId, kimai.StartedTargets[^1].ActivityId));
        Assert.True(kimai.IsRunning);
        Assert.False(kimai.ActiveIsPause);
        Assert.False(markers.TryGet("h4", out _));
    }

    [Fact]
    public async Task PauseEnd_AppliedLiveButQueuedAfterTimeout_IsNoOp()
    {
        // Kimai took both steps, the answer came too late for the kiosk.
        var (live, replay, kimai, markers) = CreateLiveAndReplay();
        await replay.SyncKioskAsync([Kiosk("t1", "start", T08), Kiosk("t2", "pauseStart", T12)]);
        kimai.StopWallClock = T1230.AddSeconds(2);

        await live.ClockAsync(Live("pauseEnd", "t3"));
        Assert.False(markers.TryGet("t3", out _));
        var before = kimai.Operations.Count;

        var result = await replay.SyncKioskAsync([Kiosk("t3", "pauseEnd", T1230)]);

        Assert.Equal("applied", result.Results.Single().Status);
        Assert.Equal("Keine laufende Pause - Nachtrag nicht moeglich.", result.Results.Single().Message);
        Assert.Equal(before, kimai.Operations.Count);
        Assert.True(kimai.IsRunning);
        Assert.False(kimai.ActiveIsPause);
    }

    [Fact]
    public async Task PauseEnd_HalfBookedLiveWithoutEventId_IsRejectedLikeAStopElsewhere()
    {
        // An older kiosk sends no event ID with the live request: nothing
        // tells this half-done pause end apart from a clock-out elsewhere
        // (issue #55), so the replay rejects instead of guessing.
        var (live, replay, kimai, _) = CreateLiveAndReplay();
        await replay.SyncKioskAsync([Kiosk("o1", "start", T08), Kiosk("o2", "pauseStart", T12)]);
        kimai.StopWallClock = T1230.AddSeconds(2);
        kimai.FailNextStartCalls = 1;
        await Assert.ThrowsAsync<HttpRequestException>(() => live.ClockAsync(Live("pauseEnd", null)));
        var before = kimai.Operations.Count;

        var result = await replay.SyncKioskAsync([Kiosk("o3", "pauseEnd", T1230)]);

        var single = Assert.Single(result.Results);
        Assert.Equal("rejected", single.Status);
        Assert.Contains("nicht eindeutig", single.Message);
        Assert.Equal(before, kimai.Operations.Count);
        Assert.False(kimai.IsRunning);
    }

    [Fact]
    public async Task PauseEnd_RejectedLiveByKimai_SetsNoMarker()
    {
        // A 4xx is shown on the kiosk and never queued - nothing to resume.
        var (live, replay, kimai, markers) = CreateLiveAndReplay();
        await replay.SyncKioskAsync([Kiosk("k1", "start", T08), Kiosk("k2", "pauseStart", T12)]);
        kimai.StartFailures.Enqueue(new KimaiApiException(System.Net.HttpStatusCode.BadRequest, "simulated"));

        await Assert.ThrowsAsync<KimaiApiException>(() => live.ClockAsync(Live("pauseEnd", "k3")));

        Assert.False(markers.TryGet("k3", out _));
    }

    [Fact]
    public async Task Switch_HalfBookedLive_ResumesOnTargetOnReplayOfTheSameEvent()
    {
        var (live, replay, kimai, markers) = CreateLiveAndReplay();
        await replay.SyncKioskAsync([Kiosk("w1", "start", T08)]);
        var workId = kimai.ActiveTimesheetId!.Value;
        kimai.StopWallClock = T10.AddSeconds(2);
        kimai.StartFailures.Enqueue(new KimaiApiException(System.Net.HttpStatusCode.BadGateway, "simulated"));

        await Assert.ThrowsAsync<KimaiApiException>(() => live.ClockAsync(Live("switch", "w2", "kx")));
        Assert.True(markers.TryGet("w2", out _));

        var result = await replay.SyncKioskAsync([Switch("w2", "kx", T10)]);

        Assert.Equal("applied", result.Results.Single().Status);
        Assert.Equal(T10, kimai.EndOf(workId));
        Assert.Equal(("start", T10), kimai.Operations[^1]);
        Assert.Equal((20, 21), (kimai.StartedTargets[^1].ProjectId, kimai.StartedTargets[^1].ActivityId));
        Assert.True(kimai.IsRunning);
    }

    [Fact]
    public async Task Switch_AppliedLiveButQueuedAfterTimeout_IsNoOp()
    {
        var (live, replay, kimai, markers) = CreateLiveAndReplay();
        await replay.SyncKioskAsync([Kiosk("a1", "start", T08)]);
        kimai.StopWallClock = T10.AddSeconds(2);

        await live.ClockAsync(Live("switch", "a2", "kx"));
        Assert.False(markers.TryGet("a2", out _));
        var before = kimai.Operations.Count;

        var result = await replay.SyncKioskAsync([Switch("a2", "kx", T10)]);

        Assert.Equal("applied", result.Results.Single().Status);
        Assert.Contains("Lief bereits", result.Results.Single().Message);
        Assert.Equal(before, kimai.Operations.Count);
    }

    [Fact]
    public async Task Switch_HalfBookedLiveWithoutEventId_IsRejectedLikeAStopElsewhere()
    {
        var (live, replay, kimai, _) = CreateLiveAndReplay();
        await replay.SyncKioskAsync([Kiosk("b1", "start", T08)]);
        kimai.StopWallClock = T10.AddSeconds(2);
        kimai.FailNextStartCalls = 1;
        await Assert.ThrowsAsync<HttpRequestException>(() => live.ClockAsync(Live("switch", null, "kx")));
        var before = kimai.Operations.Count;

        var result = await replay.SyncKioskAsync([Switch("b2", "kx", T10)]);

        Assert.Equal("rejected", result.Results.Single().Status);
        Assert.Equal(before, kimai.Operations.Count);
        Assert.False(kimai.IsRunning);
    }

    // --- Replay while the live request of the same event still runs -----

    private static TaskCompletionSource<Exception?> Hold() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task PauseEnd_ReplayWhileLiveStartPending_WaitsAndResumesAfterItFailed()
    {
        // The kiosk gave up after 8 s and syncs; the API is still waiting for
        // Kimai's answer to the start (up to 15 s). The pause is already
        // stopped, the marker does not exist yet.
        var (live, replay, kimai, markers) = CreateLiveAndReplay();
        await replay.SyncKioskAsync([Kiosk("d1", "start", T08), Kiosk("d2", "pauseStart", T12)]);
        kimai.StopWallClock = T1230.AddSeconds(2);
        var start = Hold();
        kimai.HoldNextLiveStart = start;

        var liveRequest = live.ClockAsync(Live("pauseEnd", "d3"));
        Assert.False(kimai.IsRunning);
        var before = kimai.Operations.Count;

        var result = await replay.SyncKioskAsync([Kiosk("d3", "pauseEnd", T1230)]);

        // Neither rejected nor acknowledged as a no-op - it waits.
        Assert.Equal("buffered", result.Results.Single().Status);
        Assert.Equal(before, kimai.Operations.Count);

        start.SetResult(new HttpRequestException("simulated Kimai timeout"));
        await Assert.ThrowsAsync<HttpRequestException>(() => liveRequest);
        Assert.True(markers.TryGet("d3", out _));

        await replay.FlushOutboxAsync();

        Assert.Equal(("start", T1230), kimai.Operations[^1]);
        Assert.True(kimai.IsRunning);
        Assert.False(kimai.ActiveIsPause);
    }

    [Fact]
    public async Task PauseEnd_ReplayWhileLiveStartPending_IsNoOpAfterItWentThrough()
    {
        var (live, replay, kimai, markers) = CreateLiveAndReplay();
        await replay.SyncKioskAsync([Kiosk("g1", "start", T08), Kiosk("g2", "pauseStart", T12)]);
        kimai.StopWallClock = T1230.AddSeconds(2);
        var start = Hold();
        kimai.HoldNextLiveStart = start;

        var liveRequest = live.ClockAsync(Live("pauseEnd", "g3"));
        var result = await replay.SyncKioskAsync([Kiosk("g3", "pauseEnd", T1230)]);
        Assert.Equal("buffered", result.Results.Single().Status);

        start.SetResult(null);
        await liveRequest;
        var afterLive = kimai.Operations.Count;
        Assert.False(markers.IsLiveInFlight("g3"));

        await replay.FlushOutboxAsync();

        Assert.Equal(afterLive, kimai.Operations.Count);
        Assert.True(kimai.IsRunning);
        Assert.False(kimai.ActiveIsPause);
    }

    [Fact]
    public async Task Start_ReplayWhileLiveStartPending_DoesNotStartTwice()
    {
        // Not only transitions: a replayed start that ran while the live start
        // was still pending would open a second running sheet.
        var (live, replay, kimai, _) = CreateLiveAndReplay();
        kimai.StopWallClock = T08.AddSeconds(2);
        var start = Hold();
        kimai.HoldNextLiveStart = start;

        var liveRequest = live.ClockAsync(Live("start", "c1"));
        var result = await replay.SyncKioskAsync([Kiosk("c1", "start", T08)]);
        Assert.Equal("buffered", result.Results.Single().Status);
        Assert.Empty(kimai.Operations);

        start.SetResult(null);
        await liveRequest;
        await replay.FlushOutboxAsync();

        Assert.Single(kimai.StartedTargets);
        Assert.True(kimai.IsRunning);
    }

    [Fact]
    public async Task PauseEnd_RecoveryFailingAgain_StillResumesOnTheNextRetry()
    {
        var (service, kimai) = CreateService();
        await service.SyncKioskAsync([Kiosk("v1", "start", T08), Kiosk("v2", "pauseStart", T12)]);

        // Resume fails in the batch AND in the recovery of the trailing
        // flush: the marker of the interrupted pause end must survive both.
        kimai.FailNextStartCalls = 2;
        var failed = await service.SyncKioskAsync([Kiosk("v3", "pauseEnd", T1230)]);
        Assert.Equal("buffered", failed.Results.Single().Status);
        Assert.False(kimai.IsRunning);

        await service.FlushOutboxAsync();

        Assert.Equal(("start", T1230), kimai.Operations[^1]);
        Assert.True(kimai.IsRunning);
        Assert.False(kimai.ActiveIsPause);
    }

    [Fact]
    public async Task PauseEnd_InterruptedThenRestart_IsRejectedInsteadOfGuessed()
    {
        // The marker lives in memory. After a restart the kiosk re-sends the
        // buffered pause end; Kimai still shows the half-done state, but
        // nothing proves this server stopped the pause for this event.
        var (service, kimai) = CreateService();
        await service.SyncKioskAsync([Kiosk("w1", "start", T08), Kiosk("w2", "pauseStart", T12)]);
        kimai.FailNextStartCalls = 2;
        await service.SyncKioskAsync([Kiosk("w3", "pauseEnd", T1230)]);
        var before = kimai.Operations.Count;

        var (restarted, _, _, _) = CreateServiceWithSettings(kimai);
        var result = await restarted.SyncKioskAsync([Kiosk("w3", "pauseEnd", T1230)]);

        Assert.Equal("rejected", result.Results.Single().Status);
        Assert.Equal(before, kimai.Operations.Count);
        Assert.False(kimai.IsRunning);
    }

    [Fact]
    public async Task PauseEnd_InterruptedThenStampedElsewhere_IsRejected()
    {
        // Our replay stopped the pause, the resume failed. Before the retry
        // the employee clocked in and out on another terminal: the pause is
        // no longer the latest stopped sheet, so resuming from 12:30 would
        // overlap that newer work.
        var (service, kimai, logger) = CreateServiceWithLogger();
        await service.SyncKioskAsync([Kiosk("x1", "start", T08), Kiosk("x2", "pauseStart", T12)]);
        kimai.FailNextStartCalls = 2;
        await service.SyncKioskAsync([Kiosk("x3", "pauseEnd", T1230)]);
        kimai.SimulateLiveStart(Parse("2026-08-24T13:00:00Z"));
        kimai.SimulateLiveStop(Parse("2026-08-24T13:30:00Z"));
        var before = kimai.Operations.Count;

        await service.FlushOutboxAsync();

        Assert.Equal(before, kimai.Operations.Count);
        Assert.False(kimai.IsRunning);
        Assert.Contains(logger.Messages, m => m.Contains("dropping kiosk event x3"));
    }

    [Fact]
    public async Task PauseEnd_BackdateFailedAfterStop_RepairsTheEndAndResumes()
    {
        // Issue #10: the pause stop went through, its end backdate failed -
        // the pause keeps the wall clock of that attempt (18:00) as its end.
        // The old time window (end vs. event) no longer matched, the retry
        // became a no-op and the employee stayed clocked out.
        var (service, kimai) = CreateService();
        await service.SyncKioskAsync([Kiosk("y1", "start", T08), Kiosk("y2", "pauseStart", T12)]);
        var pauseId = kimai.ActiveTimesheetId!.Value;

        kimai.FailNextBackdateCalls = 1;
        var result = await service.SyncKioskAsync([Kiosk("y3", "pauseEnd", T1230)]);

        // Buffered by the batch, completed by its trailing flush.
        Assert.Equal("buffered", result.Results.Single().Status);
        Assert.Equal(T1230, kimai.EndOf(pauseId));
        Assert.Equal(("start", T1230), kimai.Operations[^1]);
        Assert.True(kimai.IsRunning);
        Assert.False(kimai.ActiveIsPause);
    }

    [Fact]
    public async Task Switch_BackdateFailedAfterStop_RepairsTheEndAndResumesOnTarget()
    {
        var (service, kimai) = CreateService();
        await service.SyncKioskAsync([Kiosk("s1", "start", T08)]);
        var workId = kimai.ActiveTimesheetId!.Value;

        kimai.FailNextBackdateCalls = 1;
        await service.SyncKioskAsync([Switch("s2", "kx", T10)]);

        Assert.Equal(T10, kimai.EndOf(workId));
        Assert.Equal((20, 21), (kimai.StartedTargets[^1].ProjectId, kimai.StartedTargets[^1].ActivityId));
        Assert.True(kimai.IsRunning);
    }

    [Fact]
    public async Task PauseEnd_NothingEverStopped_DoesNotResume()
    {
        var (service, kimai) = CreateService();

        // Fresh clockedOut state without any history: a replayed pauseEnd has
        // nothing to point at and must not start work on its own.
        var result = await service.SyncKioskAsync([Kiosk("z1", "pauseEnd", T1230)]);

        Assert.Equal(1, result.Accepted);
        Assert.Equal("Keine laufende Pause - Nachtrag nicht moeglich.", result.Results.Single().Message);
        Assert.Empty(kimai.Operations);
        Assert.False(kimai.IsRunning);
    }

    [Fact]
    public async Task PauseEnd_WithWorkAlreadyRunning_IsANoOp()
    {
        var (service, kimai) = CreateService();

        await service.SyncKioskAsync([Kiosk("w1", "start", T08)]);
        var operationsBefore = kimai.Operations.Count;

        // Work running but no pause (the transition completed some other way):
        // a replayed pauseEnd must not touch anything.
        var result = await service.SyncKioskAsync([Kiosk("w2", "pauseEnd", T1230)]);

        Assert.Equal(1, result.Accepted);
        Assert.Equal("Keine laufende Pause - Nachtrag nicht moeglich.", result.Results.Single().Message);
        Assert.Equal(operationsBefore, kimai.Operations.Count);
    }

    [Fact]
    public async Task StaleQueuedStop_DoesNotKillNewerLiveTimesheet()
    {
        var (service, kimai) = CreateService();

        // Outage: the stop@08:00 lands in the outbox (batch processing plus
        // the trailing flush each burn one failing status call).
        kimai.FailNextStatusCalls = 2;
        var queued = await service.SyncKioskAsync([Kiosk("s1", "stop", T08)]);
        Assert.Equal(1, queued.Buffered);
        Assert.Empty(kimai.Operations);

        // Recovery - and the employee stamps IN LIVE at 12:00 via the live
        // endpoint path (which does not share the sync lock). The outbox now
        // holds a stop OLDER than the running sheet's begin.
        kimai.SimulateLiveStart(T12);

        // Without the stale-event guard this replay derived a stop for the
        // NEW sheet and backdated it to 08:00 - before its own begin (Kimai
        // would reject that with end < begin and the stamp was lost).
        await service.FlushOutboxAsync();

        var op = Assert.Single(kimai.Operations);
        Assert.Equal(("start", T12), op);
        Assert.True(kimai.IsRunning);
    }

    [Fact]
    public async Task StaleQueuedPauseStart_DoesNotTruncateNewerLiveTimesheet()
    {
        var (service, kimai) = CreateService();

        kimai.FailNextStatusCalls = 2;
        var queued = await service.SyncKioskAsync([Kiosk("p1", "pauseStart", T08)]);
        Assert.Equal(1, queued.Buffered);

        // Live recovery: work starts at 12:00, THEN the stale pauseStart@08:00
        // replays. Pausing that newer sheet would backdate its end to 08:00 -
        // four hours of real work silently gone.
        kimai.SimulateLiveStart(T12);

        await service.FlushOutboxAsync();

        var op = Assert.Single(kimai.Operations);
        Assert.Equal(("start", T12), op);
        Assert.True(kimai.IsRunning);
        Assert.False(kimai.ActiveIsPause);
    }

    [Fact]
    public async Task WrongPin_RejectsOnlyFirstEvent_RestOfBatchStaysQueued()
    {
        var (service, kimai) = CreateService();

        // One batch, two events with DIFFERENT credentials' outcomes: b1 has
        // a wrong PIN, b2 is correct. Answering both in ONE request would let
        // an attacker harvest one PIN verdict per event (20 req x 100 events
        // = 2,000 guesses/min); aborting at the first rejection caps ONE
        // request at exactly ONE verdict.
        var result = await service.SyncKioskAsync(
        [
            Kiosk("b1", "start", T08, pin: "0000"),
            Kiosk("b2", "start", T12),
        ]);

        Assert.Collection(
            result.Results,
            r =>
            {
                Assert.Equal("b1", r.EventId);
                Assert.Equal("rejected", r.Status);
                Assert.Equal("Mitarbeiter nicht gefunden oder PIN falsch.", r.Message);
            },
            r =>
            {
                Assert.Equal("b2", r.EventId);
                Assert.Equal("buffered", r.Status);
            });
        Assert.Empty(kimai.Operations);

        // The retained event drains on the NEXT round (still one verdict per
        // round) - a legitimate backlog survives instead of being mass-dropped.
        var retry = await service.SyncKioskAsync([Kiosk("b2", "start", T08)]);

        Assert.Equal(1, retry.Accepted);
        var op = Assert.Single(kimai.Operations);
        Assert.Equal(("start", T08), op);
        Assert.True(kimai.IsRunning);
    }

    [Fact]
    public async Task KioskReplay_WithNfcCardId_AppliesWithoutPin()
    {
        var (service, kimai) = CreateService();

        // NFC-unlocked session whose PIN was never entered: the replay must
        // resolve the employee by the card, mirroring the live kiosk path.
        var result = await service.SyncKioskAsync(
        [
            new OfflineKioskClockEventDto("n1", "max", null, "start", T08, NfcCardId: "04AB"),
        ]);

        Assert.Equal(1, result.Accepted);
        Assert.Equal(("start", T08), Assert.Single(kimai.Operations));
        Assert.True(kimai.IsRunning);
    }

    [Fact]
    public async Task KioskReplay_NfcCardOfAnotherEmployee_IsRejected()
    {
        var (service, kimai) = CreateService();

        // Anna's session id paired with MAX's card: the live path accepts a
        // card only when it maps to the SAME employee id - the replay must
        // never become a way around that check.
        var result = await service.SyncKioskAsync(
        [
            new OfflineKioskClockEventDto("n1", "anna", null, "start", T08, NfcCardId: "04AB"),
        ]);

        var single = Assert.Single(result.Results);
        Assert.Equal("rejected", single.Status);
        Assert.Empty(kimai.Operations);
        Assert.False(kimai.IsRunning);
    }

    [Fact]
    public async Task TransientStartFailure_BuffersEventInsteadOfRejecting()
    {
        // Service-level pin for the begin-backdate re-throw contract: a
        // TRANSIENT failure inside StartAtAsync (e.g. the old-Kimai fallback
        // whose begin-backdate keeps failing after the compensating stop)
        // must surface as "buffered", never as "rejected" - otherwise the
        // whole offline session would be lost silently.
        var (service, kimai) = CreateService();
        kimai.FailNextStartCalls = 2; // batch processing + trailing flush

        var result = await service.SyncKioskAsync([Kiosk("e1", "start", T08)]);

        Assert.Equal(0, result.Accepted);
        var single = Assert.Single(result.Results);
        Assert.Equal("buffered", single.Status);
        Assert.Empty(kimai.Operations);

        // Once Kimai recovers, the buffered event replays cleanly.
        await service.FlushOutboxAsync();

        var op = Assert.Single(kimai.Operations);
        Assert.Equal("start", op.Kind);
        Assert.Equal(T08, op.At);
        Assert.True(kimai.IsRunning);
    }

    [Fact]
    public async Task Switch_Replay_MovesWorkToTaskAndBack()
    {
        var (service, kimai) = CreateService();

        var result = await service.SyncKioskAsync(
        [
            Kiosk("e1", "start", T08),
            Switch("e2", "kx", T10),
            Switch("e3", null, T12),
            Kiosk("e4", "stop", T1230),
        ]);

        Assert.Equal(4, result.Accepted);
        Assert.Equal(
        [
            ("start", T08),
            ("stop", T10), ("start", T10),
            ("stop", T12), ("start", T12),
            ("stop", T1230),
        ], kimai.Operations);
        Assert.Equal([(7, 9), (20, 21), (7, 9)], kimai.StartedTargets.Select(t => (t.ProjectId, t.ActivityId)));
        Assert.Equal("Kunde X", kimai.StartedTargets[1].Description);
        Assert.False(kimai.IsRunning);
    }

    [Fact]
    public async Task Start_Replay_BooksTheChosenTask()
    {
        var (service, kimai) = CreateService();

        var result = await service.SyncKioskAsync(
        [
            StartOn("e1", "kx", T08),
            Kiosk("e2", "stop", T12),
        ]);

        Assert.Equal(2, result.Accepted);
        Assert.Equal([("start", T08), ("stop", T12)], kimai.Operations);
        var target = Assert.Single(kimai.StartedTargets);
        Assert.Equal((20, 21, "Kunde X"), (target.ProjectId, target.ActivityId, target.Description));
    }

    [Fact]
    public async Task Start_OnTask_AppliedLiveAndQueued_IsNoOp()
    {
        var (service, kimai) = CreateService();
        await service.SyncKioskAsync([StartOn("e1", "kx", T08)]);

        var result = await service.SyncKioskAsync([StartOn("e2", "kx", T08)]);

        Assert.Equal("applied", result.Results.Single().Status);
        Assert.Contains("Lief bereits", result.Results.Single().Message);
        Assert.DoesNotContain("anderen", result.Results.Single().Message);
        Assert.Single(kimai.Operations);
    }

    [Fact]
    public async Task Start_OnTask_WhileAnotherTaskRuns_IsNoOpButLogged()
    {
        // Clocked in on the main task at another terminal meanwhile: the
        // replay must not switch (a start is no switch), but the lost choice
        // has to be findable for whoever corrects the time per customer.
        var (service, kimai, logger) = CreateServiceWithLogger();
        await service.SyncKioskAsync([Kiosk("e1", "start", T08)]);

        var result = await service.SyncKioskAsync([StartOn("e2", "kx", T10)]);

        var single = Assert.Single(result.Results);
        Assert.Equal("applied", single.Status);
        Assert.Contains("Lief bereits auf einer anderen Taetigkeit", single.Message);
        Assert.Single(kimai.Operations);
        Assert.Contains(logger.Messages, message => message.Contains("task choice is dropped"));
    }

    [Fact]
    public async Task Start_OnDeletedTask_KeepsTheWorkingTimeOnTheDefaultTask()
    {
        // Rejecting would lose the whole time until the next stamp: book the
        // default task instead. Nobody reads the replay message of an applied
        // event, so the note goes onto the timesheet itself.
        var (service, kimai, logger) = CreateServiceWithLogger();

        var result = await service.SyncKioskAsync([StartOn("e1", "gone", T08)]);

        var single = Assert.Single(result.Results);
        Assert.Equal("applied", single.Status);
        Assert.Contains("gelöscht", single.Message);
        var target = Assert.Single(kimai.StartedTargets);
        Assert.Equal((7, 9), (target.ProjectId, target.ActivityId));
        Assert.Contains("gelöscht", target.Description);
        Assert.Contains(logger.Messages, message => message.Contains("no longer exists"));
    }

    [Fact]
    public async Task Switch_AlreadyOnTarget_IsNoOp()
    {
        // The switch was applied live and ALSO queued (request timed out on
        // the kiosk): the replay must not book a zero-length sheet.
        var (service, kimai) = CreateService();
        await service.SyncKioskAsync([Kiosk("e1", "start", T08), Switch("e2", "kx", T10)]);
        var before = kimai.Operations.Count;

        var result = await service.SyncKioskAsync([Switch("e3", "kx", T10)]);

        Assert.Equal("applied", result.Results.Single().Status);
        Assert.Contains("Lief bereits", result.Results.Single().Message);
        Assert.Equal(before, kimai.Operations.Count);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("clockedOut")]
    public async Task Switch_WithoutRunningWork_IsNoOp(string state)
    {
        var (service, kimai) = CreateService();
        IReadOnlyList<OfflineKioskClockEventDto> setup = state == "pause"
            ? [Kiosk("e1", "start", T08), Kiosk("e2", "pauseStart", T10)]
            : [Kiosk("e1", "start", T08), Kiosk("e2", "stop", T10)];
        await service.SyncKioskAsync(setup);
        var before = kimai.Operations.Count;

        var result = await service.SyncKioskAsync([Switch("e3", "kx", T12)]);

        Assert.Equal("applied", result.Results.Single().Status);
        Assert.Contains("nicht nachtragbar", result.Results.Single().Message);
        Assert.Equal(before, kimai.Operations.Count);
    }

    [Fact]
    public async Task Switch_SheetNewerThanEvent_IsObsoleteNoOp()
    {
        var (service, kimai) = CreateService();
        kimai.SimulateLiveStart(T12);

        var result = await service.SyncKioskAsync([Switch("e1", "kx", T10)]);

        Assert.Contains("Veraltet", result.Results.Single().Message);
        Assert.Single(kimai.Operations);
    }

    [Fact]
    public async Task Switch_ToDeletedTask_IsRejected()
    {
        var (service, kimai) = CreateService();
        await service.SyncKioskAsync([Kiosk("e1", "start", T08)]);

        var result = await service.SyncKioskAsync([Switch("e2", "gone", T10)]);

        var single = Assert.Single(result.Results);
        Assert.Equal("rejected", single.Status);
        Assert.Contains("nicht mehr vorhanden", single.Message);
        Assert.Single(kimai.Operations);
    }

    [Fact]
    public async Task Switch_InterruptedAfterStop_ResumesOnTargetInsteadOfClockingOut()
    {
        var (service, kimai) = CreateService();
        await service.SyncKioskAsync([Kiosk("e1", "start", T08)]);

        // Stop succeeds, the start on the task fails transiently: the event
        // buffers and the trailing flush sees NOTHING running. Without the
        // recovery the retry would be a "Lief nicht" no-op and the employee
        // would stay clocked out.
        kimai.FailNextStartCalls = 1;
        await service.SyncKioskAsync([Switch("e2", "kx", T10)]);
        await service.FlushOutboxAsync();

        Assert.Equal([("start", T08), ("stop", T10), ("start", T10)], kimai.Operations);
        Assert.Equal((20, 21), (kimai.StartedTargets[^1].ProjectId, kimai.StartedTargets[^1].ActivityId));
        Assert.True(kimai.IsRunning);
    }

    [Fact]
    public async Task Switch_ClockOutElsewhereRightAfterQueuedSwitch_IsRejectedWithoutPhantomStart()
    {
        // Terminal A queued "switch to Kunde X" at 10:00:00; 20 s later the
        // employee clocked out on terminal B. Kimai then looks exactly like a
        // half-done switch (latest stopped = work sheet ending at the event),
        // but this server never stopped it - resuming would book Kunde X
        // until the next morning.
        var (service, kimai) = CreateService();
        await service.SyncKioskAsync([Kiosk("e1", "start", T08)]);
        kimai.SimulateLiveStop(T10.AddSeconds(20));
        var before = kimai.Operations.Count;

        var result = await service.SyncKioskAsync([Switch("e2", "kx", T10)]);

        var single = Assert.Single(result.Results);
        Assert.Equal("rejected", single.Status);
        Assert.Contains("nicht eindeutig", single.Message);
        Assert.Equal(before, kimai.Operations.Count);
        Assert.False(kimai.IsRunning);
    }

    [Fact]
    public async Task Switch_AppliedLiveThenStoppedNearEvent_IsNoOp()
    {
        // The switch reached Kimai live (the kiosk queued it anyway after a
        // timeout), then the employee clocked out within the tolerance: the
        // stopped sheet already ran on the target - nothing is missing.
        var (service, kimai) = CreateService();
        await service.SyncKioskAsync([Kiosk("e1", "start", T08), Switch("e2", "kx", T10)]);
        kimai.SimulateLiveStop(T10.AddSeconds(20));
        var before = kimai.Operations.Count;

        var result = await service.SyncKioskAsync([Switch("e3", "kx", T10)]);

        Assert.Equal("applied", result.Results.Single().Status);
        Assert.Contains("nicht nachtragbar", result.Results.Single().Message);
        Assert.Equal(before, kimai.Operations.Count);
    }

    [Fact]
    public async Task Switch_RecoveryFailingAgain_StillResumesOnTheNextRetry()
    {
        var (service, kimai) = CreateService();
        await service.SyncKioskAsync([Kiosk("e1", "start", T08)]);

        // Start fails in the batch AND in the recovery of the trailing flush:
        // the marker of the interrupted switch must survive both.
        kimai.FailNextStartCalls = 2;
        await service.SyncKioskAsync([Switch("e2", "kx", T10)]);
        Assert.False(kimai.IsRunning);

        await service.FlushOutboxAsync();

        Assert.Equal([("start", T08), ("stop", T10), ("start", T10)], kimai.Operations);
        Assert.Equal((20, 21), (kimai.StartedTargets[^1].ProjectId, kimai.StartedTargets[^1].ActivityId));
    }

    [Fact]
    public async Task Switch_InterruptedAfterStop_TaskDeletedBeforeRetry_IsDroppedLoudly()
    {
        var (service, kimai, logger, settings) = CreateServiceWithSettings();
        await service.SyncKioskAsync([Kiosk("e1", "start", T08)]);

        // Start fails in the batch AND in the trailing flush: the event stays
        // buffered while the employee is already stopped at 10:00.
        kimai.FailNextStartCalls = 2;
        await service.SyncKioskAsync([Switch("e2", "kx", T10)]);
        Assert.False(kimai.IsRunning);

        // Admin removes the task before the next retry.
        settings.Employees[0].Tasks[0] = new EmployeeTaskSettings { Id = "kx", Label = "Kunde X" };
        await service.FlushOutboxAsync();

        Assert.Equal([("start", T08), ("stop", T10)], kimai.Operations);
        Assert.Contains(logger.Messages, m => m.Contains("dropping kiosk event e2"));
    }

    [Fact]
    public async Task Switch_ToDefault_WhileOnUnknownSheet_Applies()
    {
        // A sheet that matches no task (deleted task, booking from the Kimai
        // UI) is NOT the default task: switching back must still happen.
        var (service, kimai) = CreateService();
        kimai.SimulateLiveStart(T08);

        var result = await service.SyncKioskAsync([Switch("e1", null, T10)]);

        Assert.Equal("applied", result.Results.Single().Status);
        Assert.Equal([("start", T08), ("stop", T10), ("start", T10)], kimai.Operations);
        Assert.Equal((7, 9), (kimai.StartedTargets[^1].ProjectId, kimai.StartedTargets[^1].ActivityId));
    }

    [Fact]
    public async Task Switch_ToDefault_WhileOnDefault_IsNoOp()
    {
        var (service, kimai) = CreateService();
        await service.SyncKioskAsync([Kiosk("e1", "start", T08)]);

        var result = await service.SyncKioskAsync([Switch("e2", null, T10)]);

        Assert.Contains("Lief bereits", result.Results.Single().Message);
        Assert.Single(kimai.Operations);
    }

    [Fact]
    public async Task PauseEnd_InterruptedAfterPauseStop_ResumesTaskThatRanBeforeThePause()
    {
        var (service, kimai) = CreateService();
        await service.SyncKioskAsync([Kiosk("e1", "start", T08), Switch("e2", "kx", T10), Kiosk("e3", "pauseStart", T12)]);

        // The resume-start fails after the pause stop; the trailing flush
        // completes the interrupted pause end.
        kimai.FailNextStartCalls = 1;
        await service.SyncKioskAsync([Kiosk("e4", "pauseEnd", T1230)]);

        Assert.Equal(("start", T1230), kimai.Operations[^1]);
        Assert.Equal((20, 21), (kimai.StartedTargets[^1].ProjectId, kimai.StartedTargets[^1].ActivityId));
        Assert.True(kimai.IsRunning);
        Assert.False(kimai.ActiveIsPause);
    }

    [Fact]
    public async Task PauseEnd_Replay_ResumesTaskThatRanBeforeThePause()
    {
        var (service, kimai) = CreateService();

        await service.SyncKioskAsync(
        [
            Kiosk("e1", "start", T08),
            Switch("e2", "kx", T10),
            Kiosk("e3", "pauseStart", T12),
            Kiosk("e4", "pauseEnd", T1230),
        ]);

        Assert.Equal((20, 21), (kimai.StartedTargets[^1].ProjectId, kimai.StartedTargets[^1].ActivityId));
        Assert.True(kimai.IsRunning);
        Assert.False(kimai.ActiveIsPause);
    }

    [Fact]
    public async Task PauseStart_Replay_BooksNonBillablePauseLikeTheLivePath()
    {
        var (service, kimai) = CreateService();

        await service.SyncKioskAsync([Kiosk("e1", "start", T08), Kiosk("e2", "pauseStart", T12)]);

        var pause = kimai.StartedTargets[^1];
        Assert.Equal((7, 42), (pause.ProjectId, pause.ActivityId));
        Assert.Equal("Pause", pause.Description);
        Assert.False(pause.Billable);
    }

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static OfflineKioskClockEventDto Switch(string eventId, string? taskId, DateTimeOffset at) =>
        new(eventId, "max", "1234", "switch", at, null, taskId);

    private static OfflineKioskClockEventDto StartOn(string eventId, string? taskId, DateTimeOffset at) =>
        new(eventId, "max", "1234", "start", at, null, taskId);

    private static OfflineKioskClockEventDto Kiosk(string eventId, string action, DateTimeOffset at, string? pin = "1234") =>
        new(eventId, "max", pin, action, at);

    private static (OfflineClockService Service, FakeKimaiClient Kimai) CreateService()
    {
        var (service, kimai, _) = CreateServiceWithLogger();
        return (service, kimai);
    }

    private static (OfflineClockService Service, RecordingTelegramNotifier Telegram, OfflineRejectionNotifier Throttle)
        CreateServiceWithTelegram(bool enabled = true)
    {
        var (service, telegram, throttle, _) = CreateServiceWithTelegramAndKimai(enabled);
        return (service, telegram, throttle);
    }

    private static (OfflineClockService Service, RecordingTelegramNotifier Telegram, OfflineRejectionNotifier Throttle, FakeKimaiClient Kimai)
        CreateServiceWithTelegramAndKimai(bool enabled = true,
            RejectedOfflineEventStore? existingJournal = null, TimeProvider? clock = null,
            IOfflineEventIdStore? eventIds = null, IEnumerable<EmployeeSettings>? extraEmployees = null)
    {
        var baseline = TestSettings();
        var settings = new RuntimeSettings
        {
            BaseUrl = baseline.BaseUrl,
            DefaultProjectId = baseline.DefaultProjectId,
            DefaultActivityId = baseline.DefaultActivityId,
            PauseActivityId = baseline.PauseActivityId,
            Employees = [.. baseline.Employees, .. extraEmployees ?? []],
            TelegramBotToken = enabled ? "test-token" : null,
            TelegramChatId = enabled ? "test-chat" : null
        };
        var store = new InMemorySettingsStore(settings);
        var telegram = new RecordingTelegramNotifier();
        var kimai = new FakeKimaiClient();
        var journal = existingJournal ?? new RejectedOfflineEventStore(
            Path.Combine(Path.GetTempPath(), $"stempeluhr-notify-{Guid.NewGuid():N}.json"));
        var throttle = new OfflineRejectionNotifier(journal, store, kimai, telegram,
            NullLogger<OfflineRejectionNotifier>.Instance, clock);
        var service = new OfflineClockService(store, new InMemoryEmployeeService(), kimai,
            eventIds ?? new InMemoryEventIdStore(), new KioskEventCoordinator(),
            journal,
            new RecordingLogger(), throttle);
        return (service, telegram, throttle, kimai);
    }

    private sealed class RecordingTelegramNotifier : ITelegramNotifier
    {
        public List<string> Messages { get; } = [];
        public bool SendSucceeds { get; set; } = true;
        public Task<bool> SendMessageAsync(string text)
        {
            Messages.Add(text);
            return Task.FromResult(SendSucceeds);
        }
        public Task SendStampNotificationAsync(string employeeName, string action, DateTimeOffset stampUtc,
            TimeZoneInfo timeZone, string? taskLabel = null) => Task.CompletedTask;
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        private long _elapsedTicks;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _elapsedTicks;

        public void Advance(TimeSpan duration)
        {
            _now += duration;
            _elapsedTicks += duration.Ticks;
        }

        /// <summary>Wall clock only, like an NTP correction; monotonic time is unaffected.</summary>
        public void Set(DateTimeOffset wallClock) => _now = wallClock;
    }

    private static (OfflineClockService Service, FakeKimaiClient Kimai, RecordingLogger Logger) CreateServiceWithLogger()
    {
        var (service, kimai, logger, _) = CreateServiceWithSettings();
        return (service, kimai, logger);
    }

    private static (OfflineClockService Service, FakeKimaiClient Kimai, RecordingLogger Logger, RuntimeSettings Settings) CreateServiceWithSettings(
        FakeKimaiClient? existingKimai = null)
    {
        var settings = TestSettings();

        // An existing fake stands for Kimai surviving a restart of this API
        // (a fresh marker store: markers live in memory only).
        var kimai = existingKimai ?? new FakeKimaiClient();
        var logger = new RecordingLogger();
        var service = new OfflineClockService(
            new InMemorySettingsStore(settings),
            new InMemoryEmployeeService(),
            kimai,
            new InMemoryEventIdStore(),
            new KioskEventCoordinator(),
            new RejectedOfflineEventStore(Path.Combine(Path.GetTempPath(), $"stempeluhr-rejected-{Guid.NewGuid():N}.json")),
            logger);

        return (service, kimai, logger, settings);
    }

    /// <summary>
    /// Live path and replay against one Kimai, sharing the marker store like
    /// the singleton in Program.cs (issue #67).
    /// </summary>
    private static (ClockService Live, OfflineClockService Replay, FakeKimaiClient Kimai, KioskEventCoordinator Markers) CreateLiveAndReplay()
    {
        var settings = TestSettings();
        var kimai = new FakeKimaiClient();
        var markers = new KioskEventCoordinator();
        var replay = new OfflineClockService(
            new InMemorySettingsStore(settings),
            new InMemoryEmployeeService(),
            kimai,
            new InMemoryEventIdStore(),
            markers,
            new RejectedOfflineEventStore(Path.Combine(Path.GetTempPath(), $"stempeluhr-rejected-{Guid.NewGuid():N}.json")),
            new RecordingLogger());
        var live = new ClockService(
            new InMemorySettingsStore(settings),
            new InMemoryEmployeeService(),
            kimai,
            kioskEvents: markers);
        return (live, replay, kimai, markers);
    }

    private static RuntimeSettings TestSettings()
    {
        return new RuntimeSettings
        {
            BaseUrl = "http://kimai.test",
            DefaultProjectId = 1,
            DefaultActivityId = 1,

            // Lets the fake distinguish pause timesheets from work timesheets
            // (StartAtAsync with this activity id opens a pause).
            PauseActivityId = 42,

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
                    Tasks = [new EmployeeTaskSettings { Id = "kx", Label = "Kunde X", ProjectId = 20, ActivityId = 21 }],
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

    private sealed class FailOnSecondEventIdStore : IOfflineEventIdStore
    {
        private int _registrations;
        public bool TryRegister(string eventId) => ++_registrations == 1
            ? true : throw new IOException("Event ID store unavailable");
        public void Remove(string eventId) { }
    }

    private sealed class InMemoryEmployeeService : IEmployeeService
    {
        public IReadOnlyCollection<EmployeeDto> GetEnabledEmployees(RuntimeSettings settings) => [];

        public EmployeeSettings? FindEmployee(RuntimeSettings settings, ClockRequest request) =>
            settings.Employees.FirstOrDefault(employee =>
                string.Equals(employee.Id, request.EmployeeId, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(employee.Pin) || employee.Pin == request.Pin));

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
    /// timesheet), unlike real Kimai where each employee has their own.
    /// </summary>
    private sealed class FakeKimaiClient : IKimaiClient
    {
        public List<(string Kind, DateTimeOffset At)> Operations { get; } = [];

        public int FailNextStatusCalls { get; set; }

        /// <summary>
        /// Fails the next N StartAtAsync calls with a transient network error -
        /// used to cut a two-step transaction (pauseStart/pauseEnd) in half.
        /// </summary>
        public int FailNextStartCalls { get; set; }

        /// <summary>
        /// Fails the next N BackdateEndAsync calls with a transient network
        /// error: the stop went through, the sheet keeps
        /// <see cref="StopWallClock"/> as its end (issue #10).
        /// </summary>
        public int FailNextBackdateCalls { get; set; }

        /// <summary>End Kimai writes on a plain stop, before any backdate ("now").</summary>
        public DateTimeOffset StopWallClock { get; set; } = Parse("2026-08-24T18:00:00Z");

        private int _timesheetCounter;
        private int? _activeTimesheetId;
        private int? _activeActivityId;
        private int? _activeProjectId;
        private bool _activeIsPause;
        private DateTimeOffset? _activeBeganAt;
        private readonly List<(int Id, int ActivityId, int? ProjectId, DateTimeOffset EndedAt)> _stoppedTimesheets = [];

        /// <summary>Every started target in order (for task assertions).</summary>
        public List<KimaiTimesheetTarget> StartedTargets { get; } = [];

        public bool IsRunning => _activeTimesheetId is not null;

        public int? ActiveTimesheetId => _activeTimesheetId;

        public bool ActiveIsPause => _activeTimesheetId is not null && _activeIsPause;

        /// <summary>
        /// Simulates a LIVE stamp via the live endpoint path (which does not
        /// share the offline service's sync lock): the active sheet begins at
        /// <paramref name="beganAt"/> without any replay operation involved.
        /// Used to build replay-vs-live race scenarios.
        /// </summary>
        public void SimulateLiveStart(DateTimeOffset beganAt)
        {
            Operations.Add(("start", beganAt));
            _activeTimesheetId = ++_timesheetCounter;
            _activeActivityId = 9;
            _activeProjectId = null;
            _activeIsPause = false;
            _activeBeganAt = beganAt;
        }

        /// <summary>
        /// Simulates a LIVE stop (e.g. Ausstempeln on another terminal) of
        /// the running sheet at <paramref name="stoppedAt"/>.
        /// </summary>
        public void SimulateLiveStop(DateTimeOffset stoppedAt)
        {
            Operations.Add(("stop", stoppedAt));
            StopActive(stoppedAt);
        }

        private void StopActive(DateTimeOffset endedAt)
        {
            if (_activeTimesheetId is int id && _activeActivityId is int activityId)
            {
                _stoppedTimesheets.Add((id, activityId, _activeProjectId, endedAt));
            }

            _activeTimesheetId = null;
            _activeActivityId = null;
            _activeProjectId = null;
            _activeIsPause = false;
            _activeBeganAt = null;
        }

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
            var state = !running ? "clockedOut" : _activeIsPause ? "paused" : "working";
            var task = running && !_activeIsPause ? WorkTargetResolver.MatchTask(employee, _activeProjectId, _activeActivityId) : null;
            var onDefault = running && !_activeIsPause && task is null
                && WorkTargetResolver.IsDefault(settings, employee, _activeProjectId, _activeActivityId);
            return Task.FromResult(new ClockStatusDto(
                running,
                _activeTimesheetId,
                running ? (_activeBeganAt ?? Parse("2026-08-24T00:00:00Z")).ToString("yyyy-MM-dd'T'HH:mm:sszzz") : null,
                0,
                state,
                running ? "Eingestempelt" : "Nicht eingestempelt",
                task?.Id,
                task?.Label,
                onDefault));
        }

        /// <summary>
        /// The next start calls (live or backdated) fail with these
        /// exceptions, in order - after <see cref="FailNextStartCalls"/>.
        /// </summary>
        public Queue<Exception> StartFailures { get; } = new();

        public Task StartAtAsync(
            RuntimeSettings settings,
            EmployeeSettings employee,
            KimaiTimesheetTarget target,
            DateTimeOffset startedAt,
            CancellationToken cancellationToken = default)
        {
            if (FailNextStartCalls > 0)
            {
                FailNextStartCalls--;
                throw new HttpRequestException("simulated transient start failure");
            }

            if (StartFailures.TryDequeue(out var failure))
            {
                throw failure;
            }

            Operations.Add(("start", startedAt));
            _activeTimesheetId = ++_timesheetCounter;
            StartedTargets.Add(target);
            _activeActivityId = target.ActivityId;
            _activeProjectId = target.ProjectId;
            _activeIsPause = settings.PauseActivityId == target.ActivityId;
            _activeBeganAt = startedAt;
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
            StopActive(stoppedAt);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Plain stop: the sheet ends at <see cref="StopWallClock"/>. Logged in
        /// <see cref="Operations"/> only by the backdate that follows, so a
        /// stop plus backdate reads like one StopAtAsync.
        /// </summary>
        public Task StopAsync(RuntimeSettings settings, EmployeeSettings employee, int timesheetId, CancellationToken cancellationToken = default)
        {
            StopActive(StopWallClock);
            return Task.CompletedTask;
        }

        public Task BackdateEndAsync(
            RuntimeSettings settings,
            EmployeeSettings employee,
            int timesheetId,
            DateTimeOffset endedAt,
            CancellationToken cancellationToken = default)
        {
            if (FailNextBackdateCalls > 0)
            {
                FailNextBackdateCalls--;
                throw new HttpRequestException("simulated transient backdate failure");
            }

            BackdateCalls++;
            Operations.Add(("stop", endedAt));
            var index = _stoppedTimesheets.FindIndex(sheet => sheet.Id == timesheetId);
            _stoppedTimesheets[index] = _stoppedTimesheets[index] with { EndedAt = endedAt };
            return Task.CompletedTask;
        }

        public int BackdateCalls { get; private set; }

        public DateTimeOffset? EndOf(int timesheetId) =>
            _stoppedTimesheets.Where(sheet => sheet.Id == timesheetId).Select(sheet => (DateTimeOffset?)sheet.EndedAt).FirstOrDefault();

        public Task<IReadOnlyList<KimaiRecentTimesheetDto>> GetRecentStoppedTimesheetsAsync(
            RuntimeSettings settings,
            EmployeeSettings employee,
            int count,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<KimaiRecentTimesheetDto> recent = _stoppedTimesheets
                .OrderByDescending(sheet => sheet.EndedAt)
                .Take(count)
                .Select(sheet => new KimaiRecentTimesheetDto(sheet.ActivityId, sheet.EndedAt, sheet.ProjectId, sheet.Id))
                .ToList();
            return Task.FromResult(recent);
        }

        public int TimezoneCalls { get; private set; }

        /// <summary>null mimics a Kimai error response (IKimaiClient contract).</summary>
        public string? TimezoneId { get; set; } = "Europe/Berlin";

        /// <summary>Runs while the lookup is "in flight", e.g. an admin action.</summary>
        public Action? DuringTimezoneLookup { get; set; }

        public Task<string?> GetCurrentUserTimezoneAsync(
            RuntimeSettings settings,
            EmployeeSettings employee,
            CancellationToken cancellationToken = default)
        {
            TimezoneCalls++;
            DuringTimezoneLookup?.Invoke();
            return Task.FromResult(TimezoneId);
        }

        /// <summary>
        /// Holds the next live start until the test completes it: null lets
        /// the start go through, an exception fails it. Stands for a Kimai
        /// call that outlasts the kiosk's timeout.
        /// </summary>
        public TaskCompletionSource<Exception?>? HoldNextLiveStart { get; set; }

        /// <summary>Live start: begins at <see cref="StopWallClock"/>, the fake's "now".</summary>
        public async Task StartAsync(RuntimeSettings settings, EmployeeSettings employee, KimaiTimesheetTarget target, CancellationToken cancellationToken = default)
        {
            if (HoldNextLiveStart is { } hold)
            {
                HoldNextLiveStart = null;
                if (await hold.Task is { } failure)
                {
                    throw failure;
                }
            }

            await StartAtAsync(settings, employee, target, StopWallClock, cancellationToken);
        }

        public Task<IReadOnlyCollection<KimaiUserDto>> GetUsersAsync(string baseUrl, string apiToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<KimaiActivityDto>> GetActivitiesAsync(string baseUrl, string apiToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<KimaiProjectDto>> GetProjectsAsync(string baseUrl, string apiToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<KimaiTimesheetEntryDto>> GetTimesheetsAsync(
            RuntimeSettings settings,
            EmployeeSettings employee,
            DateTime begin,
            DateTime end,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
