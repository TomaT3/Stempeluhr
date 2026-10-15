using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;
using static Stempeluhr.Api.Tests.Absence;

namespace Stempeluhr.Api.Tests;

/// <summary>Entitlement per full calendar month of employment, § 5 (2) BUrlG rounding, and the account.</summary>
public sealed class VacationEntitlementCalculatorTests
{
    private static VacationBalanceDto Balance(EmployeeSettings employee, params AbsenceRequest[] requests)
        => VacationEntitlementCalculator.Calculate(employee, 2026, requests);

    [Fact]
    public void WholeYear_IsTheFullEntitlement()
    {
        var balance = Balance(Max(vacationDaysPerYear: 30m));

        Assert.Equal(new VacationBalanceDto(2026, 30m, 0m, 0m, 0m, 0m, 30m), balance);
    }

    [Fact]
    public void EntryOnTheFirst_CountsThatMonth()
    {
        // April to December = 9 months: 30 * 9 / 12 = 22.5 -> 23.
        Assert.Equal(23m, Balance(Max(entryDate: D(4, 1))).Entitlement);
        Assert.Equal(9, VacationEntitlementCalculator.FullMonths(Max(entryDate: D(4, 1)), 2026));
    }

    [Fact]
    public void EntryOnTheFifteenth_StartsWithTheNextMonth()
    {
        // The example from the concept: entry 15.03., 30 days -> April to December, 22.5 -> 23.
        Assert.Equal(9, VacationEntitlementCalculator.FullMonths(Max(entryDate: D(3, 15)), 2026));
        Assert.Equal(23m, Balance(Max(entryDate: D(3, 15))).Entitlement);
        // 25 days: 25 * 9 / 12 = 18.75 -> 19.
        Assert.Equal(19m, Balance(Max(vacationDaysPerYear: 25m, entryDate: D(3, 15))).Entitlement);
    }

    [Fact]
    public void ExitInTheMiddleOfAMonth_EndsWithThePreviousMonth()
    {
        // January to May = 5 months: 30 * 5 / 12 = 12.5 -> 13.
        Assert.Equal(5, VacationEntitlementCalculator.FullMonths(Max(exitDate: D(6, 15)), 2026));
        Assert.Equal(13m, Balance(Max(exitDate: D(6, 15))).Entitlement);
        // Leaving on the last day of the month keeps that month.
        Assert.Equal(15m, Balance(Max(exitDate: D(6, 30))).Entitlement);
    }

    [Fact]
    public void FractionsBelowAHalf_StayAsDecimals()
    {
        // 4.8 days per year, December only: 0.4 stays 0.4.
        Assert.Equal(0.4m, Balance(Max(vacationDaysPerYear: 4.8m, entryDate: D(12, 1))).Entitlement);
        // 25 * 1 / 12 = 2.0833 -> 2.08.
        Assert.Equal(2.08m, Balance(Max(vacationDaysPerYear: 25m, entryDate: D(12, 1))).Entitlement);
    }

    [Fact]
    public void NoEmploymentOrNoDaysPerYear_IsZero()
    {
        Assert.Equal(0m, Balance(Max(entryDate: D(1, 1, 2027))).Entitlement);
        Assert.Equal(0m, Balance(Max(exitDate: D(12, 31, 2025))).Entitlement);
        Assert.Equal(0m, Balance(Max(vacationDaysPerYear: null)).Entitlement);
    }

    [Fact]
    public void CarryOverAndCorrection_AreAddedForTheirYearOnly()
    {
        var employee = Max(vacationYears: [new(2026, 3.5m, -2m, "Urlaub vor Einführung"), new(2027, 10m, 0m)]);

        var balance = Balance(employee);

        Assert.Equal((3.5m, -2m, 31.5m), (balance.CarryOver, balance.Correction, balance.Remaining));
    }

    [Fact]
    public void Applied_IsTaken_AndPending_IsPlanned()
    {
        var balance = Balance(Max(),
            Request(AbsenceKind.Vacation, AbsenceStatus.Applied, D(8, 3), D(8, 4)),
            Request(AbsenceKind.Vacation, AbsenceStatus.ResolvedManually, D(8, 5)),
            Request(AbsenceKind.Vacation, AbsenceStatus.Pending, D(12, 23), D(12, 24)),
            Request(AbsenceKind.Vacation, AbsenceStatus.Failed, D(12, 28)));

        Assert.Equal((3m, 3m, 24m), (balance.Taken, balance.Planned, balance.Remaining));
    }

    [Fact]
    public void OnlyVacationDaysOfTheYearAndOfTheEmployeeCount()
    {
        var foreign = Request(AbsenceKind.Vacation, AbsenceStatus.Applied, D(8, 3)) with { EmployeeId = "anna" };

        var balance = Balance(Max(),
            Request(AbsenceKind.Vacation, AbsenceStatus.Applied, D(12, 31), D(1, 1, 2027)),
            Request(AbsenceKind.Sick, AbsenceStatus.Applied, D(8, 10)),
            Request(AbsenceKind.FlexDay, AbsenceStatus.Pending, D(8, 11)),
            Request(AbsenceKind.Vacation, AbsenceStatus.Rejected, D(9, 1)),
            Request(AbsenceKind.Vacation, AbsenceStatus.Withdrawn, D(9, 2)),
            Request(AbsenceKind.Vacation, AbsenceStatus.Cancelled, D(9, 3)),
            foreign);

        Assert.Equal((1m, 0m, 29m), (balance.Taken, balance.Planned, balance.Remaining));
    }

    [Fact]
    public void AsOf_SplitsTakenAndPlanned_ButKeepsTheRemainder()
    {
        var requests = new[] { Request(AbsenceKind.Vacation, AbsenceStatus.Applied, D(10, 30), D(11, 2)) };

        var balance = VacationEntitlementCalculator.Calculate(Max(), 2026, requests, asOf: D(10, 31));

        Assert.Equal((1m, 1m, 28m), (balance.Taken, balance.Planned, balance.Remaining));
    }
}
