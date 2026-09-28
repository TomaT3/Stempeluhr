using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

public sealed class KioskEventCoordinatorTests
{
    [Fact]
    public void LiveRequest_IsInFlightUntilDisposed()
    {
        var coordinator = new KioskEventCoordinator();

        using (coordinator.BeginLive("e1"))
        {
            Assert.True(coordinator.IsLiveInFlight("e1"));
            Assert.False(coordinator.TryGet("e1", out _));
        }

        Assert.False(coordinator.IsLiveInFlight("e1"));
    }

    [Fact]
    public void MarkerSetByTheLiveRequest_SurvivesItsEnd()
    {
        var coordinator = new KioskEventCoordinator();

        using (coordinator.BeginLive("e1"))
        {
            coordinator.Remember("e1", 42);
        }

        Assert.True(coordinator.TryGet("e1", out var stopped));
        Assert.Equal(42, stopped);
        Assert.False(coordinator.IsLiveInFlight("e1"));
    }

    [Fact]
    public void Forget_KeepsALiveRequestInFlight()
    {
        var coordinator = new KioskEventCoordinator();
        using var live = coordinator.BeginLive("e1");

        coordinator.Forget("e1");

        Assert.True(coordinator.IsLiveInFlight("e1"));
    }

    [Fact]
    public void BeginLive_ForAnEventWithAMarker_LeavesTheMarkerAlone()
    {
        var coordinator = new KioskEventCoordinator();
        coordinator.Remember("e1", 42);

        using (coordinator.BeginLive("e1"))
        {
            Assert.False(coordinator.IsLiveInFlight("e1"));
        }

        Assert.True(coordinator.TryGet("e1", out _));
    }
}
