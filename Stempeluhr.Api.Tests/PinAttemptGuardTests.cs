using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

/// <summary>Failed-PIN backoff per employee (issue #8).</summary>
public sealed class PinAttemptGuardTests
{
    private static readonly string MaxKey = PinAttemptGuard.EmployeeKey("max");

    [Fact]
    public void FiveFailuresLock_ThenTheLockEscalatesFromOneToFiveToFifteenMinutes()
    {
        var clock = new ManualClock();
        var guard = new PinAttemptGuard(clock: clock);

        foreach (var expected in new[] { 1, 5, 15, 15 })
        {
            Fail(guard, MaxKey, PinAttemptGuard.EmployeeThreshold - 1);
            Assert.Null(guard.RemainingLock(MaxKey));

            guard.RecordFailure(MaxKey);
            Assert.Equal(TimeSpan.FromMinutes(expected), guard.RemainingLock(MaxKey));

            clock.Advance(TimeSpan.FromMinutes(expected));
            Assert.Null(guard.RemainingLock(MaxKey));
        }
    }

    [Fact]
    public void Success_ResetsCountAndEscalation()
    {
        var clock = new ManualClock();
        var guard = new PinAttemptGuard(clock: clock);
        Fail(guard, MaxKey, PinAttemptGuard.EmployeeThreshold);
        clock.Advance(TimeSpan.FromMinutes(1));

        Fail(guard, MaxKey, PinAttemptGuard.EmployeeThreshold - 1);
        guard.RecordSuccess(MaxKey);
        Fail(guard, MaxKey, PinAttemptGuard.EmployeeThreshold - 1);
        Assert.Null(guard.RemainingLock(MaxKey));

        guard.RecordFailure(MaxKey);
        Assert.Equal(TimeSpan.FromMinutes(1), guard.RemainingLock(MaxKey));
    }

    [Fact]
    public void FailuresDuringTheLock_NeitherCountNorExtendIt()
    {
        var clock = new ManualClock();
        var guard = new PinAttemptGuard(clock: clock);
        Fail(guard, MaxKey, PinAttemptGuard.EmployeeThreshold);

        Fail(guard, MaxKey, 20);

        Assert.Equal(TimeSpan.FromMinutes(1), guard.RemainingLock(MaxKey));
    }

    [Fact]
    public void OccasionalTyposNeverAddUpToALock()
    {
        var clock = new ManualClock();
        var guard = new PinAttemptGuard(clock: clock);

        for (var i = 0; i < 20; i++)
        {
            guard.RecordFailure(MaxKey);
            clock.Advance(TimeSpan.FromHours(1));
        }

        Assert.Null(guard.RemainingLock(MaxKey));
    }

    [Fact]
    public void PinLoginWithoutEmployeeId_HasItsOwnHigherThreshold()
    {
        var guard = new PinAttemptGuard(clock: new ManualClock());
        Fail(guard, PinAttemptGuard.PinLoginKey, PinAttemptGuard.EmployeeThreshold);
        Assert.Null(guard.RemainingLock(PinAttemptGuard.PinLoginKey));
        Assert.Null(guard.RemainingLock(MaxKey));

        Fail(guard, PinAttemptGuard.PinLoginKey, PinAttemptGuard.PinLoginThreshold - PinAttemptGuard.EmployeeThreshold);
        Assert.NotNull(guard.RemainingLock(PinAttemptGuard.PinLoginKey));
    }

    [Fact]
    public async Task LiveClock_LocksAfterFiveWrongPins_AndTheRightPinWorksAfterTheLock()
    {
        var clock = new ManualClock();
        var guard = new PinAttemptGuard(clock: clock);
        var kimai = new CountingKimaiClient();
        var service = new ClockService(new StubSettingsStore(Settings()), new EmployeeService(), kimai, pinAttempts: guard);

        for (var i = 0; i < PinAttemptGuard.EmployeeThreshold; i++)
        {
            Assert.Null(await service.GetStatusAsync(new ClockRequest("max", "0000")));
        }

        // Locked: even the right PIN is refused before it is checked.
        var locked = await Assert.ThrowsAsync<PinLockedException>(() =>
            service.ClockAsync(new KioskClockRequest("max", "1234", "stop", null)));
        Assert.Equal(TimeSpan.FromMinutes(1), locked.RetryAfter);
        Assert.Equal(0, kimai.StatusCalls);
        // Another employee is not affected.
        Assert.NotNull(await service.GetStatusAsync(new ClockRequest("anna", "5678")));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull(await service.GetStatusAsync(new ClockRequest("max", "1234")));
        Assert.Null(guard.RemainingLock(MaxKey));
    }

    [Fact]
    public async Task UnknownEmployeeIds_AreRefusedWithoutEverLocking()
    {
        var guard = new PinAttemptGuard(clock: new ManualClock());
        var service = new ClockService(new StubSettingsStore(Settings()), new EmployeeService(), new CountingKimaiClient(), pinAttempts: guard);

        for (var i = 0; i < 20; i++)
        {
            Assert.Null(await service.GetStatusAsync(new ClockRequest("nobody", "0000")));
        }

        Assert.Null(guard.RemainingLock(PinAttemptGuard.EmployeeKey("nobody")));
    }

    [Fact]
    public async Task PinLoginAndHours_ShareTheGlobalLock()
    {
        var clock = new ManualClock();
        var guard = new PinAttemptGuard(clock: clock);
        var service = new ClockService(new StubSettingsStore(Settings()), new EmployeeService(), new CountingKimaiClient(), pinAttempts: guard);

        for (var i = 0; i < PinAttemptGuard.PinLoginThreshold / 2; i++)
        {
            Assert.Null(await service.LoginWithPinAsync("0000"));
            Assert.Null(await service.GetHoursOverviewAsync("0001"));
        }

        await Assert.ThrowsAsync<PinLockedException>(() => service.LoginWithPinAsync("1234"));
        // An empty PIN is no guess and does not touch the lock.
        Assert.Null(await service.LoginWithPinAsync(""));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("max", (await service.LoginWithPinAsync("1234"))?.Employee.Id);
    }

    [Fact]
    public async Task Filter_AnswersTooManyRequestsWithRetryAfter()
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };
        http.Response.Body = new MemoryStream();
        var context = new DefaultEndpointFilterInvocationContext(http);

        var result = await PinLockedException.Filter(context,
            _ => throw new PinLockedException(TimeSpan.FromSeconds(89.2)));
        await Assert.IsAssignableFrom<IResult>(result).ExecuteAsync(http);

        Assert.Equal(StatusCodes.Status429TooManyRequests, http.Response.StatusCode);
        Assert.Equal("90", http.Response.Headers.RetryAfter.ToString());
        http.Response.Body.Position = 0;
        Assert.Contains("2 Min.", new StreamReader(http.Response.Body).ReadToEnd());
    }

    private static void Fail(PinAttemptGuard guard, string key, int count)
    {
        for (var i = 0; i < count; i++)
        {
            guard.RecordFailure(key);
        }
    }

    private static RuntimeSettings Settings() => new()
    {
        BaseUrl = "http://kimai.test",
        Employees =
        {
            new EmployeeSettings { Id = "max", Pin = "1234", ApiToken = "t", DisplayName = "Max" },
            new EmployeeSettings { Id = "anna", Pin = "5678", ApiToken = "t2", DisplayName = "Anna" }
        }
    };

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 29, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class StubSettingsStore(RuntimeSettings settings) : IRuntimeSettingsStore
    {
        public RuntimeSettings Load() => settings;

        public Task SaveAsync(RuntimeSettings settings, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CountingKimaiClient : IKimaiClient
    {
        public int StatusCalls { get; private set; }

        public Task<ClockStatusDto> GetStatusAsync(RuntimeSettings s, EmployeeSettings e, CancellationToken ct = default)
        {
            StatusCalls++;
            return Task.FromResult(new ClockStatusDto(false, null, null, 0, "clockedOut", "Nicht eingestempelt"));
        }

        public Task<IReadOnlyCollection<KimaiTimesheetEntryDto>> GetTimesheetsAsync(
            RuntimeSettings settings, EmployeeSettings employee, DateTime begin, DateTime end, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<string?> GetCurrentUserTimezoneAsync(RuntimeSettings s, EmployeeSettings e, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StartAsync(RuntimeSettings s, EmployeeSettings e, KimaiTimesheetTarget t, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StartAtAsync(RuntimeSettings s, EmployeeSettings e, KimaiTimesheetTarget t, DateTimeOffset d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StopAsync(RuntimeSettings s, EmployeeSettings e, int id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StopAtAsync(RuntimeSettings s, EmployeeSettings e, int id, DateTimeOffset d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task BackdateEndAsync(RuntimeSettings s, EmployeeSettings e, int id, DateTimeOffset d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<KimaiRecentTimesheetDto>> GetRecentStoppedTimesheetsAsync(RuntimeSettings s, EmployeeSettings e, int count, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<KimaiUserDto>> GetUsersAsync(string b, string t, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<KimaiActivityDto>> GetActivitiesAsync(string b, string t, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<KimaiProjectDto>> GetProjectsAsync(string b, string t, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
