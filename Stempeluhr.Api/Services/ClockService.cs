using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

public sealed class ClockService(
    IRuntimeSettingsStore settingsStore,
    IEmployeeService employees,
    IKimaiClient kimai,
    ITelegramNotifier? notifier = null,
    ILogger<ClockService>? logger = null,
    InterruptedTransitionStore? interruptedTransitions = null) : IClockService
{
    /// <summary>Kiosk event IDs are 32 hex characters; anything longer is not one.</summary>
    private const int MaxEventIdLength = 64;

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
            : (await StartClockAsync(context.Settings, context.Employee, null, cancellationToken)).Status;
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
            return await StartClockAsync(context.Settings, context.Employee, request.TaskId, cancellationToken);
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
                await EndPauseAsync(context.Settings, context.Employee, EventIdOf(request), cancellationToken));
        }

        if (string.Equals(request.Action, "switch", StringComparison.OrdinalIgnoreCase))
        {
            // Unknown or incomplete task: permanent client error, never a
            // silent switch to something else.
            var target = WorkTargetResolver.Resolve(context.Settings, context.Employee, request.TaskId);
            return target is null
                ? new ClockActionResponse(ClockActionResult.BadRequest, null)
                : new ClockActionResponse(
                    ClockActionResult.Success,
                    await SwitchTaskAsync(context.Settings, context.Employee, target, EventIdOf(request), cancellationToken));
        }

        return new ClockActionResponse(ClockActionResult.BadRequest, null);
    }

    /// <summary>
    /// Event ID under which the kiosk queues this action if the request
    /// fails. Older kiosks send none - their half-done transitions stay
    /// rejected on replay, as before.
    /// </summary>
    private static string? EventIdOf(KioskClockRequest request)
    {
        return string.IsNullOrWhiteSpace(request.EventId) || request.EventId.Length > MaxEventIdLength
            ? null
            : request.EventId;
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

    /// <summary>
    /// Einstempeln auf <paramref name="taskId"/> (leer = Haupttätigkeit).
    /// Läuft schon etwas, bleibt es ein No-op - welche Tätigkeit auch genannt
    /// ist: dafür gibt es den Wechsel. Erst danach ist eine unbekannte
    /// Tätigkeit ein Fehler, nie ein stilles Einstempeln auf etwas anderes.
    /// </summary>
    private async Task<ClockActionResponse> StartClockAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        string? taskId,
        CancellationToken cancellationToken)
    {
        var running = await kimai.GetStatusAsync(settings, employee, cancellationToken);
        if (running.IsRunning)
        {
            return new ClockActionResponse(ClockActionResult.Success, running with
            {
                StateText = running.State == "paused" ? "Aktuell in Pause" : "Schon eingestempelt"
            });
        }

        var target = WorkTargetResolver.Resolve(settings, employee, taskId);
        if (target is null)
        {
            return string.IsNullOrWhiteSpace(taskId)
                ? throw new InvalidOperationException("Projekt und Aktivitaet muessen konfiguriert sein.")
                : new ClockActionResponse(ClockActionResult.BadRequest, null);
        }

        await kimai.StartAsync(settings, employee, target, cancellationToken);
        // Nur mit weiteren Tätigkeiten sagt der Name etwas aus - sonst bleibt
        // die Nachricht wie bisher.
        NotifyTransition(
            settings,
            employee,
            "start",
            employee.Tasks is { Length: > 0 } ? target.Label ?? TelegramMessageFactory.DefaultTaskName : null);
        var status = await kimai.GetStatusAsync(settings, employee, cancellationToken);
        return new ClockActionResponse(ClockActionResult.Success, status with { StateText = "Eingestempelt" });
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

        var pause = WorkTargetResolver.ResolvePause(settings, employee);
        if (pause is null)
        {
            return running with { StateText = "Pausen-Aktivitaet fehlt" };
        }

        await kimai.StopAsync(settings, employee, running.ActiveTimesheetId.Value, cancellationToken);
        await kimai.StartAsync(settings, employee, pause, cancellationToken);
        NotifyTransition(settings, employee, "pauseStart");
        var status = await kimai.GetStatusAsync(settings, employee, cancellationToken);
        return status with { StateText = "In Pause" };
    }

    private async Task<ClockStatusDto> EndPauseAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        string? eventId,
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

        if (WorkTargetResolver.ResolveDefault(settings, employee) is null)
        {
            return running with { StateText = "Arbeits-Aktivitaet fehlt" };
        }

        // Resume the task that ran before the pause (read BEFORE stopping the
        // pause: until then the latest stopped timesheet is the one the pause
        // interrupted). Falls back to the default task.
        var resume = WorkTargetResolver.ResolveResume(
            settings, employee, await GetTimesheetBeforePauseAsync(settings, employee, cancellationToken))!;

        await kimai.StopAsync(settings, employee, running.ActiveTimesheetId.Value, cancellationToken);
        await StartAfterStopAsync(settings, employee, resume, eventId, running.ActiveTimesheetId.Value, cancellationToken);
        NotifyTransition(settings, employee, "pauseEnd");
        var status = await kimai.GetStatusAsync(settings, employee, cancellationToken);
        return status with { StateText = "Eingestempelt" };
    }

    /// <summary>
    /// Latest stopped timesheet while a pause runs = the sheet the pause
    /// interrupted. Only used to pick the task to resume, so a failing lookup
    /// (an old Kimai, a timeout) must not block the pause end: default task
    /// then. Without further tasks the answer cannot matter - no request.
    /// </summary>
    private async Task<KimaiRecentTimesheetDto?> GetTimesheetBeforePauseAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        CancellationToken cancellationToken)
    {
        if (employee.Tasks is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            var recent = await kimai.GetRecentStoppedTimesheetsAsync(settings, employee, 1, cancellationToken);
            return recent.FirstOrDefault();
        }
        catch (Exception ex) when (ex is KimaiApiException or HttpRequestException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger?.LogWarning(ex, "Timesheet before the pause could not be read - resuming the default task");
            return null;
        }
    }

    /// <summary>
    /// Switches the running work timesheet to another task without clocking
    /// out: stop, then start on <paramref name="target"/> (same two-step
    /// pattern as the pause). Only from "working" - a pause or a clocked-out
    /// employee has nothing to switch.
    /// </summary>
    private async Task<ClockStatusDto> SwitchTaskAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        KimaiTimesheetTarget target,
        string? eventId,
        CancellationToken cancellationToken)
    {
        var running = await kimai.GetStatusAsync(settings, employee, cancellationToken);
        if (!running.IsRunning || running.ActiveTimesheetId is null)
        {
            return running with { StateText = "Nicht eingestempelt" };
        }

        if (running.State == "paused")
        {
            return running with { StateText = "Aktuell in Pause" };
        }

        if (WorkTargetResolver.IsRunningOn(running, target))
        {
            return running with { StateText = $"{WorkTargetResolver.DisplayName(target)} laeuft bereits" };
        }

        await kimai.StopAsync(settings, employee, running.ActiveTimesheetId.Value, cancellationToken);
        try
        {
            await StartAfterStopAsync(settings, employee, target, eventId, running.ActiveTimesheetId.Value, cancellationToken);
        }
        catch (KimaiApiException ex) when (IsRejectedByKimai(ex))
        {
            if (await ContinuePreviousTaskAsync(settings, employee, running, target, ex, cancellationToken) is { } continued)
            {
                return continued;
            }

            throw;
        }

        NotifyTransition(settings, employee, "switch", target.Label);
        var status = await kimai.GetStatusAsync(settings, employee, cancellationToken);
        return status with { StateText = target.Label is null ? "Zurueck zur Standard-Taetigkeit" : $"Wechsel zu {target.Label}" };
    }

    /// <summary>
    /// Kimai hat den Start auf der Ziel-Tätigkeit dauerhaft abgelehnt (Team-
    /// Zugriff fehlt, Projekt archiviert, ...), nachdem das laufende Blatt
    /// schon gestoppt war (issue #56). Ausgleichsbuchung: weiter auf der
    /// Tätigkeit des gestoppten Blatts, bei einer fremden Buchung auf der
    /// Standard-Tätigkeit - sonst fehlt die Arbeitszeit ab dem Wechsel, bis es
    /// jemand bemerkt. Null, wenn auch das nicht geht: dann bleibt es bei der
    /// Fehlermeldung. Kein Telegram-Hinweis - es wurde nichts gewechselt.
    /// </summary>
    private async Task<ClockStatusDto?> ContinuePreviousTaskAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        ClockStatusDto stopped,
        KimaiTimesheetTarget target,
        KimaiApiException rejection,
        CancellationToken cancellationToken)
    {
        var previous = (stopped.ActiveTaskId is { } taskId ? WorkTargetResolver.ResolveTask(employee, taskId) : null)
            ?? WorkTargetResolver.ResolveDefault(settings, employee);
        if (previous is null || WorkTargetResolver.BooksOn(previous, target.ProjectId, target.ActivityId))
        {
            // Would be rejected just the same.
            logger?.LogWarning(
                rejection,
                "Switch to {Target} rejected by Kimai after the stop - no other task to continue on",
                WorkTargetResolver.DisplayName(target));
            return null;
        }

        try
        {
            await kimai.StartAsync(settings, employee, previous, cancellationToken);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex,
                "Switch to {Target} rejected by Kimai after the stop ({Rejection}) - continuing on {Previous} failed as well",
                WorkTargetResolver.DisplayName(target), rejection.Message, WorkTargetResolver.DisplayName(previous));
            return null;
        }

        logger?.LogWarning(
            rejection,
            "Switch to {Target} rejected by Kimai after the stop - continuing on {Previous}",
            WorkTargetResolver.DisplayName(target), WorkTargetResolver.DisplayName(previous));
        var status = await kimai.GetStatusAsync(settings, employee, cancellationToken);
        return status with
        {
            StateText = "Eingestempelt",
            Warning = $"{WorkTargetResolver.DisplayName(target)} nicht moeglich - weiter auf {WorkTargetResolver.DisplayName(previous)}"
        };
    }

    /// <summary>
    /// Second step of a live transition (pauseEnd, switch) after Kimai
    /// confirmed the stop. When the start fails in a way the kiosk queues
    /// (anything but a final Kimai rejection: 5xx, 408, 429, network, the
    /// kiosk's own timeout aborting the request), the stopped sheet is remembered under the
    /// kiosk's event ID: the replay of exactly that event then resumes
    /// instead of rejecting it like a clock-out elsewhere (issue #67). A
    /// start that went through needs no marker - the replay finds it running.
    /// </summary>
    private async Task StartAfterStopAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        KimaiTimesheetTarget target,
        string? eventId,
        int stoppedTimesheetId,
        CancellationToken cancellationToken)
    {
        try
        {
            await kimai.StartAsync(settings, employee, target, cancellationToken);
        }
        catch (Exception ex) when (eventId is not null && !(ex is KimaiApiException kimaiEx && IsRejectedByKimai(kimaiEx)))
        {
            interruptedTransitions?.Remember(eventId, stoppedTimesheetId);
            logger?.LogWarning(
                ex,
                "Live transition {EventId}: sheet {TimesheetId} stopped, start failed - the queued event may resume it",
                eventId, stoppedTimesheetId);
            throw;
        }
    }

    /// <summary>
    /// Kimai hat die Buchung endgültig abgelehnt (4xx außer 408/429, siehe
    /// <see cref="KimaiApiException.IsTransient"/>). Der Kiosk reiht eine
    /// solche Antwort nicht in die Offline-Queue ein, ein neuer Versuch
    /// bleibt aus. 408/429 dagegen reiht er ein, der Nachtrag entscheidet.
    /// </summary>
    private static bool IsRejectedByKimai(KimaiApiException exception)
    {
        return !exception.IsTransient;
    }

    /// <summary>
    /// Feuert die Telegram-Benachrichtigung für einen ECHTEN Stempel-Übergang.
    /// Wird nur nach erfolgreicher Kimai-Mutation aufgerufen (nie in den
    /// No-Op-Früh-Rückgaben) - Doppel-Taps bleiben stumm.
    /// Fire-and-forget: die Benachrichtigung darf den Stempel weder verzögern
    /// noch scheitern lassen. Bewusst KEIN Request-CancellationToken - der
    /// Request ist nach der Response oft schon beendet.
    /// </summary>
    private void NotifyTransition(RuntimeSettings settings, EmployeeSettings employee, string action, string? taskLabel = null)
    {
        if (notifier is null || !settings.TelegramEnabled)
        {
            return;
        }

        // Synchron direkt am Übergang erfasst (vor jedem await): Das ist die
        // Stempelzeit - nicht erst nach TZ-Lookup/Telegram-POST.
        var stampUtc = DateTimeOffset.UtcNow;
        _ = SendNotificationAsync(settings, employee, action, taskLabel, stampUtc);
    }

    private async Task SendNotificationAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        string action,
        string? taskLabel,
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
            await notifier!.SendStampNotificationAsync(employee.DisplayName, action, stampUtc, timeZone, taskLabel);
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
