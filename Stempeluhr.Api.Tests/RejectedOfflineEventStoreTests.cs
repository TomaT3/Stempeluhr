using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

public sealed class RejectedOfflineEventStoreTests
{
    [Fact]
    public void RecordsSurviveRestart_AndResolutionStaysVisible()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"stempeluhr-rejected-test-{Guid.NewGuid():N}");
        try
        {
            var path = Path.Combine(directory, "rejected.json");
            var entry = new RejectedOfflineEvent("event-1", "max", "Max", "start",
                DateTimeOffset.Parse("2026-09-18T08:00:00Z"), DateTimeOffset.UtcNow, "PIN falsch");
            var store = new RejectedOfflineEventStore(path);
            store.Record(entry);
            store.Record(entry);

            var reopened = new RejectedOfflineEventStore(path);
            Assert.Single(reopened.List());
            Assert.Null(reopened.List()[0].ResolvedAt);
            Assert.True(reopened.Resolve("event-1"));
            Assert.False(reopened.Resolve("missing"));

            var resolved = Assert.Single(new RejectedOfflineEventStore(path).List());
            Assert.NotNull(resolved.ResolvedAt);
            Assert.Equal("Max", resolved.EmployeeName);
            Assert.DoesNotContain("\"pin\":", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
