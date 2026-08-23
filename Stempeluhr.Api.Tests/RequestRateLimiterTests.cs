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
}
