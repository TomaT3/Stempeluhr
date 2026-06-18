namespace Stempeluhr.Api.Api;

public static class EndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", () => Results.Redirect("/api/health"));

        app.MapHealthEndpoints();
        app.MapEmployeeEndpoints();
        app.MapKioskEndpoints();
        app.MapClockEndpoints();
        app.MapAdminEndpoints();

        return app;
    }
}
