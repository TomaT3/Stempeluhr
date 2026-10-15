using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;
using static Stempeluhr.Api.Tests.Absence;

namespace Stempeluhr.Api.Tests;

/// <summary>Where an absence day lands in Kimai: start time, after a night shift, zero duration, daylight saving.</summary>
public sealed class AbsencePlanTests
{
    private static readonly TimeOnly Eight = new(8, 0);
    private static readonly TimeSpan EightHours = TimeSpan.FromHours(8);

    private static (AbsencePlan.Placement? Placement, string? Error) Place(
        DateOnly day, TimeSpan duration, params KimaiTimesheetEntryDto[] entries)
        => AbsencePlan.Place(day, Eight, duration, Berlin, entries);

    [Fact]
    public void StartsAtTheConfiguredTime()
    {
        var placement = Place(D(10, 12), EightHours).Placement!;

        Assert.Equal((TimeCorrection.Day(12, 8), TimeCorrection.Day(12, 16)), (placement.Begin, placement.End));
    }

    [Fact]
    public void ANightShiftFromThePreviousDay_PushesTheStartToItsEnd()
    {
        // Ends 09:30:20 -> the absence starts at the next full minute.
        var nightShift = new KimaiTimesheetEntryDto(1, TimeCorrection.Day(11, 22), TimeCorrection.Day(12, 9, 30).AddSeconds(20), null, 1, 1, null);

        var placement = Place(D(10, 12), EightHours, nightShift).Placement!;

        Assert.Equal((TimeCorrection.Day(12, 9, 31), TimeCorrection.Day(12, 17, 31)), (placement.Begin, placement.End));
    }

    [Fact]
    public void EntriesThatEndBeforeTheStart_DoNotMoveIt()
    {
        var placement = Place(D(10, 12), EightHours, Entry(11, 22, 0, 12, 6, 0), Entry(12, 16, 0, 12, 18, 0, id: 2)).Placement!;

        Assert.Equal(TimeCorrection.Day(12, 8), placement.Begin);
    }

    [Fact]
    public void SeveralEntries_AreSkippedOneAfterTheOther()
    {
        var placement = Place(D(10, 12), EightHours, Entry(12, 7, 0, 12, 9, 0), Entry(12, 9, 0, 12, 10, 0, id: 2)).Placement!;

        Assert.Equal(TimeCorrection.Day(12, 10), placement.Begin);
    }

    [Fact]
    public void AFlexDay_HasZeroDuration_AndOnlyMovesWhenInsideAnEntry()
    {
        var plain = Place(D(10, 12), TimeSpan.Zero).Placement!;
        Assert.Equal(plain.Begin, plain.End);
        Assert.Equal(TimeCorrection.Day(12, 8), plain.Begin);

        // An entry that ends exactly at 08:00 does not contain it, one until 08:15 does.
        Assert.Equal(TimeCorrection.Day(12, 8), Place(D(10, 12), TimeSpan.Zero, Entry(12, 6, 0, 12, 8, 0)).Placement!.Begin);
        Assert.Equal(TimeCorrection.Day(12, 8, 15), Place(D(10, 12), TimeSpan.Zero, Entry(12, 6, 0, 12, 8, 15)).Placement!.Begin);
    }

    [Fact]
    public void ADayWithoutRoom_IsAnError()
    {
        var (placement, error) = Place(D(10, 12), EightHours, Entry(11, 20, 0, 12, 17, 0));

        Assert.Null(placement);
        Assert.Equal("Am 12.10.2026 ist kein Platz mehr für den Eintrag: bestehende Einträge reichen bis 17:00 Uhr.", error);
        // Exactly until midnight still fits.
        Assert.NotNull(Place(D(10, 12), EightHours, Entry(11, 20, 0, 12, 16, 0)).Placement);
    }

    [Fact]
    public void ARunningEntry_IsAnError()
    {
        var (placement, error) = Place(D(10, 12), EightHours, Entry(12, 6, 0, null));

        Assert.Null(placement);
        Assert.Contains("läuft gerade ein Eintrag", error);
    }

    [Fact]
    public void SpringForward_StartInTheGap_MovesToTheFirstValidMinute()
    {
        // 2027-03-28: 02:00 -> 03:00 in Berlin.
        var placement = AbsencePlan.Place(D(3, 28, 2027), new TimeOnly(2, 30), EightHours, Berlin, []).Placement!;

        Assert.Equal(new DateTimeOffset(2027, 3, 28, 3, 0, 0, TimeSpan.FromHours(2)), placement.Begin);
        Assert.Equal(EightHours, placement.End - placement.Begin);
    }

    [Fact]
    public void SpringForward_EightHoursAreEightRealHours()
    {
        var placement = AbsencePlan.Place(D(3, 28, 2027), Eight, EightHours, Berlin, []).Placement!;

        Assert.Equal(new DateTimeOffset(2027, 3, 28, 8, 0, 0, TimeSpan.FromHours(2)), placement.Begin);
        Assert.Equal(new DateTimeOffset(2027, 3, 28, 16, 0, 0, TimeSpan.FromHours(2)), placement.End);
    }

    [Fact]
    public void FallBack_TheLongDayHas25Hours_AndAnAmbiguousStartIsTheFirstOne()
    {
        // 2026-10-25: 03:00 -> 02:00 in Berlin. 24 real hours from midnight end at 23:00.
        var allDay = AbsencePlan.Place(D(10, 25), TimeOnly.MinValue, TimeSpan.FromHours(24), Berlin, []).Placement!;
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 23, 0, 0, TimeSpan.FromHours(1)), allDay.End);

        var ambiguous = AbsencePlan.Place(D(10, 25), new TimeOnly(2, 30), EightHours, Berlin, []).Placement!;
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.FromHours(2)), ambiguous.Begin);
    }

    [Fact]
    public void FindExisting_MatchesDayProjectAndActivity()
    {
        KimaiTimesheetEntryDto[] entries =
        [
            Entry(12, 8, 0, 12, 16, 0, SickActivity, Project, id: 7),
            Entry(13, 8, 0, 13, 16, 0, VacationActivity, Project, id: 8),
        ];

        Assert.Equal(8, AbsencePlan.FindExisting(D(10, 13), Project, VacationActivity, entries, Berlin)?.Id);
        Assert.Null(AbsencePlan.FindExisting(D(10, 12), Project, VacationActivity, entries, Berlin));
    }
}
