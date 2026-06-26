using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

public sealed class ClockService(
    IRuntimeSettingsStore settingsStore,
    IEmployeeService employees,
    IKimaiClient kimai) : IClockService
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
        var context = FindEmployee(new ClockRequest(request.EmployeeId, request.Pin));
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

    public async Task<NfcClockEventDto> ClockWithNfcCardAsync(
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

        var status = await ClockByNfcActionAsync(settings, employee, request.Action, cancellationToken);
        if (status is null)
        {
            return CreateNfcEvent(
                terminalId,
                normalizedCardId,
                employees.ToEmployeeDto(employee),
                null,
                "Unbekannte NFC-Stempelaktion.",
                false);
        }

        return CreateNfcEvent(
            terminalId,
            normalizedCardId,
            employees.ToEmployeeDto(employee),
            status,
            status.StateText,
            true);
    }

    private EmployeeContext? FindEmployee(ClockRequest request)
    {
        var settings = settingsStore.Load();
        var employee = employees.FindEmployee(settings, request);
        return employee is null ? null : new EmployeeContext(settings, employee);
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
        var status = await kimai.GetStatusAsync(settings, employee, cancellationToken);
        return status with { StateText = "Eingestempelt" };
    }

    private async Task<ClockStatusDto?> ClockByNfcActionAsync(
        RuntimeSettings settings,
        EmployeeSettings employee,
        string? action,
        CancellationToken cancellationToken)
    {
        var normalizedAction = string.IsNullOrWhiteSpace(action) ? "toggle" : action.Trim();

        if (string.Equals(normalizedAction, "toggle", StringComparison.OrdinalIgnoreCase))
        {
            var current = await kimai.GetStatusAsync(settings, employee, cancellationToken);
            return current.IsRunning
                ? await StopClockAsync(settings, employee, cancellationToken)
                : await StartClockAsync(settings, employee, cancellationToken);
        }

        if (string.Equals(normalizedAction, "start", StringComparison.OrdinalIgnoreCase))
        {
            return await StartClockAsync(settings, employee, cancellationToken);
        }

        if (string.Equals(normalizedAction, "stop", StringComparison.OrdinalIgnoreCase))
        {
            return await StopClockAsync(settings, employee, cancellationToken);
        }

        if (string.Equals(normalizedAction, "pauseStart", StringComparison.OrdinalIgnoreCase))
        {
            return await StartPauseAsync(settings, employee, cancellationToken);
        }

        if (string.Equals(normalizedAction, "pauseEnd", StringComparison.OrdinalIgnoreCase))
        {
            return await EndPauseAsync(settings, employee, cancellationToken);
        }

        return null;
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
        var status = await kimai.GetStatusAsync(settings, employee, cancellationToken);
        return status with { StateText = "Eingestempelt" };
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
