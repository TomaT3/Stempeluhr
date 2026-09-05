using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

public sealed class ClockService(
    IRuntimeSettingsStore settingsStore,
    IEmployeeService employees,
    IKimaiClient kimai,
    ITelegramNotifier? notifier = null,
    ILogger<ClockService>? logger = null) : IClockService
{
    public async Task<KioskEmployeeSessionDto?> LoginWithPinAsync(string? pin, CancellationToken cancellationToken = default)
    {
        var settings = settingsStore.Load();
        var employee = employees.FindEmployeeByPin(settings, pin);
        if (employee is null)
        {
            return null;
        }

        var status = await kimai.GetStatusAsync(settings, employee, cancellationToken);
        return new KioskEmployeeSessionDto(employees.ToEmployeeDto(employee), status);
    }

    public async Task<HoursOverviewDto?> GetHoursOverviewAsync(string? pin, CancellationToken cancellationToken = default)
    {
        var settings = settingsStore.Load();
        var employee = employees.FindEmployeeByPin(settings, pin);
        if (employee is null)
        {
            return null;
        }

        var now = DateTimeOffset.Now;
        // Kimai interpretiert naive HTML5-Datetimes in der Zeitzone des
        // Token-Inhabers - nicht in der des Containers (der laeuft UTC).
        // Sonst verschiebt sich das Abfragefenster und die aktuelle Schicht
        // fehlt (nachts ganztags). Fallback bei Fehler: Server-Zeitzone.
        var timeZone = ResolveTimezone(await kimai.GetCurrentUserTimezoneAsync(settings, employee, cancellationToken));
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone).DateTime;
        var unionStart = HoursOverviewCalculator.GetUnionStart(localNow);

        var entries = await kimai.GetTimesheetsAsync(
            settings, employee, unionStart, localNow, cancellationToken);

        return HoursOverviewCalculator.Calculate(entries, settings.PauseActivityId, now, timeZone);
    }

    private static TimeZoneInfo ResolveTimezone(string? kimaiTimezone)
    {
        if (!string.IsNullOrWhiteSpace(kimaiTimezone))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(kimaiTimezone);
            }
            catch (TimeZoneNotFoundException)
            {
                // Unknown IANA id - fall through to the server timezone.
            }
        }

        return TimeZoneInfo.Local;
    }

    public async Task<ClockStatusDto?> GetStatusAsync(ClockRequest request, CancellationToken cancellationToken = default)
    {
        var context = FindEmployee(request);
        return context is null
            ? null
            : await kimai.GetStatusAsync(context.Settings, context.Employee, cancellationToken);
    }

    public async Task<ClockStatusDto?> StartAsync(ClockRequest request, CancellationToken cancellationToken = default)
    {
        var context = FindEmployee(request);
        return context is null
            ? null
            : await StartClockAsync(context.Settings, context.Employee, cancellationToken);
    }

    public async Task<ClockStatusDto?> StopAsync(ClockRequest request, CancellationToken cancellationToken = default)
    {
        var context = FindEmployee(request);
        return context is null
            ? null
            : await StopClockAsync(context.Settings, context.Employee, cancellationToken);
    }

    public async Task<ClockActionResponse> ClockAsync(KioskClockRequest request, CancellationToken cancellationToken = default)
    {
        var context = FindEmployeeForClockAction(request);
        if (context is null)
        {
            return new ClockActionResponse(ClockActionResult.Unauthorized, null);
        }

        if (string.Equals(request.Action, "start", StringComparison.OrdinalIgnoreCase))
        {
            return new ClockActionResponse(
                ClockActionResult.Success,
                await StartClockAsync(context.Settings, context.Employee, cancellationToken));
        }

        if (string.Equals(request.Action, "stop", StringComparison.OrdinalIgnoreCase))
        {
            return new ClockActionResponse(
                ClockActionResult.Success,
                await StopClockAsync(context.Settings, context.Employee, cancellationToken));
        }

        if (string.Equals(request.Action, "pauseStart", StringComparison.OrdinalIgnoreCase))
        {
            return new ClockActionResponse(
                ClockActionResult.Success,
                await StartPauseAsync(context.Settings, context.Employee, cancellationToken));
        }

        if (string.Equals(request.Action, "pauseEnd", StringComparison.OrdinalIgnoreCase))
        {
            return new ClockActionResponse(
                ClockActionResult.Success,
                await EndPauseAsync(context.Settings, context.Employee, cancellationToken));
        }

        return new ClockActionResponse(ClockActionResult.BadRequest, null);
    }

    public async Task<NfcClockEventDto> IdentifyWithNfcCardAsync(
        NfcClockRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalizedCardId = NfcCardIdNormalizer.Normalize(request.CardId);
        var terminalId = NormalizeTerminalId(request.TerminalId);
        if (normalizedCardId is null)
        {
            return CreateNfcEvent(terminalId, null, null, null, "NFC-Karte konnte nicht gelesen werden.", false);
        }

        var settings = settingsStore.Load();
        var employee = employees.FindEmployeeByNfcCardId(settings, normalizedCardId);
        if (employee is null)
        {
            return CreateNfcEvent(terminalId, normalizedCardId, null, null, "NFC-Karte ist keinem Mitarbeiter zugeordnet.", false);
        }

        var status = await kimai.GetStatusAsync(settings, employee, cancellationToken);

        return CreateNfcEvent(
            terminalId,
            normalizedCardId,
            employees.ToEmployeeDto(employee),
            status,
            "NFC-Karte erkannt.",
            true);
    }

    private EmployeeContext? FindEmployee(ClockRequest request)
    {
        var settings = settingsStore.Load();
        var employee = employees.FindEmployee(settings, request);
        return employee is null ? null : new EmployeeContext(settings, employee);
    }

    private EmployeeContext? FindEmployeeForClockAction(KioskClockRequest request)
    {
        var settings = settingsStore.Load();
        var pinEmployee = employees.FindEmployee(settings, new ClockRequest(request.EmployeeId, request.Pin));
        if (pinEmployee is not null)
        {
            return new EmployeeContext(settings, pinEmployee);
        }

        var nfcEmployee = employees.FindEmployeeByNfcCardId(settings, request.NfcCardId);
        if (nfcEmployee is null)
        {
            return null;
        }

        return string.Equals(nfcEmployee.Id, request.EmployeeId, StringComparison.OrdinalIgnoreCase)
            ? new EmployeeContext(settings, nfcEmployee)
            : null;
    }

    private async Task<ClockStatusDto> StartClockAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        CancellationToken cancellationToken)
    {
        var running = await kimai.GetStatusAsync(settings, employee, cancellationToken);
        if (running.IsRunning)
        {
            return running with
            {
                StateText = running.State == "paused" ? "Aktuell in Pause" : "Schon eingestempelt"
            };
        }

        await kimai.StartAsync(settings, employee, cancellationToken);
        NotifyTransition(settings, employee, "start");
        var status = await kimai.GetStatusAsync(settings, employee, cancellationToken);
        return status with { StateText = "Eingestempelt" };
    }

    private async Task<ClockStatusDto> StopClockAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        CancellationToken cancellationToken)
    {
        var running = await kimai.GetStatusAsync(settings, employee, cancellationToken);
        if (!running.IsRunning || running.ActiveTimesheetId is null)
        {
            return running with { StateText = "Nicht eingestempelt" };
        }

        await kimai.StopAsync(settings, employee, running.ActiveTimesheetId.Value, cancellationToken);
        NotifyTransition(settings, employee, "stop");
        var status = await kimai.GetStatusAsync(settings, employee, cancellationToken);
        return status with { StateText = "Ausgestempelt" };
    }

    private async Task<ClockStatusDto> StartPauseAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        CancellationToken cancellationToken)
    {
        var running = await kimai.GetStatusAsync(settings, employee, cancellationToken);
        if (!running.IsRunning || running.ActiveTimesheetId is null)
        {
            return running with { StateText = "Nicht eingestempelt" };
        }

        if (running.State == "paused")
        {
            return running with { StateText = "Schon in Pause" };
        }

        if ((employee.ProjectId ?? settings.DefaultProjectId) is null || settings.PauseActivityId is null)
        {
            return running with { StateText = "Pausen-Aktivitaet fehlt" };
        }

        await kimai.StopAsync(settings, employee, running.ActiveTimesheetId.Value, cancellationToken);
        await kimai.StartPauseAsync(settings, employee, cancellationToken);
        NotifyTransition(settings, employee, "pauseStart");
        var status = await kimai.GetStatusAsync(settings, employee, cancellationToken);
        return status with { StateText = "In Pause" };
    }

    private async Task<ClockStatusDto> EndPauseAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        CancellationToken cancellationToken)
    {
        var running = await kimai.GetStatusAsync(settings, employee, cancellationToken);
        if (!running.IsRunning || running.ActiveTimesheetId is null)
        {
            return running with { StateText = "Nicht in Pause" };
        }

        if (running.State != "paused")
        {
            return running with { StateText = "Nicht in Pause" };
        }

        if ((employee.ProjectId ?? settings.DefaultProjectId) is null
            || (employee.ActivityId ?? settings.DefaultActivityId) is null)
        {
            return running with { StateText = "Arbeits-Aktivitaet fehlt" };
        }

        await kimai.StopAsync(settings, employee, running.ActiveTimesheetId.Value, cancellationToken);
        await kimai.StartAsync(settings, employee, cancellationToken);
        NotifyTransition(settings, employee, "pauseEnd");
        var status = await kimai.GetStatusAsync(settings, employee, cancellationToken);
        return status with { StateText = "Eingestempelt" };
    }

    /// <summary>
    /// Feuert die Telegram-Benachrichtigung für einen ECHTEN Stempel-Übergang.
    /// Wird nur nach erfolgreicher Kimai-Mutation aufgerufen (nie in den
    /// No-Op-Früh-Rückgaben) - Doppel-Taps bleiben stumm.
    /// Fire-and-forget: die Benachrichtigung darf den Stempel weder verzögern
    /// noch scheitern lassen. Bewusst KEIN Request-CancellationToken - der
    /// Request ist nach der Response oft schon beendet.
    /// </summary>
    private void NotifyTransition(RuntimeSettings settings, EmployeeSettings employee, string action)
    {
        if (notifier is null || !settings.TelegramEnabled)
        {
            return;
        }

        // Synchron direkt am Übergang erfasst (vor jedem await): Das ist die
        // Stempelzeit - nicht erst nach TZ-Lookup/Telegram-POST.
        var stampUtc = DateTimeOffset.UtcNow;
        _ = SendNotificationAsync(settings, employee, action, stampUtc);
    }

    private async Task SendNotificationAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        string action,
        DateTimeOffset stampUtc)
    {
        try
        {
            // Stempelzeit in der Kimai-User-TZ des Mitarbeiters formatieren
            // (Container läuft UTC - ohne Konvertierung 2h daneben).
            // Kontrakt: Kimai-API-Fehler liefert GetCurrentUserTimezoneAsync
            // laut IKimaiClient als null -> ResolveTimezone fällt auf die
            // Server-TZ zurück. Transportfehler (Timeout/Netzwerk) werfen
            // stattdessen und landen im catch darunter.
            var timeZone = ResolveTimezone(
                await kimai.GetCurrentUserTimezoneAsync(settings, employee, CancellationToken.None));
            await notifier!.SendStampNotificationAsync(employee.DisplayName, action, stampUtc, timeZone);
        }
        catch (Exception ex)
        {
            // Best effort: nie aus einem fire-and-forget Task werfen
            // (unobserved task exception). Der Notifier schluckt und loggt
            // seine eigenen Fehler bereits selbst; hier explizit loggen,
            // weil der TZ-Lookup die letzte Stelle ist, an der eine
            // Nachricht sonst spurlos verloren ginge (Netzwerkfehler werden
            // von KimaiClient nicht in null übersetzt).
            logger?.LogWarning(ex, "Telegram notification could not be prepared (timezone lookup)");
        }
    }

    private static NfcClockEventDto CreateNfcEvent(
        string terminalId,
        string? cardId,
        EmployeeDto? employee,
        ClockStatusDto? status,
        string message,
        bool success)
    {
        return new NfcClockEventDto(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            terminalId,
            cardId,
            employee,
            status,
            message,
            success);
    }

    private static string NormalizeTerminalId(string? terminalId)
    {
        return string.IsNullOrWhiteSpace(terminalId) ? "default" : terminalId.Trim();
    }

    private sealed record EmployeeContext(RuntimeSettings Settings, EmployeeSettings Employee);
}
