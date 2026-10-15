using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;
using static Stempeluhr.Api.Tests.Absence;

namespace Stempeluhr.Api.Tests;

/// <summary>Every rule of the concept, checked on submit and again on apply.</summary>
public sealed class AbsenceValidatorTests
{
    private static AbsenceValidationResult Validate(
        AbsenceKind kind,
        DateOnly[] days,
        bool isAdmin = false,
        EmployeeSettings? employee = null,
        RuntimeSettings? settings = null,
        AbsenceRequest[]? others = null,
        KimaiTimesheetEntryDto[]? timesheets = null,
        string? comment = null,
        bool applying = false)
    {
        employee ??= Max();
        return AbsenceValidator.Validate(new AbsenceValidationInput(
            kind, days, isAdmin, Today, employee, settings ?? Settings(employee),
            others ?? [], timesheets ?? [], Berlin, comment, applying));
    }

    [Fact]
    public void AValidRequest_Passes()
    {
        Assert.Equal(new AbsenceValidationResult(null), Validate(AbsenceKind.Vacation, [D(10, 12), D(10, 13)]));
    }

    [Theory]
    [InlineData(AbsenceKind.Vacation, -31, 548)]
    [InlineData(AbsenceKind.Sick, -31, 14)]
    [InlineData(AbsenceKind.FlexDay, -31, 365)]
    public void TheWindowPerKind_IsInclusive(AbsenceKind kind, int earliestOffset, int latestOffset)
    {
        var (earliest, latest) = AbsenceValidator.Window(kind, Today, isAdmin: false);

        Assert.Equal((Today.AddDays(earliestOffset), Today.AddDays(latestOffset)), (earliest, latest));
        Assert.Null(Validate(kind, [earliest]).Error);
        Assert.Null(Validate(kind, [latest]).Error);
        Assert.Equal($"Der {AbsenceKinds.Format(earliest.AddDays(-1))} liegt zu weit zurück (frühestens {AbsenceKinds.Format(earliest)}).",
            Validate(kind, [earliest.AddDays(-1)]).Error);
        Assert.Contains("zu weit in der Zukunft", Validate(kind, [latest.AddDays(1)]).Error);
    }

    [Fact]
    public void TheAdmin_MayGoBack90Days()
    {
        Assert.Null(Validate(AbsenceKind.Sick, [Today.AddDays(-90)], isAdmin: true).Error);
        Assert.Contains("zu weit zurück", Validate(AbsenceKind.Sick, [Today.AddDays(-91)], isAdmin: true).Error);
    }

    [Fact]
    public void EveryDay_LiesWithinTheEmployment()
    {
        var employee = Max(entryDate: D(10, 1), exitDate: D(10, 31));

        Assert.Equal("Der 30.09.2026 liegt vor dem Eintritt am 01.10.2026.", Validate(AbsenceKind.Sick, [D(9, 30)], employee: employee).Error);
        Assert.Equal("Der 01.11.2026 liegt nach dem Austritt am 31.10.2026.", Validate(AbsenceKind.Vacation, [D(11, 1)], employee: employee).Error);
        Assert.Null(Validate(AbsenceKind.Vacation, [D(10, 1), D(10, 31)], employee: employee).Error);
    }

    [Fact]
    public void ADay_HasAtMostOneOpenOrAppliedRequest_OfAnyKind()
    {
        AbsenceRequest Other(AbsenceStatus status) => Request(AbsenceKind.Sick, status, D(10, 13));

        foreach (var status in new[] { AbsenceStatus.Pending, AbsenceStatus.Failed, AbsenceStatus.Applied, AbsenceStatus.ResolvedManually })
        {
            Assert.Equal("Für den 13.10.2026 gibt es schon einen Antrag (Krank).",
                Validate(AbsenceKind.Vacation, [D(10, 12), D(10, 13)], others: [Other(status)]).Error);
        }
        foreach (var status in new[] { AbsenceStatus.Rejected, AbsenceStatus.Withdrawn, AbsenceStatus.Cancelled })
        {
            Assert.Null(Validate(AbsenceKind.Vacation, [D(10, 13)], others: [Other(status)]).Error);
        }
        // Another employee's request does not block.
        Assert.Null(Validate(AbsenceKind.Vacation, [D(10, 13)], others: [Other(AbsenceStatus.Applied) with { EmployeeId = "anna" }]).Error);
    }

    [Fact]
    public void ADayWithStartedWork_IsRejected_WithTheSickHint()
    {
        KimaiTimesheetEntryDto[] work = [Entry(5, 7, 0, 5, 9, 0)];

        Assert.Equal("Am 05.10.2026 wurde schon gearbeitet. Am Tag mit Arbeit bitte ab dem Folgetag krankmelden.",
            Validate(AbsenceKind.Sick, [D(10, 5)], timesheets: work).Error);
        Assert.Equal("Am 05.10.2026 wurde schon gearbeitet.", Validate(AbsenceKind.FlexDay, [D(10, 5)], timesheets: work).Error);
        Assert.Null(Validate(AbsenceKind.Sick, [D(10, 6)], timesheets: work).Error);
    }

    [Fact]
    public void ANightShiftFromThePreviousDay_DoesNotBlockTheDay()
    {
        KimaiTimesheetEntryDto[] nightShift = [Entry(4, 22, 0, 5, 6, 0)];

        Assert.Null(Validate(AbsenceKind.Sick, [D(10, 5)], timesheets: nightShift).Error);
        Assert.Contains("schon gearbeitet", Validate(AbsenceKind.Sick, [D(10, 4)], timesheets: nightShift).Error);
    }

    [Fact]
    public void PausesAreNoWork()
    {
        KimaiTimesheetEntryDto[] pause = [Entry(5, 7, 0, 5, 7, 30, activity: TimeCorrection.PauseActivity)];

        Assert.Null(Validate(AbsenceKind.Sick, [D(10, 5)], timesheets: pause).Error);
    }

    [Fact]
    public void AnAbsenceAlreadyInKimai_BlocksOnSubmit_ButTheSameKindBelongsToTheRequestOnApply()
    {
        KimaiTimesheetEntryDto[] vacation = [Entry(12, 8, 0, 12, 16, 0, VacationActivity, Project)];

        Assert.Equal("Für den 12.10.2026 ist in Kimai schon eine Abwesenheit (Urlaub) eingetragen.",
            Validate(AbsenceKind.Vacation, [D(10, 12)], timesheets: vacation).Error);
        Assert.Null(Validate(AbsenceKind.Vacation, [D(10, 12)], timesheets: vacation, applying: true).Error);
        Assert.Contains("(Urlaub) eingetragen", Validate(AbsenceKind.Sick, [D(10, 12)], timesheets: vacation, applying: true).Error);
    }

    [Fact]
    public void VacationOverTheRemainder_IsRejectedForTheEmployee_AndAWarningForTheAdmin()
    {
        var employee = Max(vacationDaysPerYear: 30m);
        AbsenceRequest[] others = [Request(AbsenceKind.Vacation, AbsenceStatus.Applied, [.. Enumerable.Range(0, 29).Select(i => D(1, 2).AddDays(i))])];

        Assert.Null(Validate(AbsenceKind.Vacation, [D(10, 12)], employee: employee, others: others).Error);
        Assert.Equal("Urlaub 2026: 2 Tage gewählt, der Rest beträgt nur 1 Tage.",
            Validate(AbsenceKind.Vacation, [D(10, 12), D(10, 13)], employee: employee, others: others).Error);

        var admin = Validate(AbsenceKind.Vacation, [D(10, 12), D(10, 13)], isAdmin: true, employee: employee, others: others);
        Assert.Equal(new AbsenceValidationResult(null, "Urlaub übersteigt den Rest 2026 um 1 Tage."), admin);
    }

    [Fact]
    public void TheRemainder_IsCheckedPerYear()
    {
        // Entry in December: 30 * 1 / 12 = 2.5 -> 3 days in 2026; 2027 has the full 30.
        var employee = Max(entryDate: D(12, 1));

        Assert.Null(Validate(AbsenceKind.Vacation, [D(12, 29), D(12, 30), D(12, 31), D(1, 4, 2027)], employee: employee).Error);
        Assert.Contains("Urlaub 2026: 4 Tage", Validate(AbsenceKind.Vacation, [D(12, 28), D(12, 29), D(12, 30), D(12, 31)], employee: employee).Error);
    }

    [Fact]
    public void SickAndFlexDays_DoNotCountAgainstTheVacation()
    {
        Assert.Null(Validate(AbsenceKind.FlexDay, [D(10, 12)], employee: Max(vacationDaysPerYear: 0m)).Error);
        Assert.Null(Validate(AbsenceKind.Sick, [D(10, 12)], employee: Max(vacationDaysPerYear: null)).Error);
    }

    [Fact]
    public void MissingSettings_AreNamed()
    {
        var bare = new RuntimeSettings { BaseUrl = "http://kimai.test", AbsenceDayStart = "8 Uhr" };

        Assert.Equal(
            "Urlaub ist noch nicht eingerichtet. Es fehlt: Abwesenheits-Projekt, Aktivität Urlaub, Beginn der Abwesenheitseinträge, "
            + "Stunden pro Abwesenheitstag des Mitarbeiters, Urlaubstage pro Jahr des Mitarbeiters.",
            Validate(AbsenceKind.Vacation, [D(10, 12)], employee: Max(vacationDaysPerYear: null, absenceDayHours: null), settings: bare).Error);
        // A flex day needs no hours and no vacation days.
        Assert.Null(Validate(AbsenceKind.FlexDay, [D(10, 12)], employee: Max(vacationDaysPerYear: null, absenceDayHours: null)).Error);
        Assert.Equal("Krank ist noch nicht eingerichtet. Es fehlt: Stunden pro Abwesenheitstag des Mitarbeiters.",
            Validate(AbsenceKind.Sick, [D(10, 12)], employee: Max(absenceDayHours: null)).Error);
    }

    [Fact]
    public void DaysAndComment_AreLimited()
    {
        var tooMany = Enumerable.Range(0, AbsenceValidator.MaxDays + 1).Select(i => D(11, 2).AddDays(i)).ToArray();

        Assert.Equal("Bitte mindestens einen Tag wählen.", Validate(AbsenceKind.FlexDay, []).Error);
        Assert.Equal("Ein Antrag umfasst höchstens 62 Tage.", Validate(AbsenceKind.FlexDay, tooMany).Error);
        Assert.Null(Validate(AbsenceKind.FlexDay, tooMany[..62]).Error);
        Assert.Equal("Der Kommentar darf höchstens 300 Zeichen haben.", Validate(AbsenceKind.Sick, [D(10, 6)], comment: new string('x', 301)).Error);
        Assert.Null(Validate(AbsenceKind.Sick, [D(10, 6)], comment: new string('x', 300)).Error);
    }
}
