using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

public sealed class TimeCorrectionStoreTests
{
    private static TimeCorrectionRequest Entry(string id, TimeCorrectionStatus status = TimeCorrectionStatus.Pending, DateTimeOffset? decidedAt = null) => new()
    {
        Id = id,
        EmployeeId = "max",
        EmployeeName = "Max Mustermann",
        Kind = TimeCorrectionKind.SetEnd,
        Source = "clock",
        CreatedAt = TimeCorrection.Now,
        Status = status,
        DecidedAt = decidedAt,
    };

    private static string TempDirectory(string name) => Path.Combine(Path.GetTempPath(), $"stempeluhr-{name}-{Guid.NewGuid():N}");

    [Fact]
    public void PersistsRequestsWithAllFields_AndAnotherInstanceReadsThemBack()
    {
        var directory = TempDirectory("corrections");
        try
        {
            var path = Path.Combine(directory, "time-corrections.json");
            var request = Entry("abc") with
            {
                Kind = TimeCorrectionKind.AddPause,
                TimesheetId = 42,
                PauseBegin = TimeCorrection.At(11),
                PauseEnd = TimeCorrection.At(11, 30),
                Original = new TimeCorrectionOriginal(TimeCorrection.At(8), TimeCorrection.At(12), 1, 1, "Stempeluhr", true),
                AppliedSteps = ["shorten"],
                TelegramChatId = -100123,
                TelegramMessageId = 77,
            };
            Assert.True(new TimeCorrectionStore(path).Add(request));

            var read = Assert.Single(new TimeCorrectionStore(path).List());
            Assert.Equal(request.Id, read.Id);
            Assert.Equal(TimeCorrectionKind.AddPause, read.Kind);
            Assert.Equal(TimeCorrection.At(11), read.PauseBegin);
            Assert.Equal(42, read.TimesheetId);
            Assert.Equal("shorten", Assert.Single(read.AppliedSteps));
            Assert.Equal(request.Original, read.Original);
            Assert.Equal((-100123L, 77L), (read.TelegramChatId, read.TelegramMessageId));
            Assert.Contains("\"addPause\"", File.ReadAllText(path));
            Assert.Contains("\"pending\"", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AddRejectsDuplicateIds_AndUpdateReplacesTheRequest()
    {
        var directory = TempDirectory("corrections-update");
        try
        {
            var store = new TimeCorrectionStore(Path.Combine(directory, "c.json"));
            Assert.True(store.Add(Entry("one")));
            Assert.False(store.Add(Entry("one")));

            Assert.True(store.Update(Entry("one", TimeCorrectionStatus.Rejected)));
            Assert.False(store.Update(Entry("unknown")));

            Assert.Equal(TimeCorrectionStatus.Rejected, store.Find("one")!.Status);
            Assert.Null(store.Find("unknown"));
            Assert.Single(store.List());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void KeepsAllOpenRequestsAndOnlyTheNewestThousandCompletedRequests()
    {
        var directory = TempDirectory("corrections-limit");
        try
        {
            var path = Path.Combine(directory, "c.json");
            var store = new TimeCorrectionStore(path);
            store.Add(Entry("pending"));
            store.Add(Entry("failed", TimeCorrectionStatus.Failed));
            for (var i = 0; i < 1001; i++)
            {
                store.Add(Entry($"done-{i}", TimeCorrectionStatus.Applied, TimeCorrection.Now.AddMinutes(i)));
            }

            var ids = new TimeCorrectionStore(path).List().Select(entry => entry.Id).ToHashSet();
            Assert.Equal(1002, ids.Count);
            Assert.Contains("pending", ids);
            Assert.Contains("failed", ids);
            Assert.DoesNotContain("done-0", ids);
            Assert.Contains("done-1000", ids);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CorruptFile_IsQuarantinedAndNewRequestsCanBeSaved()
    {
        var directory = TempDirectory("corrections-corrupt");
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "c.json");
            File.WriteAllText(path, "{broken");
            var store = new TimeCorrectionStore(path);

            Assert.Empty(store.List());
            Assert.Single(Directory.GetFiles(directory, "c.json.corrupt-*"));
            store.Add(Entry("fresh"));
            Assert.Single(new TimeCorrectionStore(path).List());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TransientReadError_DoesNotCacheEmptyStateOrOverwriteTheFile()
    {
        var directory = TempDirectory("corrections-locked");
        try
        {
            var path = Path.Combine(directory, "c.json");
            new TimeCorrectionStore(path).Add(Entry("first"));
            var store = new TimeCorrectionStore(path);

            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var error = Record.Exception(() => store.Add(Entry("second")));
                Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
                Assert.Empty(Directory.GetFiles(directory, "c.json.corrupt-*"));
            }

            Assert.Equal("first", Assert.Single(store.List()).Id);
            Assert.True(store.Add(Entry("second")));
            Assert.Equal(2, new TimeCorrectionStore(path).List().Count);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
