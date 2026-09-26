using Stempeluhr.Api.Middleware;
using Xunit;

namespace Stempeluhr.Api.Tests;

public sealed class StaticFileCachePolicyTests
{
    [Theory]
    [InlineData("main-CNEKRQK7.js")]
    [InlineData("chunk-AB12CD34.js")]
    [InlineData("styles-ZLKQUFCK.css")]
    [InlineData("roboto-ABCDEFGH.woff2")]
    public void HashedBuildOutput_IsImmutable(string fileName)
    {
        Assert.Equal(StaticFileCachePolicy.Immutable, StaticFileCachePolicy.GetCacheControl(fileName));
    }

    [Theory]
    [InlineData("index.html")]
    [InlineData("favicon.ico")]
    [InlineData("ngsw.json")]
    [InlineData("ngsw-worker.js")]
    [InlineData("agent.json")]
    [InlineData("install.sh")]
    [InlineData("update.sh")]
    [InlineData("agent-0.13.0.tar.gz")]
    public void NamesStableAcrossDeploys_AreRevalidated(string fileName)
    {
        Assert.Equal(StaticFileCachePolicy.Revalidate, StaticFileCachePolicy.GetCacheControl(fileName));
    }
}
