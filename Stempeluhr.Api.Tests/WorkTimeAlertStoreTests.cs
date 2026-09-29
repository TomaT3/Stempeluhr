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
        var old = Violation(WorkTimeViolationKind.Shift, at.AddDays(-4).AddHours(-11), at.AddDays(-4));
        var current = Violation(WorkTimeViolationKind.Continuous, at.AddHours(-7), at);
        var store = new WorkTimeAlertStore(AlertPath);
        store.MarkSent("max", old, at.AddDays(-4));
        store.MarkSent("max", current, at);

        var reloaded = new WorkTimeAlertStore(AlertPath);

        Assert.True(reloaded.HasSent("MAX", current));
        Assert.False(reloaded.HasSent("anna", current));
        Assert.False(reloaded.HasSent("max", current with { Kind = WorkTimeViolationKind.Shift }));
        Assert.False(reloaded.HasSent("max", old));
    }

    [Fact]
    public void CorruptFile_IsQuarantinedAndNewAlertsCanBeSaved()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(AlertPath, "{broken");
        var store = new WorkTimeAlertStore(AlertPath);

        var now = DateTimeOffset.UtcNow;
        var violation = Violation(WorkTimeViolationKind.Shift, now.AddHours(-11), now);

        Assert.False(store.HasSent("max", violation));
        Assert.Single(Directory.GetFiles(_directory, "work-time-alerts.json.corrupt-*"));

        store.MarkSent("max", violation, now);
        Assert.True(new WorkTimeAlertStore(AlertPath).HasSent("max", violation));
    }

    private static WorkTimeViolation Violation(WorkTimeViolationKind kind, DateTimeOffset start, DateTimeOffset end) =>
        new(kind, start, end, (int)(end - start).TotalSeconds);
}
