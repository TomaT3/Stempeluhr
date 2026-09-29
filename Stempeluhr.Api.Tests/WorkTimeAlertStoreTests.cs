using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

public sealed class WorkTimeAlertStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"stempeluhr-work-time-store-{Guid.NewGuid():N}");

    private string AlertPath => Path.Combine(_directory, "work-time-alerts.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void SentAlerts_SurviveARestart_AndOldOnesAreDropped()
    {
        var at = DateTimeOffset.Parse("2026-09-30T12:00:00Z");
        var store = new WorkTimeAlertStore(AlertPath);
        store.MarkSent("max", "shift:old", at.AddDays(-4));
        store.MarkSent("max", "continuous:new", at);

        var reloaded = new WorkTimeAlertStore(AlertPath);

        Assert.True(reloaded.HasSent("MAX", "continuous:new"));
        Assert.False(reloaded.HasSent("anna", "continuous:new"));
        Assert.False(reloaded.HasSent("max", "shift:old"));
    }

    [Fact]
    public void CorruptFile_IsQuarantinedAndNewAlertsCanBeSaved()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(AlertPath, "{broken");
        var store = new WorkTimeAlertStore(AlertPath);

        Assert.False(store.HasSent("max", "shift:x"));
        Assert.Single(Directory.GetFiles(_directory, "work-time-alerts.json.corrupt-*"));

        store.MarkSent("max", "shift:x", DateTimeOffset.UtcNow);
        Assert.True(new WorkTimeAlertStore(AlertPath).HasSent("max", "shift:x"));
    }
}
