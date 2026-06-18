namespace Stempeluhr.Api.Services;

public interface IAdminAuthorizationService
{
    bool IsAdmin(HttpRequest request);

    bool CanBootstrapFromLocalhost(HttpRequest request);
}
