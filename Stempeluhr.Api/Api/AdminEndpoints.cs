using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;

namespace Stempeluhr.Api.Api;

public static class AdminEndpoints
{
    private const string AdminDecider = "Admin";

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/rejected-offline-events", (
            HttpRequest request,
            IAdminAuthorizationService authorization,
            RejectedOfflineEventStore rejectedEvents) =>
            authorization.IsAdmin(request) ? Results.Ok(rejectedEvents.List()) : Results.Unauthorized());

        app.MapPut("/api/admin/rejected-offline-events/{eventId}/resolved", (
            string eventId,
            HttpRequest request,
            IAdminAuthorizationService authorization,
            RejectedOfflineEventStore rejectedEvents) =>
        {
            if (!authorization.IsAdmin(request))
            {
                return Results.Unauthorized();
            }
            return rejectedEvents.Resolve(eventId) ? Results.Ok() : Results.NotFound();
        });

        // Korrekturanträge. Entscheidet der Admin, steht als Entscheider "Admin"
        // im Antrag; Kimai-Fehler beim Genehmigen kommen als 200 mit Status Failed.
        app.MapGet("/api/admin/corrections", (
            string? status,
            HttpRequest request,
            IAdminAuthorizationService authorization,
            ITimeCorrectionService corrections) =>
            authorization.IsAdmin(request)
                ? Results.Ok(corrections.List(openOnly: !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase)))
                : Results.Unauthorized());

        app.MapPost("/api/admin/corrections/{id}/approve", async (
            string id,
            HttpRequest request,
            IAdminAuthorizationService authorization,
            ITimeCorrectionService corrections,
            CancellationToken cancellationToken) =>
            authorization.IsAdmin(request)
                ? CorrectionEndpoints.ToResult(await corrections.ApproveAsync(id, AdminDecider, cancellationToken))
                : Results.Unauthorized());

        app.MapPost("/api/admin/corrections/{id}/reject", async (
            string id,
            HttpRequest request,
            RejectCorrectionRequest? body,
            IAdminAuthorizationService authorization,
            ITimeCorrectionService corrections,
            CancellationToken cancellationToken) =>
            authorization.IsAdmin(request)
                ? CorrectionEndpoints.ToResult(await corrections.RejectAsync(id, body?.Note, AdminDecider, cancellationToken))
                : Results.Unauthorized());

        app.MapPost("/api/admin/corrections/{id}/retry", async (
            string id,
            HttpRequest request,
            IAdminAuthorizationService authorization,
            ITimeCorrectionService corrections,
            CancellationToken cancellationToken) =>
            authorization.IsAdmin(request)
                ? CorrectionEndpoints.ToResult(await corrections.RetryAsync(id, AdminDecider, cancellationToken))
                : Results.Unauthorized());

        app.MapPut("/api/admin/corrections/{id}/resolved", async (
            string id,
            HttpRequest request,
            IAdminAuthorizationService authorization,
            ITimeCorrectionService corrections,
            CancellationToken cancellationToken) =>
            authorization.IsAdmin(request)
                ? CorrectionEndpoints.ToResult(await corrections.ResolveManuallyAsync(id, AdminDecider, cancellationToken))
                : Results.Unauthorized());

        app.MapGet("/api/admin/settings", (
            HttpRequest request,
            IRuntimeSettingsStore settingsStore,
            IAdminAuthorizationService authorization) =>
        {
            if (!authorization.IsAdmin(request))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(AdminSettingsDto.FromSettings(settingsStore.Load()));
        });

        app.MapGet("/api/admin/employee-statuses", async (
            HttpRequest request,
            IAdminAuthorizationService authorization,
            IAdminService adminService,
            CancellationToken cancellationToken) =>
        {
            if (!authorization.IsAdmin(request))
            {
                return Results.Unauthorized();
            }

            var statuses = await adminService.GetEmployeeStatusesAsync(cancellationToken);
            return Results.Ok(statuses);
        });

        app.MapGet("/api/admin/terminal-statuses", (
            HttpRequest request,
            IAdminAuthorizationService authorization,
            IRuntimeSettingsStore settingsStore,
            TerminalHealthStore health) =>
        {
            if (!authorization.IsAdmin(request))
            {
                return Results.Unauthorized();
            }

            var now = DateTimeOffset.UtcNow;
            var statuses = settingsStore.Load().TerminalTokens.Keys
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(terminalId => AdminTerminalStatusDto.From(
                    terminalId, health.Snapshot(terminalId, now), now, TimeZoneInfo.Local))
                .ToArray();
            return Results.Ok(statuses);
        });

        app.MapPut("/api/admin/settings", async (
            HttpRequest request,
            AdminSettingsUpdateDto update,
            IRuntimeSettingsStore settingsStore,
            IAdminAuthorizationService authorization,
            IAdminService adminService,
            CancellationToken cancellationToken) =>
        {
            if (!authorization.IsAdmin(request) && !authorization.CanBootstrapFromLocalhost(request))
            {
                return Results.Unauthorized();
            }

            var current = settingsStore.Load();
            var settings = update.ToSettings(current);
            if (adminService.HasDuplicatePins(settings.Employees))
            {
                return Results.Conflict(new { message = "PINs muessen eindeutig sein." });
            }

            if (adminService.HasDuplicateNfcCardIds(settings.Employees))
            {
                return Results.Conflict(new { message = "NFC-Karten muessen eindeutig sein." });
            }

            if (adminService.ValidateTasks(settings) is { } taskError)
            {
                return Results.BadRequest(new { message = taskError });
            }

            await settingsStore.SaveAsync(settings, cancellationToken);

            return Results.Ok(AdminSettingsDto.FromSettings(settings));
        });

        app.MapPost("/api/admin/kimai-users", async (
            HttpRequest request,
            KimaiImportRequest importRequest,
            IKimaiClient kimai,
            IRuntimeSettingsStore settingsStore,
            IAdminAuthorizationService authorization,
            CancellationToken cancellationToken) =>
        {
            if (!authorization.IsAdmin(request))
            {
                return Results.Unauthorized();
            }

            var settings = settingsStore.Load();
            var baseUrl = FirstNonEmpty(importRequest.BaseUrl, settings.BaseUrl);
            var token = FirstNonEmpty(importRequest.AdminApiToken, settings.AdminApiToken);

            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
            {
                return Results.BadRequest(new { message = "Kimai-URL und Admin-API-Token fehlen." });
            }

            var users = await kimai.GetUsersAsync(baseUrl, token, cancellationToken);
            return Results.Ok(users);
        });

        app.MapPost("/api/admin/kimai-activities", async (
            HttpRequest request,
            KimaiImportRequest importRequest,
            IKimaiClient kimai,
            IRuntimeSettingsStore settingsStore,
            IAdminAuthorizationService authorization,
            CancellationToken cancellationToken) =>
        {
            if (!authorization.IsAdmin(request))
            {
                return Results.Unauthorized();
            }

            var settings = settingsStore.Load();
            var baseUrl = FirstNonEmpty(importRequest.BaseUrl, settings.BaseUrl);
            var token = FirstNonEmpty(importRequest.AdminApiToken, settings.AdminApiToken);

            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
            {
                return Results.BadRequest(new { message = "Kimai-URL und Admin-API-Token fehlen." });
            }

            var activities = await kimai.GetActivitiesAsync(baseUrl, token, cancellationToken);
            return Results.Ok(activities);
        });

        app.MapPost("/api/admin/kimai-projects", async (
            HttpRequest request,
            KimaiImportRequest importRequest,
            IKimaiClient kimai,
            IRuntimeSettingsStore settingsStore,
            IAdminAuthorizationService authorization,
            CancellationToken cancellationToken) =>
        {
            if (!authorization.IsAdmin(request))
            {
                return Results.Unauthorized();
            }

            var settings = settingsStore.Load();
            var baseUrl = FirstNonEmpty(importRequest.BaseUrl, settings.BaseUrl);
            var token = FirstNonEmpty(importRequest.AdminApiToken, settings.AdminApiToken);

            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
            {
                return Results.BadRequest(new { message = "Kimai-URL und Admin-API-Token fehlen." });
            }

            var projects = await kimai.GetProjectsAsync(baseUrl, token, cancellationToken);
            return Results.Ok(projects);
        });

        return app;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }
}
