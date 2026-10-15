using System.Text.Json;
using System.Text.Json.Serialization;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

/// <summary>
/// Contract data per employee and the Kimai targets for absences go through
/// RuntimeSettings, the admin DTO, the mapping and the save (AGENTS
/// invariant). An old, cached admin client does not know them: its save must
/// keep them. A new client sends them completely (IncludesContract /
/// IncludesAbsences) and may clear them with null.
/// </summary>
public sealed class AdminSettingsAbsenceTests
{
    // Same options as RuntimeSettingsStore.
    private static readonly JsonSerializerOptions FileOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Same defaults as the minimal API request binding.
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static readonly EmployeeVacationYear Year2026 = new(2026, 3.5m, -2m, "Urlaub vor Einführung");

    private static EmployeeSettings Anna(Func<EmployeeSettings, EmployeeSettings>? change = null)
    {
        var anna = new EmployeeSettings
        {
            Id = "anna",
            DisplayName = "Anna",
            ApiToken = "anna-secret",
            PersonnelNumber = "1007",
            EntryDate = new DateOnly(2026, 3, 15),
            ExitDate = new DateOnly(2027, 6, 30),
            VacationDaysPerYear = 30m,
            AbsenceDayHours = 8m,
            VacationYears = [Year2026],
        };
        return change is null ? anna : change(anna);
    }

    private static RuntimeSettings Current(params EmployeeSettings[] employees) => new()
    {
        BaseUrl = "http://kimai.test",
        AbsenceProjectId = 40,
        VacationActivityId = 41,
        SickActivityId = 42,
        FlexDayActivityId = 43,
        AbsenceDayStart = "07:30",
        PauseEndRecoveryToleranceSeconds = 45,
        Employees = employees.Length == 0 ? [Anna()] : [.. employees],
    };

    private static AdminEmployeeUpdateDto EmployeeUpdate(
        bool includesContract,
        string? personnelNumber = null,
        DateOnly? entryDate = null,
        DateOnly? exitDate = null,
        decimal? vacationDaysPerYear = null,
        decimal? absenceDayHours = null,
        IReadOnlyCollection<EmployeeVacationYear>? vacationYears = null) => new(
        Id: "anna",
        KimaiUserId: null,
        DisplayName: "Anna",
        Pin: null,
        NfcCardId: null,
        ApiToken: null,
        KeepApiToken: true,
        ProjectId: null,
        ActivityId: null,
        Color: null,
        ImageUrl: null,
        Description: null,
        Tags: null,
        Billable: true,
        IsEnabled: true,
        IncludesContract: includesContract,
        PersonnelNumber: personnelNumber,
        EntryDate: entryDate,
        ExitDate: exitDate,
        VacationDaysPerYear: vacationDaysPerYear,
        AbsenceDayHours: absenceDayHours,
        VacationYears: vacationYears);

    private static AdminSettingsUpdateDto SettingsUpdate(
        AdminEmployeeUpdateDto employee,
        bool includesAbsences = false,
        int? absenceProjectId = null,
        int? vacationActivityId = null,
        int? sickActivityId = null,
        int? flexDayActivityId = null,
        string? absenceDayStart = null) => new(
        BaseUrl: "http://kimai.test",
        AdminPassword: null,
        AdminApiToken: null,
        KeepAdminApiToken: true,
        DefaultProjectId: null,
        DefaultActivityId: null,
        PauseActivityId: null,
        TelegramBotToken: null,
        TelegramChatId: null,
        Employees: [employee],
        IncludesAbsences: includesAbsences,
        AbsenceProjectId: absenceProjectId,
        VacationActivityId: vacationActivityId,
        SickActivityId: sickActivityId,
        FlexDayActivityId: flexDayActivityId,
        AbsenceDayStart: absenceDayStart);

    private static AdminService CreateAdminService() => new(null!, null!);

    [Fact]
    public void ExistingSettingsFile_WithoutTheNewFields_LoadsWithDefaults()
    {
        const string json = """
            {
              "baseUrl": "http://kimai.test",
              "employees": [ { "id": "anna", "displayName": "Anna", "apiToken": "t" } ]
            }
            """;

        var loaded = JsonSerializer.Deserialize<RuntimeSettings>(json, FileOptions)!;
        var anna = Assert.Single(loaded.Employees);

        Assert.Null(loaded.AbsenceProjectId);
        Assert.Equal("08:00", loaded.AbsenceDayStart);
        Assert.Equal(new TimeOnly(8, 0), loaded.AbsenceDayStartTime);
        Assert.False(loaded.AbsencesConfigured);
        Assert.Null(anna.PersonnelNumber);
        Assert.Null(anna.EntryDate);
        Assert.Null(anna.VacationDaysPerYear);
        Assert.Empty(anna.VacationYears);
        Assert.Null(CreateAdminService().ValidateAbsences(loaded));
    }

    [Fact]
    public void NullCollectionsAndStart_InTheSettingsFile_FallBackToDefaults()
    {
        const string json = """{"absenceDayStart":null,"employees":[{"id":"anna","vacationYears":null}]}""";

        var loaded = JsonSerializer.Deserialize<RuntimeSettings>(json, FileOptions)!;

        Assert.Equal("08:00", loaded.AbsenceDayStart);
        Assert.Empty(Assert.Single(loaded.Employees).VacationYears);
    }

    [Fact]
    public void Settings_SurviveTheSettingsFileRoundTrip()
    {
        var json = JsonSerializer.Serialize(Current(), FileOptions);
        var loaded = JsonSerializer.Deserialize<RuntimeSettings>(json, FileOptions)!;
        var anna = Assert.Single(loaded.Employees);

        Assert.Contains("\"entryDate\": \"2026-03-15\"", json);
        Assert.DoesNotContain("absencesConfigured", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("absenceDayStartTime", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((40, 41, 42, 43, "07:30"),
            (loaded.AbsenceProjectId, loaded.VacationActivityId, loaded.SickActivityId, loaded.FlexDayActivityId, loaded.AbsenceDayStart));
        Assert.True(loaded.AbsencesConfigured);
        Assert.Equal(("1007", new DateOnly(2026, 3, 15), new DateOnly(2027, 6, 30), 30m, 8m),
            (anna.PersonnelNumber, anna.EntryDate, anna.ExitDate, anna.VacationDaysPerYear, anna.AbsenceDayHours));
        Assert.Equal([Year2026], anna.VacationYears);
    }

    [Fact]
    public void FromSettings_ReturnsTheFieldsAndNeverTheToken()
    {
        var dto = AdminSettingsDto.FromSettings(Current());
        var anna = Assert.Single(dto.Employees);
        var json = JsonSerializer.Serialize(dto, WebOptions);

        Assert.Equal((40, 41, 42, 43, "07:30"),
            (dto.AbsenceProjectId, dto.VacationActivityId, dto.SickActivityId, dto.FlexDayActivityId, dto.AbsenceDayStart));
        Assert.Equal(("1007", new DateOnly(2026, 3, 15), new DateOnly(2027, 6, 30), 30m, 8m),
            (anna.PersonnelNumber, anna.EntryDate, anna.ExitDate, anna.VacationDaysPerYear, anna.AbsenceDayHours));
        Assert.Equal([Year2026], anna.VacationYears);
        Assert.Contains("\"exitDate\":\"2027-06-30\"", json);
        Assert.DoesNotContain("anna-secret", json);
    }

    [Fact]
    public void OldClientBody_WithoutTheNewFields_KeepsEverything()
    {
        // What a cached admin client of the previous release sends.
        const string body = """
            {
              "baseUrl": "http://kimai.test",
              "keepAdminApiToken": true,
              "employees": [ { "id": "anna", "displayName": "Anna", "keepApiToken": true, "billable": true, "isEnabled": true } ]
            }
            """;
        var update = JsonSerializer.Deserialize<AdminSettingsUpdateDto>(body, WebOptions)!;

        var result = update.ToSettings(Current());
        var anna = Assert.Single(result.Employees);

        Assert.Equal((40, 41, 42, 43, "07:30"),
            (result.AbsenceProjectId, result.VacationActivityId, result.SickActivityId, result.FlexDayActivityId, result.AbsenceDayStart));
        Assert.Equal(("1007", new DateOnly(2026, 3, 15), new DateOnly(2027, 6, 30), 30m, 8m),
            (anna.PersonnelNumber, anna.EntryDate, anna.ExitDate, anna.VacationDaysPerYear, anna.AbsenceDayHours));
        Assert.Equal([Year2026], anna.VacationYears);
        Assert.Equal("anna-secret", anna.ApiToken);
    }

    [Fact]
    public void GetThenPut_IsLossless()
    {
        var current = Current();
        var dto = AdminSettingsDto.FromSettings(current);
        var employee = Assert.Single(dto.Employees);
        var update = SettingsUpdate(
            EmployeeUpdate(true, employee.PersonnelNumber, employee.EntryDate, employee.ExitDate,
                employee.VacationDaysPerYear, employee.AbsenceDayHours, [.. employee.VacationYears]),
            true, dto.AbsenceProjectId, dto.VacationActivityId, dto.SickActivityId, dto.FlexDayActivityId, dto.AbsenceDayStart);

        var result = update.ToSettings(current);

        Assert.Equal(JsonSerializer.Serialize(current, FileOptions), JsonSerializer.Serialize(result, FileOptions));
    }

    [Fact]
    public void NewClient_ClearsTheFieldsWithNull()
    {
        var result = SettingsUpdate(EmployeeUpdate(true, personnelNumber: "  "), includesAbsences: true).ToSettings(Current());
        var anna = Assert.Single(result.Employees);

        Assert.Null(result.AbsenceProjectId);
        Assert.Null(result.FlexDayActivityId);
        Assert.Equal("08:00", result.AbsenceDayStart);
        Assert.False(result.AbsencesConfigured);
        Assert.Null(anna.PersonnelNumber);
        Assert.Null(anna.EntryDate);
        Assert.Null(anna.ExitDate);
        Assert.Null(anna.VacationDaysPerYear);
        Assert.Null(anna.AbsenceDayHours);
        // The vacation accounts follow their own rule: null keeps them.
        Assert.Equal([Year2026], anna.VacationYears);
    }

    [Fact]
    public void NewClient_TrimsTheValues()
    {
        var result = SettingsUpdate(
            EmployeeUpdate(true, personnelNumber: " 2001 ", vacationYears: [new EmployeeVacationYear(2027, 1m, 0m, "   "), Year2026 with { Note = " alt " }]),
            includesAbsences: true, absenceDayStart: " 06:45 ").ToSettings(Current());
        var anna = Assert.Single(result.Employees);

        Assert.Equal("06:45", result.AbsenceDayStart);
        Assert.Equal("2001", anna.PersonnelNumber);
        Assert.Equal([Year2026 with { Note = "alt" }, new EmployeeVacationYear(2027, 1m, 0m)], anna.VacationYears);
    }

    [Fact]
    public void VacationYears_EmptyListClears()
    {
        var anna = Assert.Single(SettingsUpdate(EmployeeUpdate(false, vacationYears: [])).ToSettings(Current()).Employees);

        Assert.Empty(anna.VacationYears);
        Assert.Equal(30m, anna.VacationDaysPerYear);
    }

    [Fact]
    public void NewEmployee_FromAnOldClient_StartsWithoutContractData()
    {
        var update = SettingsUpdate(EmployeeUpdate(false) with { Id = null });

        var created = Assert.Single(update.ToSettings(Current()).Employees);

        Assert.NotEqual("anna", created.Id);
        Assert.Null(created.EntryDate);
        Assert.Empty(created.VacationYears);
    }

    [Theory]
    [InlineData("8:00")]
    [InlineData("00:00")]
    [InlineData("23:59")]
    public void Validate_AcceptsValidStartTimes(string start)
    {
        Assert.Null(CreateAdminService().ValidateAbsences(new RuntimeSettings { AbsenceDayStart = start }));
    }

    [Theory]
    [InlineData("24:00")]
    [InlineData("8 Uhr")]
    [InlineData("08:00:00")]
    public void Validate_RejectsInvalidStartTimes(string start)
    {
        Assert.Equal("Beginn der Abwesenheitseinträge muss eine Uhrzeit (HH:mm) sein.",
            CreateAdminService().ValidateAbsences(new RuntimeSettings { AbsenceDayStart = start }));
    }

    [Fact]
    public void Validate_RejectsTheSameActivityForTwoKinds()
    {
        var settings = new RuntimeSettings { VacationActivityId = 41, SickActivityId = 42, FlexDayActivityId = 41 };

        Assert.Equal("Urlaub, Krank und Gleittag brauchen verschiedene Aktivitäten.", CreateAdminService().ValidateAbsences(settings));
    }

    [Theory]
    [InlineData(-1, "Anna: Urlaubstage pro Jahr müssen zwischen 0 und 60 liegen.")]
    [InlineData(60.5, "Anna: Urlaubstage pro Jahr müssen zwischen 0 und 60 liegen.")]
    public void Validate_RejectsVacationDaysOutOfRange(double days, string expected)
    {
        var settings = Current(Anna(anna => Copy(anna, vacationDaysPerYear: (decimal)days)));

        Assert.Equal(expected, CreateAdminService().ValidateAbsences(settings));
    }

    [Theory]
    [InlineData(-0.5)]
    [InlineData(24.25)]
    public void Validate_RejectsAbsenceHoursOutOfRange(double hours)
    {
        var settings = Current(Anna(anna => Copy(anna, absenceDayHours: (decimal)hours)));

        Assert.Equal("Anna: Stunden pro Abwesenheitstag müssen zwischen 0 und 24 liegen.", CreateAdminService().ValidateAbsences(settings));
    }

    [Fact]
    public void Validate_AcceptsTheRangeLimits()
    {
        Assert.Null(CreateAdminService().ValidateAbsences(Current(Anna(anna => Copy(anna, vacationDaysPerYear: 0m, absenceDayHours: 24m)))));
        Assert.Null(CreateAdminService().ValidateAbsences(Current(Anna(anna => Copy(anna, vacationDaysPerYear: 60m, absenceDayHours: 0m)))));
    }

    [Fact]
    public void Validate_RejectsAnExitBeforeTheEntry()
    {
        var settings = Current(Anna(anna => Copy(anna, exitDate: new DateOnly(2026, 3, 14))));

        Assert.Equal("Anna: Das Austrittsdatum liegt vor dem Eintrittsdatum.", CreateAdminService().ValidateAbsences(settings));
        Assert.Null(CreateAdminService().ValidateAbsences(Current(Anna(anna => Copy(anna, exitDate: new DateOnly(2026, 3, 15))))));
    }

    [Fact]
    public void Validate_RejectsDuplicateVacationYears()
    {
        var settings = Current(Anna(anna => Copy(anna, vacationYears: [Year2026, new EmployeeVacationYear(2026, 0m, 1m)])));

        Assert.Equal("Anna: Jedes Jahr darf im Urlaubskonto nur einmal vorkommen.", CreateAdminService().ValidateAbsences(settings));
    }

    [Fact]
    public void AbsencesConfigured_NeedsTheProjectAndAllThreeActivities()
    {
        Assert.True(Current().AbsencesConfigured);
        Assert.False(new RuntimeSettings { AbsenceProjectId = 40, VacationActivityId = 41, SickActivityId = 42 }.AbsencesConfigured);
        Assert.False(new RuntimeSettings { VacationActivityId = 41, SickActivityId = 42, FlexDayActivityId = 43 }.AbsencesConfigured);
    }

    private static EmployeeSettings Copy(
        EmployeeSettings source,
        decimal? vacationDaysPerYear = null,
        decimal? absenceDayHours = null,
        DateOnly? exitDate = null,
        EmployeeVacationYear[]? vacationYears = null) => new()
    {
        Id = source.Id,
        DisplayName = source.DisplayName,
        ApiToken = source.ApiToken,
        PersonnelNumber = source.PersonnelNumber,
        EntryDate = source.EntryDate,
        ExitDate = exitDate ?? source.ExitDate,
        VacationDaysPerYear = vacationDaysPerYear ?? source.VacationDaysPerYear,
        AbsenceDayHours = absenceDayHours ?? source.AbsenceDayHours,
        VacationYears = vacationYears ?? source.VacationYears,
    };
}
