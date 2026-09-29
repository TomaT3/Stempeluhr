namespace Stempeluhr.Api.Models;

public sealed class RuntimeSettings
{
    /// <summary>Terminal ID to secret token. Managed in settings.json; never returned to clients.</summary>
    private readonly Dictionary<string, string> terminalTokens = [];
    public Dictionary<string, string> TerminalTokens
    {
        get => terminalTokens;
        init => terminalTokens = value ?? [];
    }

    public string BaseUrl { get; init; } = string.Empty;
    public string? AdminPassword { get; init; }
    public string? AdminApiToken { get; init; }
    public int? DefaultProjectId { get; init; }
    public int? DefaultActivityId { get; init; }
    public int? PauseActivityId { get; init; }

    /// <summary>Telegram-Bot-Token (Secret, nie im Client/Admin-DTO ausliefern).</summary>
    public string? TelegramBotToken { get; init; }

    /// <summary>Telegram-Chat-ID (Gruppe) für Stempel-Benachrichtigungen.</summary>
    public string? TelegramChatId { get; init; }

    public bool TelegramEnabled =>
        !string.IsNullOrWhiteSpace(TelegramBotToken) && !string.IsNullOrWhiteSpace(TelegramChatId);

    /// <summary>
    /// Maximaler Abstand zwischen dem Ende des letzten gestoppten
    /// Timesheets und dem Zeitstempel eines replayten pauseEnd- bzw.
    /// Wechsel-Events, ab dem der Zustand wie eine unterbrochene Transaktion
    /// aussieht. Fortgesetzt wird nie aufgrund dieses Abstands, sondern nur,
    /// wenn dieser Server das Blatt für genau dieses Event gestoppt hat
    /// (Merker mit Timesheet-ID, Issues #55 und #10). Ohne Merker entscheidet
    /// der Abstand nur: innerhalb der Toleranz ablehnen (der Kiosk meldet den
    /// Stempel zur Prüfung), sonst ein stiller No-op.
    ///
    /// Trade-off: Der Wert sollte größer sein als die zu erwartende
    /// Uhrenabweichung zwischen Client (Pi) und Server (Kimai). Raspberry Pi
    /// OS (Bookworm) synchronisiert die Uhr standardmäßig via systemd-timesyncd
    /// (NTP über DHCP) - dann reichen 30 s locker. Ist er zu klein, endet ein
    /// Fall ohne Merker (Neustart dazwischen) als stiller No-op statt als
    /// gemeldete Ablehnung.
    /// </summary>
    public int PauseEndRecoveryToleranceSeconds { get; init; } = 30;
    public List<EmployeeSettings> Employees { get; init; } = [];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && Employees.Any(employee => employee.IsEnabled);
}
