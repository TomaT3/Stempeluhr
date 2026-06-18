using System.Net;

namespace Stempeluhr.Api.Services;

public sealed class AdminAuthorizationService(
    IRuntimeSettingsStore settingsStore,
    IConfiguration configuration) : IAdminAuthorizationService
{
    public bool IsAdmin(HttpRequest request)
    {
        var expected = FirstNonEmpty(configuration["Admin:Password"], settingsStore.Load().AdminPassword);
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        var provided = request.Headers["X-Admin-Password"].FirstOrDefault();
        return string.Equals(provided, expected, StringComparison.Ordinal);
    }

    public bool CanBootstrapFromLocalhost(HttpRequest request)
    {
        var hasPassword = !string.IsNullOrWhiteSpace(configuration["Admin:Password"]) ||
            !string.IsNullOrWhiteSpace(settingsStore.Load().AdminPassword);

        return !hasPassword && IsLocalhost(request);
    }

    private static bool IsLocalhost(HttpRequest request)
    {
        var remoteIp = request.HttpContext.Connection.RemoteIpAddress;
        return remoteIp is null || IPAddress.IsLoopback(remoteIp);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }
}
