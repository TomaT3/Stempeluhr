using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;

namespace Stempeluhr.Api.Api;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
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
