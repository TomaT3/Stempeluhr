using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;
using static Stempeluhr.Api.Tests.Absence;

namespace Stempeluhr.Api.Tests;

public sealed class AbsenceStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"stempeluhr-absences-{Guid.NewGuid():N}");
    private readonly ManualClock _clock = new(TimeCorrection.Now);

    private string FilePath => Path.Combine(_directory, "absences.json");

    private AbsenceStore NewStore() => new(FilePath, clock: _clock);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void PersistsRequestsWithAllFields_AndAnotherInstanceReadsThemBack()
    {
        var request = Request(AbsenceKind.FlexDay, AbsenceStatus.Applied, D(10, 12), D(10, 13)) with
        {
            Comment = "Überstunden",
            DecidedAt = TimeCorrection.Now,
            DecidedBy = "Admin",
            AppliedDays = new Dictionary<string, int> { ["2026-10-12"] = 101, ["2026-10-13"] = 102 },
            TelegramChatId = -100123,
            TelegramMessageId = 77,
        };
        Assert.True(NewStore().Add(request));

        var read = Assert.Single(NewStore().List());
        Assert.Equal((AbsenceKind.FlexDay, AbsenceStatus.Applied, "Überstunden"), (read.Kind, read.Status, read.Comment));
        Assert.Equal([D(10, 12), D(10, 13)], read.Days);
        Assert.Equal(102, read.AppliedDays["2026-10-13"]);
        Assert.Equal((-100123L, 77L), (read.TelegramChatId, read.TelegramMessageId));
        var json = File.ReadAllText(FilePath);
        Assert.Contains("\"flexDay\"", json);
        Assert.Contains("\"2026-10-12\"", json);
        Assert.DoesNotContain("blocksDays", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Add_RefusesADuplicateId_AndUpdateAnUnknownOne()
    {
        var store = NewStore();
        var request = Request(AbsenceKind.Sick, AbsenceStatus.Pending, D(10, 6));

        Assert.True(store.Add(request));
        Assert.False(store.Add(request));
        Assert.False(store.Update(request with { Id = "unknown" }));
    }

    [Fact]
    public void Update_KeepsTheTelegramMessage()
    {
        var store = NewStore();
        var request = Request(AbsenceKind.Vacation, AbsenceStatus.Pending, D(10, 12));
        store.Add(request);
        store.AttachTelegramMessage(request.Id, -100, 5);

        store.Update(request with { Status = AbsenceStatus.Rejected });

        Assert.Equal((AbsenceStatus.Rejected, 5L), (store.Find(request.Id)!.Status, store.Find(request.Id)!.TelegramMessageId));
    }

    [Fact]
    public void CorruptFile_IsQuarantined_AndTheStoreStartsEmpty()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ not json");

        var store = NewStore();

        Assert.Empty(store.List());
        Assert.Single(Directory.GetFiles(_directory, "absences.json.corrupt-*"));
        Assert.True(store.Add(Request(AbsenceKind.Sick, AbsenceStatus.Pending, D(10, 6))));
        Assert.Single(NewStore().List());
    }

    [Fact]
    public void Retention_DropsOnlyOldRejectedAndWithdrawnRequests()
    {
        var old = TimeCorrection.Now - AbsenceStore.DiscardedRetention - TimeSpan.FromDays(1);
        var recent = TimeCorrection.Now - AbsenceStore.DiscardedRetention + TimeSpan.FromDays(1);
        var store = NewStore();
        AbsenceRequest Old(AbsenceStatus status) =>
            Request(AbsenceKind.Vacation, status, D(1, 5, 2023)) with { CreatedAt = old, DecidedAt = old };

        foreach (var status in Enum.GetValues<AbsenceStatus>())
        {
            store.Add(Old(status));
        }
        store.Add(Request(AbsenceKind.Vacation, AbsenceStatus.Rejected, D(1, 5, 2023)) with { CreatedAt = old, DecidedAt = recent });

        var kept = NewStore().List().Select(request => request.Status).Order().ToArray();
        Assert.Equal(
            [AbsenceStatus.Pending, AbsenceStatus.Applied, AbsenceStatus.Rejected, AbsenceStatus.Failed,
             AbsenceStatus.ResolvedManually, AbsenceStatus.Cancelled],
            kept);
    }
}
