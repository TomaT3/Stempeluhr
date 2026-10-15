using System.Text.Json;
using Stempeluhr.Api.Services;

namespace Stempeluhr.Api.Models;

public sealed record AdminEmployeeStatusDto(
    string EmployeeId,
    string DisplayName,
    bool IsRunning,
    string? StartedAt,
    int DurationSeconds,
    string State,
    string StateText,
    bool IsAvailable,
    string? ActiveTaskLabel = null);

/// <summary>One entry of the admin terminal status page. Never contains tokens.</summary>
public sealed record AdminTerminalStatusDto(
    string TerminalId,
    string State,
    string? LastReportAt,
    IReadOnlyCollection<AdminTerminalProblemDto> Problems,
    TerminalHealthReport? Report,
    IReadOnlyCollection<string> PowerFlags)
{
    public static AdminTerminalStatusDto From(
        string terminalId, TerminalHealthSnapshot snapshot, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        // "never" only until the start grace period ends; then it is an outage.
        var state = snapshot.Alarms.ContainsKey(TerminalCondition.Unreachable) ? "unreachable"
            : snapshot.ReceivedAt is null ? "never"
            : snapshot.Alarms.Count > 0 ? "problem"
            : "online";
        return new AdminTerminalStatusDto(
            terminalId,
            state,
            snapshot.ReceivedAt?.ToString("O"),
            snapshot.Alarms.Select(alarm => new AdminTerminalProblemDto(
                JsonNamingPolicy.CamelCase.ConvertName(alarm.Key.ToString()),
                alarm.Value.ToString("O"),
                TelegramMessageFactory.DescribeTerminalProblem(alarm.Key, alarm.Value, snapshot.Report, now, timeZone)))
                .ToArray(),
            snapshot.Report,
            TerminalHealthEvaluator.DescribeThrottling(snapshot.Report?.ThrottledFlags));
    }
}

public sealed record AdminTerminalProblemDto(string Kind, string Since, string Text);

public sealed record AdminSettingsDto(
    string BaseUrl,
    bool HasAdminPassword,
    bool HasAdminApiToken,
    int? DefaultProjectId,
    int? DefaultActivityId,
    int? PauseActivityId,
    bool HasTelegramBotToken,
    string? TelegramChatId,
    IReadOnlyCollection<AdminEmployeeDto> Employees)
{
    public IReadOnlyCollection<string> TerminalIds { get; init; } = [];
    public string? TelegramAlertChatId { get; init; }
    public string? TelegramCorrectionChatId { get; init; }
    public IReadOnlyCollection<long> TelegramApproverUserIds { get; init; } = [];
    public string? InfluxUrl { get; init; }
    public string? InfluxOrg { get; init; }
    public string? InfluxBucket { get; init; }
    public bool HasInfluxToken { get; init; }
    public int? AbsenceProjectId { get; init; }
    public int? VacationActivityId { get; init; }
    public int? SickActivityId { get; init; }
    public int? FlexDayActivityId { get; init; }
    public string AbsenceDayStart { get; init; } = RuntimeSettings.DefaultAbsenceDayStart;

    public static AdminSettingsDto FromSettings(RuntimeSettings settings)
    {
        return new AdminSettingsDto(
            settings.BaseUrl,
            !string.IsNullOrWhiteSpace(settings.AdminPassword),
            !string.IsNullOrWhiteSpace(settings.AdminApiToken),
            settings.DefaultProjectId,
            settings.DefaultActivityId,
            settings.PauseActivityId,
            !string.IsNullOrWhiteSpace(settings.TelegramBotToken),
            settings.TelegramChatId,
            settings.Employees.Select(AdminEmployeeDto.FromSettings).ToArray())
        {
            TerminalIds = settings.TerminalTokens.Keys.ToArray(),
            TelegramAlertChatId = settings.TelegramAlertChatId,
            TelegramCorrectionChatId = settings.TelegramCorrectionChatId,
            TelegramApproverUserIds = settings.TelegramApproverUserIds.ToArray(),
            InfluxUrl = settings.InfluxUrl,
            InfluxOrg = settings.InfluxOrg,
            InfluxBucket = settings.InfluxBucket,
            HasInfluxToken = !string.IsNullOrWhiteSpace(settings.InfluxToken),
            AbsenceProjectId = settings.AbsenceProjectId,
            VacationActivityId = settings.VacationActivityId,
            SickActivityId = settings.SickActivityId,
            FlexDayActivityId = settings.FlexDayActivityId,
            AbsenceDayStart = settings.AbsenceDayStart,
        };
    }
}

public sealed record AdminEmployeeDto(
    string Id,
    int? KimaiUserId,
    string DisplayName,
    string? Pin,
    string? NfcCardId,
    bool HasApiToken,
    int? ProjectId,
    int? ActivityId,
    string Color,
    string? ImageUrl,
    string? Description,
    string[] Tags,
    bool Billable,
    bool IsEnabled,
    IReadOnlyCollection<AdminEmployeeTaskDto> Tasks,
    string? DefaultTaskLabel)
{
    public string? PersonnelNumber { get; init; }
    public DateOnly? EntryDate { get; init; }
    public DateOnly? ExitDate { get; init; }
    public decimal? VacationDaysPerYear { get; init; }
    public decimal? AbsenceDayHours { get; init; }
    public IReadOnlyCollection<EmployeeVacationYear> VacationYears { get; init; } = [];

    public static AdminEmployeeDto FromSettings(EmployeeSettings employee)
    {
        return new AdminEmployeeDto(
            employee.Id,
            employee.KimaiUserId,
            employee.DisplayName,
            employee.Pin,
            employee.NfcCardId,
            !string.IsNullOrWhiteSpace(employee.ApiToken),
            employee.ProjectId,
            employee.ActivityId,
            employee.Color,
            employee.ImageUrl,
            employee.Description,
            employee.Tags,
            employee.Billable,
            employee.IsEnabled,
            (employee.Tasks ?? []).Select(AdminEmployeeTaskDto.FromSettings).ToArray(),
            employee.DefaultTaskLabel)
        {
            PersonnelNumber = employee.PersonnelNumber,
            EntryDate = employee.EntryDate,
            ExitDate = employee.ExitDate,
            VacationDaysPerYear = employee.VacationDaysPerYear,
            AbsenceDayHours = employee.AbsenceDayHours,
            VacationYears = employee.VacationYears,
        };
    }
}

/// <summary>Weitere Tätigkeit eines Mitarbeiters im Admin-Bereich (lesen und speichern).</summary>
public sealed record AdminEmployeeTaskDto(
    string? Id,
    string? Label,
    int? ProjectId,
    int? ActivityId,
    bool Billable)
{
    public static AdminEmployeeTaskDto FromSettings(EmployeeTaskSettings task)
    {
        return new AdminEmployeeTaskDto(task.Id, task.Label, task.ProjectId, task.ActivityId, task.Billable);
    }

    public EmployeeTaskSettings ToSettings()
    {
        return new EmployeeTaskSettings
        {
            Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id.Trim(),
            Label = Label?.Trim() ?? string.Empty,
            ProjectId = ProjectId,
            ActivityId = ActivityId,
            Billable = Billable
        };
    }
}

public sealed record AdminSettingsUpdateDto(
    string? BaseUrl,
    string? AdminPassword,
    string? AdminApiToken,
    bool KeepAdminApiToken,
    int? DefaultProjectId,
    int? DefaultActivityId,
    int? PauseActivityId,
    string? TelegramBotToken,
    string? TelegramChatId,
    IReadOnlyCollection<AdminEmployeeUpdateDto> Employees,
    string? TelegramAlertChatId = null,
    string? InfluxUrl = null,
    string? InfluxOrg = null,
    string? InfluxBucket = null,
    string? InfluxToken = null,
    string? TelegramCorrectionChatId = null,
    IReadOnlyCollection<long>? TelegramApproverUserIds = null,
    bool IncludesAbsences = false,
    int? AbsenceProjectId = null,
    int? VacationActivityId = null,
    int? SickActivityId = null,
    int? FlexDayActivityId = null,
    string? AbsenceDayStart = null)
{
    public RuntimeSettings ToSettings(RuntimeSettings current)
    {
        var employees = Employees.Select(employee => employee.ToSettings(current)).ToList();
        return new RuntimeSettings
        {
            BaseUrl = BaseUrl?.Trim() ?? string.Empty,
            AdminPassword = string.IsNullOrWhiteSpace(AdminPassword) ? current.AdminPassword : AdminPassword,
            AdminApiToken = KeepAdminApiToken && string.IsNullOrWhiteSpace(AdminApiToken) ? current.AdminApiToken : AdminApiToken,
            DefaultProjectId = DefaultProjectId,
            DefaultActivityId = DefaultActivityId,
            PauseActivityId = PauseActivityId,
            // Keep-current-when-null/whitespace: alte Admin-Clients kennen die
            // Telegram-Felder nicht und senden null - ein Save darf die Config
            // nicht löschen (Löschen geht manuell in settings.json).
            TelegramBotToken = string.IsNullOrWhiteSpace(TelegramBotToken) ? current.TelegramBotToken : TelegramBotToken.Trim(),
            TelegramChatId = string.IsNullOrWhiteSpace(TelegramChatId) ? current.TelegramChatId : TelegramChatId.Trim(),
            TelegramAlertChatId = string.IsNullOrWhiteSpace(TelegramAlertChatId)
                ? current.TelegramAlertChatId
                : TelegramAlertChatId.Trim(),
            // Wie DefaultTaskLabel: null (alter Client) behält den Wert, ein
            // leerer Wert schaltet die Telegram-Freigabe bewusst ab - die
            // Admin-UI hat dafür Felder.
            TelegramCorrectionChatId = TelegramCorrectionChatId is null
                ? current.TelegramCorrectionChatId
                : string.IsNullOrWhiteSpace(TelegramCorrectionChatId) ? null : TelegramCorrectionChatId.Trim(),
            // Ungültige IDs lehnt ValidateTelegram ab. Hier nicht filtern: aus
            // [0] würde sonst eine leere Liste, und leer heißt "alle dürfen".
            TelegramApproverUserIds = TelegramApproverUserIds is null
                ? [.. current.TelegramApproverUserIds]
                : TelegramApproverUserIds.Distinct().ToList(),
            // Same keep-current semantics: the admin UI has no Influx fields.
            InfluxUrl = KeepOrTrim(InfluxUrl, current.InfluxUrl),
            InfluxOrg = KeepOrTrim(InfluxOrg, current.InfluxOrg),
            InfluxBucket = KeepOrTrim(InfluxBucket, current.InfluxBucket),
            InfluxToken = KeepOrTrim(InfluxToken, current.InfluxToken),
            // Ein alter, gecachter Admin-Client kennt die Abwesenheitsfelder
            // nicht (IncludesAbsences fehlt = false): alle behalten. Ein neuer
            // Client sendet sie vollständig, dort löscht null bewusst.
            AbsenceProjectId = IncludesAbsences ? AbsenceProjectId : current.AbsenceProjectId,
            VacationActivityId = IncludesAbsences ? VacationActivityId : current.VacationActivityId,
            SickActivityId = IncludesAbsences ? SickActivityId : current.SickActivityId,
            FlexDayActivityId = IncludesAbsences ? FlexDayActivityId : current.FlexDayActivityId,
            AbsenceDayStart = IncludesAbsences ? AbsenceDayStart ?? RuntimeSettings.DefaultAbsenceDayStart : current.AbsenceDayStart,
            PauseEndRecoveryToleranceSeconds = current.PauseEndRecoveryToleranceSeconds,
            TerminalTokens = current.TerminalTokens,
            Employees = employees
        };
    }

    /// <summary>
    /// Telegram-User-IDs sind positiv. Eine ungültige ID darf die Einschränkung
    /// nie aufheben, deshalb wird der Save abgelehnt statt sie wegzufiltern.
    /// </summary>
    public string? ValidateTelegram() =>
        TelegramApproverUserIds?.Any(id => id <= 0) == true
            ? "Telegram-User-IDs müssen positive Zahlen sein."
            : null;

    private static string? KeepOrTrim(string? value, string? current) =>
        string.IsNullOrWhiteSpace(value) ? current : value.Trim();
}

public sealed record AdminEmployeeUpdateDto(
    string? Id,
    int? KimaiUserId,
    string? DisplayName,
    string? Pin,
    string? NfcCardId,
    string? ApiToken,
    bool KeepApiToken,
    int? ProjectId,
    int? ActivityId,
    string? Color,
    string? ImageUrl,
    string? Description,
    string[]? Tags,
    bool Billable,
    bool IsEnabled,
    IReadOnlyCollection<AdminEmployeeTaskDto>? Tasks = null,
    string? DefaultTaskLabel = null,
    bool IncludesContract = false,
    string? PersonnelNumber = null,
    DateOnly? EntryDate = null,
    DateOnly? ExitDate = null,
    decimal? VacationDaysPerYear = null,
    decimal? AbsenceDayHours = null,
    IReadOnlyCollection<EmployeeVacationYear>? VacationYears = null)
{
    public EmployeeSettings ToSettings(RuntimeSettings current)
    {
        var id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id;
        var existing = current.Employees.FirstOrDefault(employee => string.Equals(employee.Id, id, StringComparison.OrdinalIgnoreCase));

        return new EmployeeSettings
        {
            Id = id,
            KimaiUserId = KimaiUserId,
            DisplayName = DisplayName?.Trim() ?? string.Empty,
            Pin = string.IsNullOrWhiteSpace(Pin) ? null : Pin.Trim(),
            NfcCardId = NfcCardIdNormalizer.Normalize(NfcCardId),
            ApiToken = KeepApiToken && string.IsNullOrWhiteSpace(ApiToken) ? existing?.ApiToken ?? string.Empty : ApiToken ?? string.Empty,
            ProjectId = ProjectId,
            ActivityId = ActivityId,
            Color = string.IsNullOrWhiteSpace(Color) ? "#2563eb" : Color,
            ImageUrl = string.IsNullOrWhiteSpace(ImageUrl) ? null : ImageUrl,
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description,
            Tags = Tags ?? [],
            Billable = Billable,
            IsEnabled = IsEnabled,
            // Keep-current-when-null: ein alter, gecachter Admin-Client kennt
            // die Tätigkeiten nicht und sendet null - ein Save darf sie nicht
            // löschen. Eine leere Liste löscht bewusst.
            Tasks = Tasks is null
                ? existing?.Tasks ?? []
                : Tasks.Select(task => task.ToSettings()).ToArray(),
            // Gleiches Muster: null (alter Client) behält den Wert, ein
            // leerer String löscht ihn bewusst.
            DefaultTaskLabel = DefaultTaskLabel is null
                ? existing?.DefaultTaskLabel
                : string.IsNullOrWhiteSpace(DefaultTaskLabel) ? null : DefaultTaskLabel.Trim(),
            // Vertragsdaten wie die Abwesenheits-Einstellungen: ohne
            // IncludesContract (alter Client) behalten, sonst gilt der
            // gesendete Stand, null löscht.
            PersonnelNumber = IncludesContract
                ? string.IsNullOrWhiteSpace(PersonnelNumber) ? null : PersonnelNumber.Trim()
                : existing?.PersonnelNumber,
            EntryDate = IncludesContract ? EntryDate : existing?.EntryDate,
            ExitDate = IncludesContract ? ExitDate : existing?.ExitDate,
            VacationDaysPerYear = IncludesContract ? VacationDaysPerYear : existing?.VacationDaysPerYear,
            AbsenceDayHours = IncludesContract ? AbsenceDayHours : existing?.AbsenceDayHours,
            // Die Urlaubskonten pflegt die Abwesenheiten-Seite: null behält,
            // eine leere Liste löscht.
            VacationYears = VacationYears is null
                ? existing?.VacationYears ?? []
                : VacationYears
                    .Select(year => year with { Note = string.IsNullOrWhiteSpace(year.Note) ? null : year.Note.Trim() })
                    .OrderBy(year => year.Year)
                    .ToArray()
        };
    }
}
