using System.Text.Json.Serialization;

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
    /// Optionaler eigener Chat für technische Warnungen (Terminal-Überwachung).
    /// Leer: die Warnungen gehen in <see cref="TelegramChatId"/>.
    /// </summary>
    public string? TelegramAlertChatId { get; init; }

    // Derived: must not end up in settings.json as if it were configured.
    [JsonIgnore]
    public string? TelegramAlertChat =>
        string.IsNullOrWhiteSpace(TelegramAlertChatId) ? TelegramChatId : TelegramAlertChatId;

    [JsonIgnore]
    public bool TelegramAlertsEnabled =>
        !string.IsNullOrWhiteSpace(TelegramBotToken) && !string.IsNullOrWhiteSpace(TelegramAlertChat);

    /// <summary>
    /// Eigener Chat für Korrekturanträge mit den Knöpfen Genehmigen/Ablehnen.
    /// Leer: keine Telegram-Freigabe (und kein Poller), es gilt nur die Admin-Seite.
    /// </summary>
    public string? TelegramCorrectionChatId { get; init; }

    /// <summary>
    /// Telegram-User-IDs, die Korrekturanträge entscheiden dürfen. Leer: jedes
    /// Mitglied von <see cref="TelegramCorrectionChatId"/>.
    /// </summary>
    private readonly List<long> telegramApproverUserIds = [];
    public List<long> TelegramApproverUserIds
    {
        get => telegramApproverUserIds;
        init => telegramApproverUserIds = value ?? [];
    }

    [JsonIgnore]
    public bool TelegramCorrectionsEnabled =>
        !string.IsNullOrWhiteSpace(TelegramBotToken) && !string.IsNullOrWhiteSpace(TelegramCorrectionChatId);

    /// <summary>Optional InfluxDB for terminal metrics over time, e.g. <c>http://192.168.1.10:8086</c>.</summary>
    public string? InfluxUrl { get; init; }

    /// <summary>InfluxDB 2.x organization; may stay empty for InfluxDB 3.</summary>
    public string? InfluxOrg { get; init; }

    /// <summary>InfluxDB bucket (InfluxDB 3: database).</summary>
    public string? InfluxBucket { get; init; }

    /// <summary>InfluxDB token with write access (secret, never returned to clients).</summary>
    public string? InfluxToken { get; init; }

    [JsonIgnore]
    public bool InfluxEnabled =>
        Uri.TryCreate(InfluxUrl, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
        && !string.IsNullOrWhiteSpace(InfluxBucket) && !string.IsNullOrWhiteSpace(InfluxToken);

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
