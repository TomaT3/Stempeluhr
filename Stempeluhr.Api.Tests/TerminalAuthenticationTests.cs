using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

public sealed class TerminalAuthenticationTests
{
    [Theory]
    [InlineData("Basic abc", false)]
    [InlineData("Bearer abc", true)]
    [InlineData("bearer abc", true)]
    [InlineData("Bearer", true)]
    public void OnlyTerminalCredentialsOptIntoTerminalAuthentication(string authorization, bool expected)
    {
        var request = new DefaultHttpContext().Request;
        request.Headers.Authorization = authorization;
        Assert.Equal(expected, TerminalAuthentication.IsTerminalRequest(request));
        request.Headers["X-Terminal-Id"] = "pi-1";
        Assert.True(TerminalAuthentication.IsTerminalRequest(request));
    }

    [Fact]
    public void NullTerminalTokensAreNormalizedForAuthenticationAndAdmin()
    {
        var settings = JsonSerializer.Deserialize<RuntimeSettings>("{\"terminalTokens\":null}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Empty(settings.TerminalTokens);
        var request = new DefaultHttpContext().Request;
        request.Headers.Authorization = "Bearer token";
        request.Headers["X-Terminal-Id"] = "pi-1";
        Assert.Null(TerminalAuthentication.Authenticate(request, settings));
        Assert.Empty(AdminSettingsDto.FromSettings(settings).TerminalIds);
    }

    [Fact]
    public void TokensAreBoundToTerminalAndCanBeRotatedOrRevoked()
    {
        var settings = new RuntimeSettings { TerminalTokens = new() { ["pi-1"] = "secret" } };
        var request = new DefaultHttpContext().Request;
        Assert.Null(TerminalAuthentication.Authenticate(request, settings));
        request.Headers.Authorization = "Bearer secret";
        request.Headers["X-Terminal-Id"] = "pi-2";
        Assert.Null(TerminalAuthentication.Authenticate(request, settings));
        request.Headers["X-Terminal-Id"] = "pi-1";
        Assert.Equal("pi-1", TerminalAuthentication.Authenticate(request, settings));
        settings.TerminalTokens["pi-1"] = "rotated";
        Assert.Null(TerminalAuthentication.Authenticate(request, settings));
        request.Headers.Authorization = "Bearer rotated";
        Assert.Equal("pi-1", TerminalAuthentication.Authenticate(request, settings));
        settings.TerminalTokens.Clear();
        Assert.Null(TerminalAuthentication.Authenticate(request, settings));
    }

    [Fact]
    public void ClientCannotForgeAuthenticatedTerminalMetadata()
    {
        var entry = JsonSerializer.Deserialize<OfflineKioskClockEventDto>("""
            {"eventId":"e1","employeeId":"max","action":"start",
             "performedAt":"2026-09-28T08:00:00Z","authenticatedTerminalId":"pi-1"}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Null(entry!.AuthenticatedTerminalId);
    }

    [Fact]
    public void CatalogContainsActiveEmployeesAndSaltedVerifiersWithoutSecrets()
    {
        var settings = new RuntimeSettings { Employees = [
            new() { Id = "max", DisplayName = "Max", Pin = "7391", ApiToken = "kimai-secret", NfcCardId = "04:ab" },
            new() { Id = "inactive", IsEnabled = false, ApiToken = "other" },
            new() { Id = "unconfigured" }
        ] };
        var json = JsonSerializer.Serialize(TerminalAuthentication.Catalog(settings, new EmployeeService()));
        Assert.DoesNotContain("\"7391\"", json);
        Assert.DoesNotContain("kimai-secret", json);
        using var document = JsonDocument.Parse(json);
        var entry = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("04AB", entry.GetProperty("cardId").GetString());
        var salt = entry.GetProperty("salt").GetString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{salt}:7391"))).ToLowerInvariant();
        Assert.Equal(hash, entry.GetProperty("verifier").GetString());
        Assert.NotEqual(json, JsonSerializer.Serialize(TerminalAuthentication.Catalog(settings, new EmployeeService())));
    }

    [Fact]
    public void AdminSavePreservesTerminalSecretsButResponseOnlyContainsIds()
    {
        var settings = new RuntimeSettings { TerminalTokens = new() { ["pi-1"] = "terminal-secret" } };
        var dto = AdminSettingsDto.FromSettings(settings);
        Assert.Equal("pi-1", Assert.Single(dto.TerminalIds));
        Assert.DoesNotContain("terminal-secret", JsonSerializer.Serialize(dto));
        var update = new AdminSettingsUpdateDto("", null, null, true, null, null, null, null, null, []);
        Assert.Equal("terminal-secret", update.ToSettings(settings).TerminalTokens["pi-1"]);
    }
}
