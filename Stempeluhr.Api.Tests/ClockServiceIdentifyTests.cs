using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

/// <summary>
/// Tests the card identification used by POST /api/kiosk/identify: a scanned
/// card id resolves to an employee WITHOUT stamping anything. This is the
/// kiosk's online fallback when a card is not in its local cache.
/// </summary>
public sealed class ClockServiceIdentifyTests
{
    [Fact]
    public async Task IdentifyWithNfcCardAsync_KnownCard_ReturnsEmployeeWithoutStamping()
    {
        var settings = new RuntimeSettings
        {
            BaseUrl = "http://kimai.test",
            Employees =
            {
                new EmployeeSettings { Id = "max", Pin = "1234", ApiToken = "t", DisplayName = "Max", NfcCardId = "04ABCD" }
            }
        };
        var kimai = new StubKimaiClient();
        var service = new ClockService(
            new StubSettingsStore(settings),
            new EmployeeService(),
            kimai);

        var result = await service.IdentifyWithNfcCardAsync(
            new NfcClockRequest("04abcd", null, "term-1"));

        Assert.True(result.Success);
        Assert.NotNull(result.Employee);
        Assert.Equal("max", result.Employee.Id);
        Assert.Equal("04ABCD", result.CardId);
        Assert.True(kimai.LastStatusChecked);
        // Identification must never stamp: no start/stop/pause operations.
        Assert.False(kimai.LastStamped);
    }

    [Fact]
    public async Task IdentifyWithNfcCardAsync_UnknownCard_ReturnsFailureWithoutStamping()
    {
        var settings = new RuntimeSettings
        {
            BaseUrl = "http://kimai.test",
            Employees =
            {
                new EmployeeSettings { Id = "max", Pin = "1234", ApiToken = "t", DisplayName = "Max", NfcCardId = "04ABCD" }
            }
        };
        var kimai = new StubKimaiClient();
        var service = new ClockService(
            new StubSettingsStore(settings),
            new EmployeeService(),
            kimai);

        var result = await service.IdentifyWithNfcCardAsync(
            new NfcClockRequest("FFFF01", null, "term-1"));

        Assert.False(result.Success);
        Assert.Null(result.Employee);
        Assert.False(kimai.LastStatusChecked);
        Assert.False(kimai.LastStamped);
    }

    private sealed class StubSettingsStore(RuntimeSettings settings) : IRuntimeSettingsStore
    {
        public RuntimeSettings Load() => settings;

        public Task SaveAsync(RuntimeSettings settings, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubKimaiClient : IKimaiClient
    {
        public Task<KimaiTimesheetDetailDto?> GetTimesheetAsync(RuntimeSettings s, EmployeeSettings e, int id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> CreateTimesheetAsync(RuntimeSettings s, EmployeeSettings e, KimaiTimesheetTarget t, DateTimeOffset begin, DateTimeOffset end, string? description, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateTimesheetTimesAsync(RuntimeSettings s, EmployeeSettings e, int id, DateTimeOffset? begin, DateTimeOffset? end, CancellationToken ct = default) => throw new NotSupportedException();

        public bool LastStatusChecked { get; private set; }
        public bool LastStamped { get; private set; }

        public Task<ClockStatusDto> GetStatusAsync(RuntimeSettings s, EmployeeSettings e, CancellationToken ct = default)
        {
            LastStatusChecked = true;
            return Task.FromResult(new ClockStatusDto(
                false,
                null,
                null,
                0,
                "clockedOut",
                "Nicht eingestempelt"));
        }

        public Task<IReadOnlyCollection<KimaiTimesheetEntryDto>> GetTimesheetsAsync(
            RuntimeSettings settings, EmployeeSettings employee, DateTime begin, DateTime end, CancellationToken ct = default)
        {
            LastStamped = true;
            throw new NotSupportedException();
        }

        public Task<int?> GetCurrentUserIdAsync(RuntimeSettings s, EmployeeSettings e, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetCurrentUserTimezoneAsync(RuntimeSettings s, EmployeeSettings e, CancellationToken ct = default) =>
            Task.FromResult<string?>("Europe/Berlin");
        public Task StartAsync(RuntimeSettings s, EmployeeSettings e, KimaiTimesheetTarget t, CancellationToken ct = default) { LastStamped = true; throw new NotSupportedException(); }
        public Task StartAtAsync(RuntimeSettings s, EmployeeSettings e, KimaiTimesheetTarget t, DateTimeOffset d, CancellationToken ct = default) { LastStamped = true; throw new NotSupportedException(); }
        public Task StopAsync(RuntimeSettings s, EmployeeSettings e, int id, CancellationToken ct = default) { LastStamped = true; throw new NotSupportedException(); }
        public Task StopAtAsync(RuntimeSettings s, EmployeeSettings e, int id, DateTimeOffset d, CancellationToken ct = default) { LastStamped = true; throw new NotSupportedException(); }
        public Task BackdateEndAsync(RuntimeSettings s, EmployeeSettings e, int id, DateTimeOffset d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<KimaiRecentTimesheetDto>> GetRecentStoppedTimesheetsAsync(RuntimeSettings s, EmployeeSettings e, int count, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<KimaiUserDto>> GetUsersAsync(string b, string t, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<KimaiActivityDto>> GetActivitiesAsync(string b, string t, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<KimaiProjectDto>> GetProjectsAsync(string b, string t, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
