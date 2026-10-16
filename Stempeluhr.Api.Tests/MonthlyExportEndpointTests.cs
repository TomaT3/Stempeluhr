using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Stempeluhr.Api.Api;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

/// <summary>The handler of GET /api/admin/export/month; the repo has no WebApplicationFactory, so it is called directly.</summary>
public sealed class MonthlyExportEndpointTests
{
    private sealed class Authorization(bool isAdmin) : IAdminAuthorizationService
    {
        public bool IsAdmin(HttpRequest request) => isAdmin;
        public bool CanBootstrapFromLocalhost(HttpRequest request) => false;
    }

    private sealed class Export : IMonthlyExportService
    {
        public (int Year, int Month)? Requested { get; private set; }

        public Task<MonthlyExportFile> ExportAsync(int year, int month, CancellationToken cancellationToken = default)
        {
            Requested = (year, month);
            return Task.FromResult(new MonthlyExportFile(MonthlyExportService.FileName(year, month), [1, 2, 3]));
        }
    }

    private static async Task<(HttpContext Context, Export Export)> Call(bool isAdmin, int? year, int? month)
    {
        var export = new Export();
        var context = new DefaultHttpContext();
        context.RequestServices = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory>()
            .AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>))
            .BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        var result = await AdminEndpoints.ExportMonthAsync(year, month, context.Request, new Authorization(isAdmin), export, CancellationToken.None);
        await result.ExecuteAsync(context);
        return (context, export);
    }

    [Fact]
    public async Task WithoutAdminPassword_Returns401AndDoesNotExport()
    {
        var (context, export) = await Call(isAdmin: false, 2026, 10);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Null(export.Requested);
    }

    [Theory]
    [InlineData(null, 10)]
    [InlineData(2026, null)]
    [InlineData(2026, 0)]
    [InlineData(2026, 13)]
    [InlineData(1, 10)]
    public async Task InvalidMonth_Returns400(int? year, int? month)
    {
        var (context, export) = await Call(isAdmin: true, year, month);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Null(export.Requested);
    }

    [Fact]
    public async Task ValidRequest_ReturnsTheWorkbookAsAttachment()
    {
        var (context, export) = await Call(isAdmin: true, 2026, 10);

        Assert.Equal((2026, 10), export.Requested);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", context.Response.ContentType);
        var disposition = context.Response.Headers.ContentDisposition.ToString();
        Assert.StartsWith("attachment", disposition);
        Assert.Contains("Stempeluhr-Export-2026-10.xlsx", disposition);
        context.Response.Body.Position = 0;
        Assert.Equal(new byte[] { 1, 2, 3 }, ((MemoryStream)context.Response.Body).ToArray());
    }
}
