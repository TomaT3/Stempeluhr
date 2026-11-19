using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Stempeluhr.Api.Api;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

/// <summary>Wartungs-WLAN und Wartungs-PIN: Admin-Save, Admin-DTO und Terminal-Endpunkt.</summary>
public sealed class TerminalMaintenanceTests
{
    private static readonly RuntimeSettings Current = new()
    {
        BaseUrl = "http://kimai.test",
        TerminalTokens = new() { ["pi-1"] = "terminal-secret" },
        ServiceWifiSsid = "Stempeluhr-Service",
        ServiceWifiPsk = "hotspot-secret",
        MaintenancePinHash = TerminalMaintenance.HashPin("482915"),
        Employees = [new() { Id = "max", Pin = "1234" }],
    };

    private sealed class Store(RuntimeSettings settings) : IRuntimeSettingsStore
    {
        public RuntimeSettings Load() => settings;
        public Task SaveAsync(RuntimeSettings value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static AdminSettingsUpdateDto Update(
        string? ssid = null, string? psk = null, string? pin = null, bool clearPin = false) => new(
        BaseUrl: "http://kimai.test",
        AdminPassword: null,
        AdminApiToken: null,
        KeepAdminApiToken: true,
        DefaultProjectId: null,
        DefaultActivityId: null,
        PauseActivityId: null,
        TelegramBotToken: null,
        TelegramChatId: null,
        Employees: [new(Id: "max", KimaiUserId: null, DisplayName: "Max", Pin: "1234", NfcCardId: null,
            ApiToken: null, KeepApiToken: true, ProjectId: null, ActivityId: null, Color: null, ImageUrl: null,
            Description: null, Tags: null, Billable: false, IsEnabled: true)],
        ServiceWifiSsid: ssid,
        ServiceWifiPsk: psk,
        MaintenancePin: pin,
        ClearMaintenancePin: clearPin);

    private static async Task<(int Status, string Body, string CacheControl)> CallEndpoint(
        RuntimeSettings settings, string? terminalId, string? token)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory>()
                .AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>))
                .BuildServiceProvider(),
        };
        if (terminalId is not null) context.Request.Headers["X-Terminal-Id"] = terminalId;
        if (token is not null) context.Request.Headers.Authorization = $"Bearer {token}";
        context.Response.Body = new MemoryStream();
        await KioskEndpoints.GetMaintenance(context, new Store(settings)).ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync(),
            context.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("pi-1", "wrong")]
    [InlineData("pi-2", "terminal-secret")]
    public async Task Endpoint_WithoutValidTerminalToken_Returns401WithoutSecrets(string? terminalId, string? token)
    {
        var (status, body, _) = await CallEndpoint(Current, terminalId, token);

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.DoesNotContain("hotspot-secret", body);
        Assert.DoesNotContain("pbkdf2", body);
    }

    [Fact]
    public async Task Endpoint_WithTerminalToken_ReturnsServiceWifiAndPinHash()
    {
        var (status, body, cacheControl) = await CallEndpoint(Current, "pi-1", "terminal-secret");

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal("no-store", cacheControl);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("Stempeluhr-Service", json.RootElement.GetProperty("serviceWifi").GetProperty("ssid").GetString());
        Assert.Equal("hotspot-secret", json.RootElement.GetProperty("serviceWifi").GetProperty("psk").GetString());
        Assert.Equal(Current.MaintenancePinHash, json.RootElement.GetProperty("maintenancePinHash").GetString());
    }

    [Fact]
    public async Task Endpoint_WithoutConfiguration_ReturnsNulls()
    {
        var settings = new RuntimeSettings { TerminalTokens = new() { ["pi-1"] = "terminal-secret" } };

        var (status, body, _) = await CallEndpoint(settings, "pi-1", "terminal-secret");

        Assert.Equal(StatusCodes.Status200OK, status);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("serviceWifi").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("maintenancePinHash").ValueKind);
    }

    [Fact]
    public void AdminDto_ContainsOnlyFlagsForSecrets()
    {
        var dto = AdminSettingsDto.FromSettings(Current);
        var json = JsonSerializer.Serialize(dto);

        Assert.Equal(("Stempeluhr-Service", true, true), (dto.ServiceWifiSsid, dto.HasServiceWifiPsk, dto.HasMaintenancePin));
        Assert.DoesNotContain("hotspot-secret", json);
        Assert.DoesNotContain("pbkdf2", json);
        Assert.False(AdminSettingsDto.FromSettings(new RuntimeSettings()).HasMaintenancePin);
    }

    [Fact]
    public void Save_OldClientOrBlankSecrets_KeepTheCurrentValues()
    {
        foreach (var update in new[] { Update(), Update(ssid: "Stempeluhr-Service", psk: "", pin: " ") })
        {
            var result = update.ToSettings(Current);

            Assert.Equal(("Stempeluhr-Service", "hotspot-secret", Current.MaintenancePinHash),
                (result.ServiceWifiSsid, result.ServiceWifiPsk, result.MaintenancePinHash));
            Assert.Null(update.ValidateMaintenance(result));
        }
    }

    [Fact]
    public void Save_EmptySsid_RemovesServiceWifiWithPassword()
    {
        var result = Update(ssid: "").ToSettings(Current);

        Assert.Null(result.ServiceWifiSsid);
        Assert.Null(result.ServiceWifiPsk);
    }

    [Fact]
    public void Save_NewPin_IsStoredOnlyAsVerifiableHash()
    {
        var update = Update(pin: " 739104 ");
        var result = update.ToSettings(Current);

        Assert.Null(update.ValidateMaintenance(result));
        Assert.DoesNotContain("739104", result.MaintenancePinHash);
        Assert.True(TerminalMaintenance.VerifyPin("739104", result.MaintenancePinHash));
        Assert.False(TerminalMaintenance.VerifyPin("482915", result.MaintenancePinHash));
        Assert.Null(Update(clearPin: true).ToSettings(Current).MaintenancePinHash);
    }

    [Theory]
    [InlineData("12345", "6 bis 12 Ziffern")]
    [InlineData("12345a", "6 bis 12 Ziffern")]
    [InlineData("1234567890123", "6 bis 12 Ziffern")]
    public void Save_InvalidPin_IsRejectedBeforeHashing(string pin, string message)
    {
        var update = Update(pin: pin);

        Assert.Contains(message, update.ValidateMaintenancePin());
        // Never hashed: the stored hash stays.
        Assert.Equal(Current.MaintenancePinHash, update.ToSettings(Current).MaintenancePinHash);
    }

    private static AdminEmployeeUpdateDto Employee(string pin) => new(Id: "max", KimaiUserId: null, DisplayName: "Max",
        Pin: pin, NfcCardId: null, ApiToken: null, KeepApiToken: true, ProjectId: null, ActivityId: null, Color: null,
        ImageUrl: null, Description: null, Tags: null, Billable: false, IsEnabled: true);

    [Fact]
    public void Save_PinOfAnEmployee_IsRejected()
    {
        var update = Update(pin: "123456") with { Employees = [Employee(" 123456 ")] };

        Assert.Contains("Mitarbeiter-PIN", update.ValidateMaintenancePin());
    }

    [Fact]
    public void Save_EmployeeLaterGivenTheMaintenancePin_IsRejected()
    {
        // Current holds the hash of 482915; the maintenance PIN itself is not resent.
        var update = Update() with { Employees = [Employee("482915")] };
        Assert.Null(update.ValidateMaintenancePin());

        Assert.Contains("Mitarbeiter-PIN entspricht der Wartungs-PIN", update.ValidateMaintenance(update.ToSettings(Current)));
        var other = Update() with { Employees = [Employee("482916")] };
        Assert.Null(other.ValidateMaintenance(other.ToSettings(Current)));
    }

    [Fact]
    public void Save_ChangedSsidWithoutNewPassword_IsRejected()
    {
        var update = Update(ssid: "Handy-B", psk: "");
        var result = update.ToSettings(Current);

        Assert.Null(result.ServiceWifiPsk);
        Assert.Contains("fehlt das Passwort", update.ValidateMaintenance(result));
        Assert.Equal("neues-geheim", Update(ssid: "Handy-B", psk: "neues-geheim").ToSettings(Current).ServiceWifiPsk);
    }

    [Fact]
    public void Save_SsidKeepsSpacesAtTheEdges()
    {
        var result = Update(ssid: " Service ", psk: "hotspot-geheim").ToSettings(Current);

        Assert.Equal(" Service ", result.ServiceWifiSsid);
        Assert.Null(Update(ssid: "   ").ToSettings(Current).ServiceWifiSsid);
    }

    [Theory]
    [InlineData("Neu", null, "fehlt das Passwort")]
    [InlineData("Neu", "kurz", "8 bis 63")]
    [InlineData("Diese SSID ist eindeutig zu lang fuer WLAN", "langes-passwort", "32 Byte")]
    public void Save_InvalidServiceWifi_IsRejected(string ssid, string? psk, string message)
    {
        var update = Update(ssid: ssid, psk: psk);

        Assert.Contains(message, update.ValidateMaintenance(update.ToSettings(new RuntimeSettings())));
    }

    [Fact]
    public void VerifyPin_RejectsMalformedHashes()
    {
        foreach (var stored in new[] { null, "", "plain", "pbkdf2-sha256$0$00$00", "pbkdf2-sha256$1000$zz$00", "md5$1$00$00" })
            Assert.False(TerminalMaintenance.VerifyPin("482915", stored));
    }
}
