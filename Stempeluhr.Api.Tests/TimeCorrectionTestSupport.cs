using System.Net;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;

namespace Stempeluhr.Api.Tests;

/// <summary>Shared fixtures for the correction request tests: fixed clock, settings, in-memory Kimai.</summary>
internal static class TimeCorrection
{
    /// <summary>Monday 2026-10-05 12:00 in Berlin (CEST, +02:00).</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);

    public static readonly TimeSpan Cest = TimeSpan.FromHours(2);

    public const int PauseActivity = 2;
    public const int WorkProject = 1;
    public const int WorkActivity = 1;
    public const int OwnUserId = 7;

    /// <summary>Local Berlin time on the day of <see cref="Now"/>, e.g. <c>At(8, 30)</c> = 08:30.</summary>
    public static DateTimeOffset At(int hour, int minute = 0)
        => new(2026, 10, 5, hour, minute, 0, Cest);

    public static DateTimeOffset Day(int dayOfOctober, int hour, int minute = 0)
        => new(2026, 10, dayOfOctober, hour, minute, 0, Cest);

    /// <summary>Local text as the kiosk sends it.</summary>
    public static string Text(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm");

    public static RuntimeSettings Settings(params EmployeeSettings[] employees) => new()
    {
        BaseUrl = "http://kimai.test",
        DefaultProjectId = WorkProject,
        DefaultActivityId = WorkActivity,
        PauseActivityId = PauseActivity,
        Employees = employees.Length == 0 ? [Max()] : [.. employees],
    };

    public static EmployeeSettings Max() => new()
    {
        Id = "max",
        DisplayName = "Max Mustermann",
        Pin = "1234",
        NfcCardId = "04A2B3C4",
        ApiToken = "max-token",
        ProjectId = WorkProject,
        ActivityId = WorkActivity,
        Tasks = [new EmployeeTaskSettings { Id = "kx", Label = "Kunde X", ProjectId = 5, ActivityId = 6 }],
    };

    public static EmployeeSettings Anna() => new()
    {
        Id = "anna",
        DisplayName = "Anna Beispiel",
        Pin = "5678",
        NfcCardId = "04D5E6F7",
        ApiToken = "anna-token",
        ProjectId = WorkProject,
        ActivityId = WorkActivity,
    };

    public static TimeCorrectionRequest Request(
        TimeCorrectionKind kind,
        FakeKimai.Sheet? original = null,
        Func<TimeCorrectionRequest, TimeCorrectionRequest>? configure = null)
    {
        var request = new TimeCorrectionRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            EmployeeId = "max",
            EmployeeName = "Max Mustermann",
            Kind = kind,
            Source = "clock",
            CreatedAt = Now,
            TimesheetId = original?.Id,
            Original = original is null
                ? null
                : new TimeCorrectionOriginal(original.Begin, original.End, original.Activity, original.Project, original.Description, original.Billable),
        };
        return configure is null ? request : configure(request);
    }
}

internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan duration) => now += duration;
}

internal sealed class StubSettingsStore(RuntimeSettings settings) : IRuntimeSettingsStore
{
    public RuntimeSettings Load() => settings;

    public Task SaveAsync(RuntimeSettings settings, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

internal sealed class RecordingNotifier : ITimeCorrectionNotifier
{
    public List<string> Events { get; } = [];
    public bool Throws { get; set; }

    public Task OnSubmitted(TimeCorrectionRequest request, CancellationToken cancellationToken = default)
    {
        Events.Add($"submitted:{request.Status}");
        return Throws ? throw new InvalidOperationException("notifier down") : Task.CompletedTask;
    }

    public Task OnDecided(TimeCorrectionRequest request, CancellationToken cancellationToken = default)
    {
        Events.Add($"decided:{request.Status}");
        return Throws ? throw new InvalidOperationException("notifier down") : Task.CompletedTask;
    }
}

/// <summary>
/// In-memory Kimai for the correction tests. It keeps real timesheets, so
/// "no duplicate booking" is checked on the resulting state, and it records
/// every write in <see cref="Writes"/> ("what Kimai was asked to do").
/// </summary>
internal sealed class FakeKimai : IKimaiClient
{
    public sealed class Sheet
    {
        public int Id { get; set; }
        public int? User { get; set; } = TimeCorrection.OwnUserId;
        public DateTimeOffset Begin { get; set; }
        public DateTimeOffset? End { get; set; }
        public int Activity { get; set; } = TimeCorrection.WorkActivity;
        public int Project { get; set; } = TimeCorrection.WorkProject;
        public string? Description { get; set; } = "Stempeluhr";
        public bool Billable { get; set; } = true;
    }

    private int _nextId = 100;

    public List<Sheet> Sheets { get; } = [];

    /// <summary>Every create/patch Kimai received, e.g. <c>patch 1 end=11:00</c>, <c>create act=2 11:00-11:30</c>.</summary>
    public List<string> Writes { get; } = [];

    public int? UserId { get; set; } = TimeCorrection.OwnUserId;
    public bool Unreachable { get; set; }

    /// <summary>Throws before the n-th create (1-based) happens; once.</summary>
    public int? FailBeforeCreate { get; set; }

    /// <summary>Creates the n-th timesheet in Kimai, then throws (answer lost) - once.</summary>
    public int? FailAfterCreate { get; set; }

    public HttpStatusCode FailStatus { get; set; } = HttpStatusCode.InternalServerError;
    private int _creates;

    public Sheet Add(DateTimeOffset begin, DateTimeOffset? end, int activity = TimeCorrection.WorkActivity, int project = TimeCorrection.WorkProject, int? user = TimeCorrection.OwnUserId)
    {
        var sheet = new Sheet { Id = _nextId++, Begin = begin, End = end, Activity = activity, Project = project, User = user };
        Sheets.Add(sheet);
        return sheet;
    }

    private void ThrowIfUnreachable()
    {
        if (Unreachable)
        {
            throw new HttpRequestException("kimai down");
        }
    }

    public Task<int?> GetCurrentUserIdAsync(RuntimeSettings s, EmployeeSettings e, CancellationToken ct = default)
    {
        ThrowIfUnreachable();
        return Task.FromResult(UserId);
    }

    public Task<string?> GetCurrentUserTimezoneAsync(RuntimeSettings s, EmployeeSettings e, CancellationToken ct = default)
    {
        ThrowIfUnreachable();
        return Task.FromResult<string?>("Europe/Berlin");
    }

    public Task<KimaiTimesheetDetailDto?> GetTimesheetAsync(RuntimeSettings s, EmployeeSettings e, int id, CancellationToken ct = default)
    {
        ThrowIfUnreachable();
        // Like Kimai for a token with view_other_timesheet: foreign sheets are returned too.
        var sheet = Sheets.FirstOrDefault(candidate => candidate.Id == id);
        return Task.FromResult(sheet is null
            ? null
            : new KimaiTimesheetDetailDto(sheet.Id, sheet.Begin, sheet.End, sheet.Activity, sheet.Project, sheet.Description, sheet.Billable, sheet.User));
    }

    public Task<IReadOnlyCollection<KimaiTimesheetEntryDto>> GetTimesheetsAsync(
        RuntimeSettings s, EmployeeSettings e, DateTime begin, DateTime end, CancellationToken ct = default)
    {
        ThrowIfUnreachable();
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        // Only the token owner's sheets, filtered on begin like Kimai does.
        IReadOnlyCollection<KimaiTimesheetEntryDto> result = Sheets
            .Where(sheet => sheet.User == TimeCorrection.OwnUserId)
            .Where(sheet =>
            {
                var local = TimeZoneInfo.ConvertTime(sheet.Begin, berlin).DateTime;
                return local >= begin && local <= end;
            })
            .OrderBy(sheet => sheet.Begin)
            .Select(sheet => new KimaiTimesheetEntryDto(
                sheet.Id, sheet.Begin, sheet.End, sheet.End is null ? 0 : (int)(sheet.End.Value - sheet.Begin).TotalSeconds,
                sheet.Activity, sheet.Project, sheet.Description))
            .ToArray();
        return Task.FromResult(result);
    }

    public Task<int> CreateTimesheetAsync(
        RuntimeSettings s, EmployeeSettings e, KimaiTimesheetTarget target, DateTimeOffset begin, DateTimeOffset end, string? description, CancellationToken ct = default)
    {
        ThrowIfUnreachable();
        _creates++;
        if (FailBeforeCreate == _creates)
        {
            FailBeforeCreate = null;
            throw new KimaiApiException(FailStatus, "{}", "POST /api/timesheets");
        }

        Writes.Add($"create act={target.ActivityId} proj={target.ProjectId} {Hm(begin)}-{Hm(end)} desc={description} billable={target.Billable}");
        var sheet = Add(begin, end, target.ActivityId, target.ProjectId);
        sheet.Description = description;
        sheet.Billable = target.Billable;
        if (FailAfterCreate == _creates)
        {
            FailAfterCreate = null;
            throw new HttpRequestException("answer lost");
        }

        return Task.FromResult(sheet.Id);
    }

    public Task UpdateTimesheetTimesAsync(
        RuntimeSettings s, EmployeeSettings e, int id, DateTimeOffset? begin, DateTimeOffset? end, CancellationToken ct = default)
    {
        ThrowIfUnreachable();
        var sheet = Sheets.First(candidate => candidate.Id == id);
        Writes.Add($"patch {id}{(begin is { } b ? $" begin={Hm(b)}" : "")}{(end is { } n ? $" end={Hm(n)}" : "")}");
        if (begin is { } newBegin) sheet.Begin = newBegin;
        if (end is { } newEnd) sheet.End = newEnd;
        return Task.CompletedTask;
    }

    private static string Hm(DateTimeOffset value) => value.ToOffset(TimeCorrection.Cest).ToString("HH:mm");

    /// <summary>Only whether something runs counts (like <c>/api/timesheets/active</c> of the token owner).</summary>
    public Task<ClockStatusDto> GetStatusAsync(RuntimeSettings s, EmployeeSettings e, CancellationToken ct = default)
    {
        ThrowIfUnreachable();
        var running = Sheets.FirstOrDefault(sheet => sheet.User == TimeCorrection.OwnUserId && sheet.End is null);
        return Task.FromResult(running is null
            ? new ClockStatusDto(false, null, null, 0, "clockedOut", "Nicht eingestempelt")
            : new ClockStatusDto(true, running.Id, running.Begin.ToString("o"), 0, "working", "Eingestempelt"));
    }

    public Task StartAsync(RuntimeSettings s, EmployeeSettings e, KimaiTimesheetTarget t, CancellationToken ct = default) => throw new NotSupportedException();

    /// <summary>Starts a running timesheet that begins in the past; shares the failure switches of <see cref="CreateTimesheetAsync"/>.</summary>
    public Task StartAtAsync(RuntimeSettings s, EmployeeSettings e, KimaiTimesheetTarget target, DateTimeOffset begin, CancellationToken ct = default)
    {
        ThrowIfUnreachable();
        _creates++;
        if (FailBeforeCreate == _creates)
        {
            FailBeforeCreate = null;
            throw new KimaiApiException(FailStatus, "{}", "POST /api/timesheets");
        }

        Writes.Add($"start act={target.ActivityId} proj={target.ProjectId} {Hm(begin)}- desc={target.Description} billable={target.Billable}");
        var sheet = Add(begin, null, target.ActivityId, target.ProjectId);
        sheet.Description = target.Description;
        sheet.Billable = target.Billable;
        if (FailAfterCreate == _creates)
        {
            FailAfterCreate = null;
            throw new HttpRequestException("answer lost");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(RuntimeSettings s, EmployeeSettings e, int id, CancellationToken ct = default) => throw new NotSupportedException();
    public Task StopAtAsync(RuntimeSettings s, EmployeeSettings e, int id, DateTimeOffset d, CancellationToken ct = default) => throw new NotSupportedException();
    public Task BackdateEndAsync(RuntimeSettings s, EmployeeSettings e, int id, DateTimeOffset d, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<KimaiRecentTimesheetDto>> GetRecentStoppedTimesheetsAsync(RuntimeSettings s, EmployeeSettings e, int count, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyCollection<KimaiUserDto>> GetUsersAsync(string b, string t, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyCollection<KimaiActivityDto>> GetActivitiesAsync(string b, string t, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyCollection<KimaiProjectDto>> GetProjectsAsync(string b, string t, CancellationToken ct = default) => throw new NotSupportedException();
}
