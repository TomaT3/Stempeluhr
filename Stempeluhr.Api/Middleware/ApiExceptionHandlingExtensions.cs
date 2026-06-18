using Microsoft.AspNetCore.Diagnostics;
using Stempeluhr.Api.Services;

namespace Stempeluhr.Api.Middleware;

public static class ApiExceptionHandlingExtensions
{
    public static IApplicationBuilder UseApiExceptionHandling(this IApplicationBuilder app)
    {
        app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(async context =>
            {
                var exception = context.Features.Get<IExceptionHandlerPathFeature>()?.Error;
                var statusCode = exception is KimaiApiException apiException
                    ? (int)apiException.StatusCode
                    : StatusCodes.Status500InternalServerError;

                context.Response.StatusCode = statusCode;
                await context.Response.WriteAsJsonAsync(new
                {
                    message = exception is KimaiApiException
                        ? "Kimai konnte die Anfrage nicht verarbeiten."
                        : "Interner Fehler.",
                    details = exception is KimaiApiException api ? api.Details : null
                });
            });
        });

        return app;
    }
}
