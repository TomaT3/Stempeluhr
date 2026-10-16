using System.Net;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;
using static Stempeluhr.Api.Tests.Absence;

namespace Stempeluhr.Api.Tests;

public sealed class MonthlyExportServiceTests : IDisposable
{
    private const int Work = TimeCorrection.WorkActivity;
    private const int Pause = TimeCorrection.PauseActivity;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"stempeluhr-export-{Guid.NewGuid():N}");
    private readonly ExportKimai _kimai = new();
    private readonly AbsenceStore _store;

    public MonthlyExportServiceTests()
    {
        _store = new AbsenceStore(Path.Combine(_directory, "absences.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private MonthlyExportService Service(RuntimeSettings settings)
        => new(new StubSettingsStore(settings), _kimai, _store);

    private static DateTimeOffset At(int month, int day, int hour, int minute = 0, int year = 2026)
    {
        var local = new DateTime(year, month, day, hour, minute, 0);
        return new DateTimeOffset(local, Berlin.GetUtcOffset(local));
    }

    private static KimaiTimesheetEntryDto Sheet(
        int id, DateTimeOffset begin, DateTimeOffset? end, int activity = Work, int project = TimeCorrection.WorkProject)
        => new(id, begin, end, end is null ? 0 : (int)(end.Value - begin).TotalSeconds, activity, project, null);

    private static KimaiTimesheetEntryDto Vacation(int id, int day, int month = 10)
        => Sheet(id, At(month, day, 8), At(month, day, 16), VacationActivity, Project);

    [Fact]
    public async Task Report_SumsWorkPauseAndAbsencesSeparately()
    {
        _kimai.Entries["max-token"] =
        [
            Sheet(1, At(10, 5, 8), At(10, 5, 12)),
            Sheet(2, At(10, 5, 12), At(10, 5, 12, 30), Pause),
            Sheet(3, At(10, 5, 12, 30), At(10, 5, 16, 30)),
            Sheet(4, At(10, 6, 8), At(10, 6, 12)),
            Vacation(5, 7),
            Vacation(6, 12),
            Sheet(7, At(10, 8, 8), At(10, 8, 16), SickActivity, Project),
            Sheet(8, At(10, 9, 8), At(10, 9, 8), FlexActivity, Project),
        ];
        var report = await Service(Settings(Max())).BuildReportAsync(2026, 10);

        var summary = Assert.Single(report.Employees).Summary;
        Assert.Equal("1001", summary.PersonnelNumber);
        Assert.Equal(2, summary.WorkDays);
        Assert.Equal(12m, summary.WorkHours);
        Assert.Equal(2, summary.VacationDays);
        Assert.Equal(1, summary.SickDays);
        Assert.Equal(1, summary.FlexDays);
        Assert.Equal(24m, summary.VacationSickHours);
        Assert.Empty(summary.Notes);
    }

    [Fact]
    public async Task Report_BuildsOneShiftFromWorkAndPauseEntries()
    {
        _kimai.Entries["max-token"] =
        [
            Sheet(1, At(10, 5, 8), At(10, 5, 12)),
            Sheet(2, At(10, 5, 12), At(10, 5, 12, 30), Pause),
            Sheet(3, At(10, 5, 12, 30), At(10, 5, 16, 30), 6, 5),
            Sheet(4, At(10, 6, 8), At(10, 6, 12)),
        ];
        var report = await Service(Settings(Max())).BuildReportAsync(2026, 10);

        var shifts = Assert.Single(report.Employees).Shifts;
        Assert.Equal(2, shifts.Count);
        Assert.Equal(new DateOnly(2026, 10, 5), shifts[0].Day);
        Assert.Equal(new TimeOnly(8, 0), shifts[0].Begin);
        Assert.Equal(new TimeOnly(16, 30), shifts[0].End);
        Assert.Equal(30, shifts[0].PauseMinutes);
        Assert.Equal(8m, shifts[0].NetHours);
        Assert.Equal("Arbeit, Kunde X", shifts[0].Task);
        Assert.Equal(new DateOnly(2026, 10, 6), shifts[1].Day);
    }

    [Fact]
    public async Task Report_CountsANightShiftOnlyInTheMonthItBegins()
    {
        _kimai.Entries["max-token"] =
        [
            Sheet(1, At(9, 30, 22), At(10, 1, 6)),   // begins in September
            Sheet(2, At(10, 31, 22), At(11, 1, 6)),  // begins in October, ends in November
        ];
        var service = Service(Settings(Max()));

        var october = Assert.Single((await service.BuildReportAsync(2026, 10)).Employees);
        Assert.Equal(1, october.Summary.WorkDays);
        Assert.Equal(8m, october.Summary.WorkHours);
        var shift = Assert.Single(october.Shifts);
        Assert.Equal(new DateOnly(2026, 10, 31), shift.Day);
        Assert.Equal(new TimeOnly(22, 0), shift.Begin);
        Assert.Equal(new TimeOnly(6, 0), shift.End);

        var november = Assert.Single((await service.BuildReportAsync(2026, 11)).Employees);
        Assert.Equal(0, november.Summary.WorkDays);
        Assert.Equal(0m, november.Summary.WorkHours);
        Assert.Empty(november.Shifts);

        var september = Assert.Single((await service.BuildReportAsync(2026, 9)).Employees);
        Assert.Equal(1, september.Summary.WorkDays);
        Assert.Equal(8m, september.Summary.WorkHours);
    }

    [Fact]
    public async Task Report_UsesTheMonthBoundsInTheEmployeesTimeZone()
    {
        // 23:30 UTC on 30 Sep is 01:30 on 1 Oct in Berlin: October.
        _kimai.Entries["max-token"] = [Sheet(1, new DateTimeOffset(2026, 9, 30, 23, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 1, 3, 30, 0, TimeSpan.Zero))];

        var summary = Assert.Single((await Service(Settings(Max())).BuildReportAsync(2026, 10)).Employees).Summary;

        Assert.Equal(1, summary.WorkDays);
        Assert.Equal(4m, summary.WorkHours);
        Assert.Equal(new DateTime(2026, 10, 1), _kimai.LastBegin);
        Assert.Equal(new DateTime(2026, 10, 31, 23, 59, 59), _kimai.LastEnd);
    }

    [Fact]
    public async Task Report_ShowsAVacationEntryCreatedDirectlyInKimai()
    {
        _kimai.Entries["max-token"] = [Vacation(5, 7)];

        var employee = Assert.Single((await Service(Settings(Max())).BuildReportAsync(2026, 10)).Employees);

        Assert.Equal(1, employee.Summary.VacationDays);
        Assert.Equal(0, employee.Summary.WorkDays);
        var absence = Assert.Single(employee.Absences);
        Assert.Equal(new DateOnly(2026, 10, 7), absence.Day);
        Assert.Equal("Urlaub", absence.Kind);
        Assert.Equal(8m, absence.Hours);
        Assert.Equal("direkt in Kimai", absence.RecordedBy);
        Assert.Empty(employee.Summary.Notes);
    }

    [Fact]
    public async Task Report_EnrichesAKimaiAbsenceWithItsRequest()
    {
        _store.Add(Request(AbsenceKind.Vacation, AbsenceStatus.Applied, D(10, 7)) with
        {
            CreatedBy = "Chefin",
            AppliedDays = new Dictionary<string, int> { ["2026-10-07"] = 5 },
        });
        _kimai.Entries["max-token"] = [Vacation(5, 7)];

        var employee = Assert.Single((await Service(Settings(Max())).BuildReportAsync(2026, 10)).Employees);

        var absence = Assert.Single(employee.Absences);
        Assert.Equal("Genehmigt", absence.Status);
        Assert.Equal("Chefin", absence.RecordedBy);
        Assert.Empty(employee.Summary.Notes);
    }

    [Fact]
    public async Task Report_KeepsAnEmployeeWhoseKimaiFailsAndContinues()
    {
        _kimai.Entries["max-token"] = [Sheet(1, At(10, 5, 8), At(10, 5, 12))];
        _kimai.FailingTokens.Add("anna-token");

        var report = await Service(Settings(Max(), Anna())).BuildReportAsync(2026, 10);

        Assert.Equal(2, report.Employees.Count);
        var anna = report.Employees.Single(employee => employee.Summary.Name == "Anna Beispiel").Summary;
        Assert.Null(anna.WorkDays);
        Assert.Null(anna.WorkHours);
        Assert.Contains(anna.Notes, note => note.StartsWith("Kimai nicht erreichbar"));
        var max = report.Employees.Single(employee => employee.Summary.Name == "Max Mustermann").Summary;
        Assert.Equal(4m, max.WorkHours);
        // The balance does not depend on Kimai.
        Assert.Equal(25m, anna.RemainingVacation);
    }

    [Fact]
    public async Task Export_StillProducesTheFileWhenKimaiIsDownForEveryone()
    {
        _kimai.FailingTokens.Add("max-token");
        var file = await Service(Settings(Max())).ExportAsync(2026, 10);
        var reader = new XlsxTestReader(file.Content);

        Assert.Equal("Max Mustermann", reader.Cell("Übersicht", "B2"));
        Assert.Null(reader.Cell("Übersicht", "C2"));
        Assert.StartsWith("Kimai nicht erreichbar", reader.Cell("Übersicht", "J2"));
    }

    [Fact]
    public async Task Report_NotesRequestsWithoutKimaiEntryAndOpenOrFailedRequests()
    {
        _store.Add(Request(AbsenceKind.Vacation, AbsenceStatus.Applied, D(10, 7), D(10, 8)));
        _store.Add(Request(AbsenceKind.Sick, AbsenceStatus.Pending, D(10, 14)));
        _store.Add(Request(AbsenceKind.Vacation, AbsenceStatus.Failed, D(10, 20), D(11, 2)));
        _store.Add(Request(AbsenceKind.Vacation, AbsenceStatus.Rejected, D(10, 21)));
        _store.Add(Request(AbsenceKind.Vacation, AbsenceStatus.Pending, D(11, 9)));
        _kimai.Entries["max-token"] = [Vacation(5, 7)];

        var notes = Assert.Single((await Service(Settings(Max())).BuildReportAsync(2026, 10)).Employees).Summary.Notes;

        Assert.Contains("Urlaub laut Antrag ohne Kimai-Eintrag: 08.10.2026.", notes);
        Assert.Contains("Offener Antrag (Krank): 14.10.2026.", notes);
        Assert.Contains("Fehlgeschlagener Antrag (Urlaub): 20.10.2026.", notes);
        Assert.Equal(3, notes.Count);
    }

    [Fact]
    public async Task Report_NotesAnEntryThatIsStillRunningAndDoesNotCountIt()
    {
        _kimai.Entries["max-token"] = [Sheet(1, At(10, 5, 8), At(10, 5, 12)), Sheet(2, At(10, 6, 8), null)];

        var employee = Assert.Single((await Service(Settings(Max())).BuildReportAsync(2026, 10)).Employees);

        Assert.Equal(2, employee.Summary.WorkDays);
        Assert.Equal(4m, employee.Summary.WorkHours);
        Assert.Contains("Ein Eintrag läuft noch: seine Stunden sind nicht gezählt.", employee.Summary.Notes);
        Assert.Null(employee.Shifts[1].End);
    }

    [Fact]
    public async Task Report_NotesARunningAbsenceEntryAsIncomplete()
    {
        _kimai.Entries["max-token"] = [Sheet(1, At(10, 7, 8), null, VacationActivity, Project)];

        var employee = Assert.Single((await Service(Settings(Max())).BuildReportAsync(2026, 10)).Employees);

        Assert.Contains("Ein Eintrag läuft noch: seine Stunden sind nicht gezählt.", employee.Summary.Notes);
        Assert.Equal("läuft noch (nicht abgeschlossen)", Assert.Single(employee.Absences).Status);
        Assert.Equal(0m, employee.Summary.VacationSickHours);
    }

    [Fact]
    public async Task Report_WithPartialAbsenceSettings_EvaluatesNoAbsencesAndNoWork()
    {
        var settings = Settings(Max());
        var partial = new RuntimeSettings
        {
            BaseUrl = settings.BaseUrl,
            DefaultProjectId = settings.DefaultProjectId,
            DefaultActivityId = settings.DefaultActivityId,
            PauseActivityId = settings.PauseActivityId,
            AbsenceProjectId = settings.AbsenceProjectId,
            VacationActivityId = settings.VacationActivityId,
            Employees = settings.Employees,
        };
        _store.Add(Request(AbsenceKind.Vacation, AbsenceStatus.Applied, D(10, 7)));
        _kimai.Entries["max-token"] = [Vacation(5, 7)];

        var report = await Service(partial).BuildReportAsync(2026, 10);

        var employee = Assert.Single(report.Employees);
        Assert.Empty(employee.Absences);
        Assert.Equal(0, employee.Summary.VacationDays);
        Assert.Equal(0, employee.Summary.WorkDays);
        Assert.Equal(0m, employee.Summary.VacationSickHours);
        Assert.DoesNotContain(employee.Summary.Notes, note => note.Contains("ohne Kimai-Eintrag"));
        Assert.Contains(report.Notes, note => note.StartsWith("Abwesenheits-Einstellungen unvollständig"));
    }

    [Fact]
    public async Task Report_WithoutAbsenceSettingsHasNoAbsencesAndExplainsWhy()
    {
        var settings = Settings(Max());
        var unconfigured = new RuntimeSettings
        {
            BaseUrl = settings.BaseUrl,
            DefaultProjectId = settings.DefaultProjectId,
            DefaultActivityId = settings.DefaultActivityId,
            PauseActivityId = settings.PauseActivityId,
            Employees = settings.Employees,
        };
        _kimai.Entries["max-token"] = [Vacation(5, 7)];

        var report = await Service(unconfigured).BuildReportAsync(2026, 10);

        Assert.Empty(Assert.Single(report.Employees).Absences);
        Assert.Contains(report.Notes, note => note.StartsWith("Abwesenheits-Einstellungen unvollständig"));
        var reader = new XlsxTestReader(XlsxWriter.Write(MonthlyExportService.ToSheets(report)));
        Assert.StartsWith("Abwesenheits-Einstellungen", reader.Cell("Übersicht", "J3"));
    }

    [Fact]
    public async Task Report_ListsOnlyActiveEmployeesEmployedInTheMonth()
    {
        var left = Anna();
        var settings = Settings(
            Max(),
            Max(exitDate: new DateOnly(2026, 9, 30)).WithId("gone"),
            Max(entryDate: new DateOnly(2026, 11, 1)).WithId("later"),
            Max(entryDate: new DateOnly(2026, 10, 31), exitDate: new DateOnly(2026, 10, 31)).WithId("lastday"),
            left);
        settings.Employees[4] = Disabled(left);

        var report = await Service(settings).BuildReportAsync(2026, 10);

        Assert.Equal(2, report.Employees.Count);
    }

    [Fact]
    public async Task Report_ComputesTheRemainingVacationAtMonthEnd()
    {
        _store.Add(Request(AbsenceKind.Vacation, AbsenceStatus.Applied, D(10, 7), D(10, 8)));
        _store.Add(Request(AbsenceKind.Vacation, AbsenceStatus.Applied, D(11, 2)));
        _store.Add(Request(AbsenceKind.Vacation, AbsenceStatus.Pending, D(12, 1)));

        var summary = Assert.Single((await Service(Settings(Max())).BuildReportAsync(2026, 10)).Employees).Summary;

        // 30 - 2 taken - 2 planned.
        Assert.Equal(26m, summary.RemainingVacation);
    }

    [Fact]
    public async Task Report_LeavesTheRemainingVacationEmptyWithoutAnAnnualEntitlement()
    {
        var summary = Assert.Single((await Service(Settings(Max(vacationDaysPerYear: null))).BuildReportAsync(2026, 10)).Employees).Summary;

        Assert.Null(summary.RemainingVacation);
    }

    [Fact]
    public async Task Export_WritesThreeSheetsWithSummaryRow()
    {
        _kimai.Entries["max-token"] =
        [
            Sheet(1, At(10, 5, 8), At(10, 5, 12)),
            Sheet(2, At(10, 5, 12), At(10, 5, 12, 30), Pause),
            Sheet(3, At(10, 5, 12, 30), At(10, 5, 16, 30)),
            Vacation(5, 7),
        ];
        _kimai.Entries["anna-token"] = [Sheet(9, At(10, 6, 9), At(10, 6, 13), Work, 1)];

        var file = await Service(Settings(Max(), Anna())).ExportAsync(2026, 10);

        Assert.Equal("Stempeluhr-Export-2026-10.xlsx", file.FileName);
        var reader = new XlsxTestReader(file.Content);
        Assert.Equal(["Übersicht", "Abwesenheiten", "Arbeitszeiten"], reader.SheetNames);

        // Anna has no personnel number and sorts first.
        Assert.Equal("Anna Beispiel", reader.Cell("Übersicht", "B2"));
        Assert.Null(reader.Cell("Übersicht", "A2"));
        Assert.Equal("1001", reader.Cell("Übersicht", "A3"));
        Assert.Equal("Max Mustermann", reader.Cell("Übersicht", "B3"));
        Assert.Equal("1", reader.Cell("Übersicht", "C3"));
        Assert.Equal("8", reader.Cell("Übersicht", "D3"));
        Assert.Equal("1", reader.Cell("Übersicht", "E3"));
        Assert.Equal("8", reader.Cell("Übersicht", "H3"));
        Assert.Equal("30", reader.Cell("Übersicht", "I3"));
        Assert.Equal("Summe", reader.Cell("Übersicht", "B4"));
        Assert.Equal("2", reader.Cell("Übersicht", "C4"));
        Assert.Equal("12", reader.Cell("Übersicht", "D4"));
        Assert.True(reader.IsBold("Übersicht", "B4"));

        Assert.Equal("Urlaub", reader.Cell("Abwesenheiten", "D2"));
        Assert.Equal("direkt in Kimai", reader.Cell("Abwesenheiten", "G2"));
        Assert.Equal("dd.MM.yyyy", reader.FormatCode("Abwesenheiten", "C2"));

        Assert.Equal(3, reader.RowCount("Arbeitszeiten"));
        Assert.Equal("Anna Beispiel", reader.Cell("Arbeitszeiten", "B2"));
        Assert.Equal("HH:mm", reader.FormatCode("Arbeitszeiten", "D2"));
        Assert.Equal("30", reader.Cell("Arbeitszeiten", "F3"));
        Assert.Equal("8", reader.Cell("Arbeitszeiten", "G3"));
    }

    [Fact]
    public async Task Report_RunsAtMostFourEmployeesAtOnce()
    {
        var employees = Enumerable.Range(1, 10)
            .Select(index => Anna().WithId($"e{index}", $"token-{index}"))
            .ToArray();
        _kimai.Delay = TimeSpan.FromMilliseconds(20);

        await Service(Settings(employees)).BuildReportAsync(2026, 10);

        Assert.Equal(4, _kimai.MaxConcurrent);
    }

    private static EmployeeSettings Disabled(EmployeeSettings employee) => employee.WithId(employee.Id, employee.ApiToken, enabled: false);

    /// <summary>Kimai stub: entries per token, failing tokens, call recording for the concurrency limit.</summary>
    private sealed class ExportKimai : IKimaiClient
    {
        private int _running;

        public Dictionary<string, KimaiTimesheetEntryDto[]> Entries { get; } = [];
        public HashSet<string> FailingTokens { get; } = [];
        public TimeSpan Delay { get; set; }
        public int MaxConcurrent { get; private set; }
        public DateTime? LastBegin { get; private set; }
        public DateTime? LastEnd { get; private set; }

        public Task<string?> GetCurrentUserTimezoneAsync(RuntimeSettings settings, EmployeeSettings employee, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("Europe/Berlin");

        public async Task<IReadOnlyCollection<KimaiTimesheetEntryDto>> GetTimesheetsAsync(
            RuntimeSettings settings, EmployeeSettings employee, DateTime begin, DateTime end, CancellationToken cancellationToken = default)
        {
            var now = Interlocked.Increment(ref _running);
            try
            {
                lock (this)
                {
                    MaxConcurrent = Math.Max(MaxConcurrent, now);
                    if (employee.ApiToken == "max-token")
                    {
                        (LastBegin, LastEnd) = (begin, end);
                    }
                }
                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, cancellationToken);
                }
                if (FailingTokens.Contains(employee.ApiToken))
                {
                    throw new KimaiApiException(HttpStatusCode.InternalServerError, "{}", "GET /api/timesheets");
                }
                // Like a loose Kimai filter: the service must apply the month itself.
                return Entries.TryGetValue(employee.ApiToken, out var entries) ? entries : [];
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        public Task<ClockStatusDto> GetStatusAsync(RuntimeSettings settings, EmployeeSettings employee, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StartAsync(RuntimeSettings settings, EmployeeSettings employee, KimaiTimesheetTarget target, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StartAtAsync(RuntimeSettings settings, EmployeeSettings employee, KimaiTimesheetTarget target, DateTimeOffset startedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAsync(RuntimeSettings settings, EmployeeSettings employee, int timesheetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAtAsync(RuntimeSettings settings, EmployeeSettings employee, int timesheetId, DateTimeOffset stoppedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task BackdateEndAsync(RuntimeSettings settings, EmployeeSettings employee, int timesheetId, DateTimeOffset endedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<KimaiRecentTimesheetDto>> GetRecentStoppedTimesheetsAsync(RuntimeSettings settings, EmployeeSettings employee, int count, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int?> GetCurrentUserIdAsync(RuntimeSettings settings, EmployeeSettings employee, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<KimaiUserDto>> GetUsersAsync(string baseUrl, string apiToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<KimaiActivityDto>> GetActivitiesAsync(string baseUrl, string apiToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<KimaiProjectDto>> GetProjectsAsync(string baseUrl, string apiToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<KimaiTimesheetDetailDto?> GetTimesheetAsync(RuntimeSettings settings, EmployeeSettings employee, int timesheetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> CreateTimesheetAsync(RuntimeSettings settings, EmployeeSettings employee, KimaiTimesheetTarget target, DateTimeOffset begin, DateTimeOffset end, string? description, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateTimesheetTimesAsync(RuntimeSettings settings, EmployeeSettings employee, int timesheetId, DateTimeOffset? begin, DateTimeOffset? end, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteTimesheetAsync(RuntimeSettings settings, EmployeeSettings employee, int timesheetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

internal static class ExportEmployeeExtensions
{
    /// <summary>A copy with another id/token (EmployeeSettings has init-only properties and no copy constructor).</summary>
    public static EmployeeSettings WithId(this EmployeeSettings employee, string id, string? token = null, bool enabled = true) => new()
    {
        Id = id,
        DisplayName = employee.DisplayName,
        Pin = null,
        NfcCardId = null,
        ApiToken = token ?? employee.ApiToken,
        ProjectId = employee.ProjectId,
        ActivityId = employee.ActivityId,
        Tasks = employee.Tasks,
        PersonnelNumber = employee.PersonnelNumber,
        EntryDate = employee.EntryDate,
        ExitDate = employee.ExitDate,
        VacationDaysPerYear = employee.VacationDaysPerYear,
        AbsenceDayHours = employee.AbsenceDayHours,
        VacationYears = employee.VacationYears,
        IsEnabled = enabled,
    };
}
