using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

/// <summary>
/// Unit tests for the idempotency store - the component whose Remove/Order
/// bug silently dropped never-applied offline events as duplicates.
/// </summary>
public sealed class FileOfflineEventIdStoreTests : IDisposable
{
    private readonly string _filePath;
    private FileOfflineEventIdStore? _store;

    public FileOfflineEventIdStoreTests()
    {
        _filePath = Path.Combine(Path.GetTempPath(), $"event-ids-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        _store?.Dispose();
        foreach (var suffix in new[] { "", ".tmp", ".corrupt" })
        {
            File.Delete(_filePath + suffix);
        }
    }

    private FileOfflineEventIdStore CreateStore() => _store = new FileOfflineEventIdStore(_filePath);

    [Fact]
    public void TryRegister_NewId_ReturnsTrueAndPersists()
    {
        var store = CreateStore();

        Assert.True(store.TryRegister("evt-1"));

        // Allow the background writer to flush, then reload from disk.
        store.Dispose();
        using var reloaded = new FileOfflineEventIdStore(_filePath);
        Assert.False(reloaded.TryRegister("evt-1"), "ID must survive a restart");
    }

    [Fact]
    public void TryRegister_SameIdTwice_ReturnsFalseOnSecondCall()
    {
        var store = CreateStore();

        Assert.True(store.TryRegister("evt-1"));
        Assert.False(store.TryRegister("evt-1"));
    }

    /// <summary>
    /// Regression test for the review finding: Remove() used to keep the ID in
    /// the ordering queue, so the next persist wrote it back to disk and a
    /// restart resurrected it - the event was then treated as duplicate and
    /// the stamp was lost forever.
    /// </summary>
    [Fact]
    public void RemovedId_IsNotResurrectedAfterRestart()
    {
        var store = CreateStore();
        store.TryRegister("buffered-event");
        store.Remove("buffered-event");

        // Give the single background writer time to persist the removal.
        store.Dispose();

        using var reloaded = new FileOfflineEventIdStore(_filePath);
        Assert.True(
            reloaded.TryRegister("buffered-event"),
            "A removed (never applied) event must be retryable after a restart");
    }

    [Fact]
    public void Remove_ThenReRegister_DoesNotGrowOrderDuplicates()
    {
        var store = CreateStore();

        for (var i = 0; i < 5; i++)
        {
            store.TryRegister("cycling-id");
            store.Remove("cycling-id");
        }

        store.Dispose();

        // The persisted file must not contain accumulated duplicate entries
        // of the cycling ID (the old implementation appended to _order on
        // every TryRegister but only ever removed from _ids).
        var persisted = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(_filePath)) ?? [];
        var count = persisted.Count(id => id == "cycling-id");
        Assert.True(count <= 1, $"_order contained {count} copies of a removed+re-added ID");
    }

    [Fact]
    public void CorruptFile_IsMovedAside_AndStoreStartsFresh()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, "{ this is not json ]");

        using var store = CreateStore();

        Assert.True(store.TryRegister("after-corruption"));
        Assert.True(File.Exists(_filePath + ".corrupt"), "corrupt file should be preserved for inspection");
    }
}
