using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;

namespace Stempeluhr.Api.Tests;

/// <summary>
/// Shared fixtures for the absence tests. The clock, Kimai and the employees
/// come from <see cref="TimeCorrection"/>: today is Monday 2026-10-05 in Berlin.
/// </summary>
internal static class Absence
{
    public const int Project = 40;
    public const int VacationActivity = 41;
    public const int SickActivity = 42;
    public const int FlexActivity = 43;

    public static readonly DateOnly Today = new(2026, 10, 5);
    public static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    public static DateOnly D(int month, int day, int year = 2026) => new(year, month, day);

    public static RuntimeSettings Settings(params EmployeeSettings[] employees) => new()
    {
        BaseUrl = "http://kimai.test",
        DefaultProjectId = TimeCorrection.WorkProject,
        DefaultActivityId = TimeCorrection.WorkActivity,
        PauseActivityId = TimeCorrection.PauseActivity,
        AbsenceProjectId = Project,
        VacationActivityId = VacationActivity,
        SickActivityId = SickActivity,
        FlexDayActivityId = FlexActivity,
        Employees = employees.Length == 0 ? [Max()] : [.. employees],
    };

    public static EmployeeSettings Max(
        decimal? vacationDaysPerYear = 30m,
        decimal? absenceDayHours = 8m,
        DateOnly? entryDate = null,
        DateOnly? exitDate = null,
        EmployeeVacationYear[]? vacationYears = null) => new()
    {
        Id = "max",
        DisplayName = "Max Mustermann",
        Pin = "1234",
        NfcCardId = "04A2B3C4",
        ApiToken = "max-token",
        ProjectId = TimeCorrection.WorkProject,
        ActivityId = TimeCorrection.WorkActivity,
        Tasks = [new EmployeeTaskSettings { Id = "kx", Label = "Kunde X", ProjectId = 5, ActivityId = 6 }],
        PersonnelNumber = "1001",
        VacationDaysPerYear = vacationDaysPerYear,
        AbsenceDayHours = absenceDayHours,
        EntryDate = entryDate,
        ExitDate = exitDate,
        VacationYears = vacationYears ?? [],
    };

    public static EmployeeSettings Anna() => new()
    {
        Id = "anna",
        DisplayName = "Anna Beispiel",
        Pin = "5678",
        NfcCardId = "04D5E6F7",
        ApiToken = "anna-token",
        VacationDaysPerYear = 25m,
        AbsenceDayHours = 6m,
    };

    public static AbsenceRequest Request(
        AbsenceKind kind,
        AbsenceStatus status,
        params DateOnly[] days) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        EmployeeId = "max",
        EmployeeName = "Max Mustermann",
        Kind = kind,
        Days = days,
        Source = "clock",
        CreatedAt = TimeCorrection.Now,
        CreatedBy = "Max Mustermann",
        Status = status,
    };

    /// <summary>Kimai entry with Berlin local times on 2026-10-dd.</summary>
    public static KimaiTimesheetEntryDto Entry(
        int day, int fromHour, int fromMinute, int? toDay, int toHour = 0, int toMinute = 0,
        int activity = TimeCorrection.WorkActivity, int project = TimeCorrection.WorkProject, int id = 1)
        => new(
            id,
            TimeCorrection.Day(day, fromHour, fromMinute),
            toDay is { } end ? TimeCorrection.Day(end, toHour, toMinute) : null,
            null, activity, project, null);
}

/// <summary>Settings that can be saved, for the vacation account updates.</summary>
internal sealed class MemorySettingsStore(RuntimeSettings settings) : IRuntimeSettingsStore
{
    // Like RuntimeSettingsStore: every Load is a fresh copy.
    private string _json = System.Text.Json.JsonSerializer.Serialize(settings, Options);

    private static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web);

    public int Saves { get; private set; }

    public RuntimeSettings Load() => System.Text.Json.JsonSerializer.Deserialize<RuntimeSettings>(_json, Options)!;

    public Task SaveAsync(RuntimeSettings settings, CancellationToken cancellationToken = default)
    {
        _json = System.Text.Json.JsonSerializer.Serialize(settings, Options);
        Saves++;
        return Task.CompletedTask;
    }
}
