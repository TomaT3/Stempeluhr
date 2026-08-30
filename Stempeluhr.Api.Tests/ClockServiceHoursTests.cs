using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

public sealed class ClockServiceHoursTests
{
    [Fact]
    public async Task GetHoursOverviewAsync_UnknownPin_ReturnsNull()
    {
        var settings = new RuntimeSettings { BaseUrl = "http://kimai.test" };
        var service = new ClockService(
            new StubSettingsStore(settings),
            new EmployeeService(),
            new StubKimaiClient());

        Assert.Null(await service.GetHoursOverviewAsync("1234"));
    }

    [Fact]
    public async Task GetHoursOverviewAsync_ValidPin_ReturnsOverview()
    {
        var settings = new RuntimeSettings
        {
            BaseUrl = "http://kimai.test",
            PauseActivityId = 99,
            Employees = { new EmployeeSettings { Id = "max", Pin = "1234", ApiToken = "t", DisplayName = "Max" } }
        };
        var kimai = new StubKimaiClient();
        var service = new ClockService(
            new StubSettingsStore(settings),
            new EmployeeService(),
            kimai);

        var result = await service.GetHoursOverviewAsync("1234");

        Assert.NotNull(result);
        Assert.True(kimai.LastBegin <= kimai.LastEnd);
        Assert.Equal("max", kimai.LastEmployeeId);
    }

    private sealed class StubSettingsStore(RuntimeSettings settings) : IRuntimeSettingsStore
    {
        public RuntimeSettings Load() => settings;

        public Task SaveAsync(RuntimeSettings settings, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubKimaiClient : IKimaiClient
    {
        public DateTime LastBegin { get; private set; }
        public DateTime LastEnd { get; private set; }
        public string? LastEmployeeId { get; private set; }

        public Task<IReadOnlyCollection<KimaiTimesheetEntryDto>> GetTimesheetsAsync(
            RuntimeSettings settings, EmployeeSettings employee, DateTime begin, DateTime end, CancellationToken ct = default)
        {
            LastBegin = begin;
            LastEnd = end;
            LastEmployeeId = employee.Id;
            return Task.FromResult<IReadOnlyCollection<KimaiTimesheetEntryDto>>([]);
        }

        public Task<ClockStatusDto> GetStatusAsync(RuntimeSettings s, EmployeeSettings e, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetCurrentUserTimezoneAsync(RuntimeSettings s, EmployeeSettings e, CancellationToken ct = default) => Task.FromResult<string?>("Europe/Berlin");
        public Task StartAsync(RuntimeSettings s, EmployeeSettings e, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StartAtAsync(RuntimeSettings s, EmployeeSettings e, int p, int a, DateTimeOffset d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StartPauseAsync(RuntimeSettings s, EmployeeSettings e, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StopAsync(RuntimeSettings s, EmployeeSettings e, int id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StopAtAsync(RuntimeSettings s, EmployeeSettings e, int id, DateTimeOffset d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KimaiRecentTimesheetDto?> GetLatestStoppedTimesheetAsync(RuntimeSettings s, EmployeeSettings e, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<KimaiUserDto>> GetUsersAsync(string b, string t, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<KimaiActivityDto>> GetActivitiesAsync(string b, string t, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<KimaiProjectDto>> GetProjectsAsync(string b, string t, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
