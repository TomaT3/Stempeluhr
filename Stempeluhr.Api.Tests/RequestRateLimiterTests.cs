using Microsoft.Extensions.DependencyInjection;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

public sealed class RequestRateLimiterTests
{
    [Fact]
    public void Allows_UpToLimit_ThenRejects()
    {
        var limiter = new RequestRateLimiter(TimeSpan.FromSeconds(60), maxRequests: 3);

        Assert.True(limiter.TryAcquire("1.2.3.4"));
        Assert.True(limiter.TryAcquire("1.2.3.4"));
        Assert.True(limiter.TryAcquire("1.2.3.4"));
        Assert.False(limiter.TryAcquire("1.2.3.4"));
    }

    [Fact]
    public void DifferentKeys_AreIndependent()
    {
        var limiter = new RequestRateLimiter(TimeSpan.FromSeconds(60), maxRequests: 1);

        Assert.True(limiter.TryAcquire("1.1.1.1"));
        Assert.False(limiter.TryAcquire("1.1.1.1"));
        Assert.True(limiter.TryAcquire("2.2.2.2"));
    }

    /// <summary>
    /// Regression tests for the review finding that the kiosk sync limit
    /// bounded requests but not events (20 req x 100 events = 2,000 PIN
    /// guesses per minute): batches are now priced by their event count.
    /// </summary>
    [Fact]
    public void Cost_IsChargedAgainstTheWindowBudget()
    {
        var limiter = new RequestRateLimiter(TimeSpan.FromSeconds(60), maxRequests: 10);

        Assert.True(limiter.TryAcquire("1.2.3.4", 6));
        Assert.True(limiter.TryAcquire("1.2.3.4", 4));
        // Budget spent: even a single unit is rejected now.
        Assert.False(limiter.TryAcquire("1.2.3.4"));
    }

    [Fact]
    public void CostAboveBudget_FailsClosed_WithoutPartialGrant()
    {
        var limiter = new RequestRateLimiter(TimeSpan.FromSeconds(60), maxRequests: 3);

        // A 100-event batch priced at cost 10 must not slip through.
        Assert.False(limiter.TryAcquire("1.2.3.4", 10));
    }

    [Fact]
    public void NonPositiveCost_CountsAsSingleRequest()
    {
        var limiter = new RequestRateLimiter(TimeSpan.FromSeconds(60), maxRequests: 2);

        Assert.True(limiter.TryAcquire("1.2.3.4", 0));
        Assert.True(limiter.TryAcquire("1.2.3.4", -5));
        Assert.False(limiter.TryAcquire("1.2.3.4"));
    }

    /// <summary>
    /// Regression test for the review finding: the table used to grow without
    /// bound with unique keys because lazy eviction only replaced keys that
    /// came back.
    /// </summary>
    [Fact]
    public void Table_DoesNotGrow_Unbounded()
    {
        // Use reflection to read the private dictionary's count.
        var limiter = new RequestRateLimiter(TimeSpan.FromHours(1), maxRequests: 100);
        var field = typeof(RequestRateLimiter).GetField(
            "_entries", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var entries = (System.Collections.IDictionary)field!.GetValue(limiter)!;
        var property = entries.GetType().GetProperty("Count");

        for (var i = 0; i < 20_000; i++)
        {
            limiter.TryAcquire($"ip-{i}");
            if ((int)property!.GetValue(entries)! <= 10_000)
            {
                continue;
            }

            Assert.Fail($"Table grew to {(int)property.GetValue(entries)!} entries - cap not enforced");
            return;
        }

        Assert.True((int)property!.GetValue(entries)! <= 10_000, "Cap must hold after 20k unique keys");
    }

    /// <summary>
    /// Issue #53: both limiters share one type, so a plain double registration
    /// handed the identify limiter (60/min) to the sync endpoint as well - one
    /// shared instance. The keyed registration must yield two separate
    /// limiters with their own budgets.
    /// </summary>
    [Fact]
    public void KioskLimiters_AreSeparateInstancesWithTheirOwnLimits()
    {
        using var provider = new ServiceCollection().AddKioskRateLimiters().BuildServiceProvider();

        var sync = provider.GetRequiredKeyedService<RequestRateLimiter>(KioskRateLimiters.SyncKey);
        var identify = provider.GetRequiredKeyedService<RequestRateLimiter>(KioskRateLimiters.IdentifyKey);

        Assert.NotSame(sync, identify);
        Assert.Same(sync, provider.GetRequiredKeyedService<RequestRateLimiter>(KioskRateLimiters.SyncKey));

        Assert.True(sync.TryAcquire("1.2.3.4", 20));
        Assert.False(sync.TryAcquire("1.2.3.4"));

        // The exhausted sync budget must not touch identify.
        Assert.True(identify.TryAcquire("1.2.3.4", 60));
        Assert.False(identify.TryAcquire("1.2.3.4"));
    }

    [Fact]
    public void WorkTimeHintLimiter_HasItsOwn60PerMinuteBudget_ApartFromTheCorrectionBudget()
    {
        using var provider = new ServiceCollection().AddKioskRateLimiters().BuildServiceProvider();

        var hints = provider.GetRequiredKeyedService<RequestRateLimiter>(KioskRateLimiters.WorkTimeHintKey);
        var corrections = provider.GetRequiredKeyedService<RequestRateLimiter>(KioskRateLimiters.CorrectionKey);

        Assert.NotSame(hints, corrections);
        Assert.True(hints.TryAcquire("1.2.3.4", 60));
        Assert.False(hints.TryAcquire("1.2.3.4"));

        // The exhausted hint budget must not touch the corrections.
        Assert.True(corrections.TryAcquire("1.2.3.4", 30));
        Assert.False(corrections.TryAcquire("1.2.3.4"));
    }
}
