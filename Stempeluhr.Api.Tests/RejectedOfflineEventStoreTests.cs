using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

public sealed class RejectedOfflineEventStoreTests
{
    [Fact]
    public void KeepsAllOpenRecordsAndOnlyTheNewestThousandResolvedRecords()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"stempeluhr-journal-limit-{Guid.NewGuid():N}");
        try
        {
            var store = new RejectedOfflineEventStore(Path.Combine(directory, "rejected.json"));
            var at = DateTimeOffset.Parse("2026-09-18T08:00:00Z");
            store.Record(new RejectedOfflineEvent("open", "max", "Max", "start", at, at, "abgelehnt"));
            for (var i = 0; i < 1001; i++)
            {
                store.Record(new RejectedOfflineEvent($"resolved-{i}", "max", "Max", "start",
                    at, at, "abgelehnt", at.AddMinutes(i)));
            }

            var entries = new RejectedOfflineEventStore(Path.Combine(directory, "rejected.json")).List();
            Assert.Equal(1001, entries.Count);
            Assert.Contains(entries, entry => entry.EventId == "open");
            Assert.DoesNotContain(entries, entry => entry.EventId == "resolved-0");
            Assert.Contains(entries, entry => entry.EventId == "resolved-1000");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CorruptJournal_IsQuarantinedAndNewRecordsCanBeSaved()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"stempeluhr-corrupt-journal-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "rejected.json");
            File.WriteAllText(path, "{broken");
            var store = new RejectedOfflineEventStore(path);

            Assert.Empty(store.List());
            Assert.Single(Directory.GetFiles(directory, "rejected.json.corrupt-*"));
            File.WriteAllText(path, "{also broken");
            var recovered = new RejectedOfflineEventStore(path);
            Assert.Empty(recovered.List());
            Assert.Equal(2, Directory.GetFiles(directory, "rejected.json.corrupt-*").Length);
            recovered.Record(new RejectedOfflineEvent("event-1", "max", "Max", "start",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "abgelehnt"));
            Assert.Single(new RejectedOfflineEventStore(path).List());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TransientReadError_DoesNotCacheEmptyStateOrOverwriteTheJournal()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"stempeluhr-locked-journal-{Guid.NewGuid():N}");
        try
        {
            var path = Path.Combine(directory, "rejected.json");
            var first = new RejectedOfflineEvent("first", "max", "Max", "start",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "abgelehnt");
            new RejectedOfflineEventStore(path).Record(first);
            var store = new RejectedOfflineEventStore(path);

            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var error = Record.Exception(() => store.Record(first with { EventId = "second" }));
                Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
                Assert.Empty(Directory.GetFiles(directory, "rejected.json.corrupt-*"));
            }

            store.Record(first with { EventId = "second" });
            Assert.Equal(2, new RejectedOfflineEventStore(path).List().Count);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

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
