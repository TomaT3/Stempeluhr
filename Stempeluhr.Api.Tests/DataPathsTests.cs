using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

public sealed class DataPathsTests
{
    private static readonly string ContentRoot = Path.Combine(Path.GetTempPath(), "stempeluhr-root");

    [Fact]
    public void Default_IsDataBelowTheContentRoot()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Equal(Path.Combine(ContentRoot, "data"), DataPaths.Directory(configuration, new Environment()));
    }

    /// <summary>
    /// The E2E test runs next to a dev API from the same checkout. Without its
    /// own data folder both processes rewrote the same offline-event-ids.json.
    /// </summary>
    [Fact]
    public void DataPath_MovesTheFolder()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Stempeluhr:DataPath"] = "/tmp/e2e/data" })
            .Build();

        Assert.Equal("/tmp/e2e/data", DataPaths.Directory(configuration, new Environment()));
    }

    private sealed class Environment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Stempeluhr.Api";
        public string ContentRootPath { get; set; } = ContentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
