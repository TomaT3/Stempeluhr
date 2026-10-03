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
            employee.DefaultTaskLabel);
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
    string? TelegramAlertChatId = null)
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
            TerminalTokens = current.TerminalTokens,
            Employees = employees
        };
    }
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
    string? DefaultTaskLabel = null)
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
                : string.IsNullOrWhiteSpace(DefaultTaskLabel) ? null : DefaultTaskLabel.Trim()
        };
    }
}
