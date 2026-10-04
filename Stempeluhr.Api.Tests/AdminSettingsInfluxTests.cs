using Stempeluhr.Api.Models;
using Xunit;

namespace Stempeluhr.Api.Tests;

/// <summary>
/// The admin UI has no Influx fields: a save must keep the configuration from
/// settings.json, and the token must never leave the server.
/// </summary>
public sealed class AdminSettingsInfluxTests
{
    private static readonly RuntimeSettings Current = new()
    {
        BaseUrl = "http://kimai.test",
        InfluxUrl = "http://nas:8086",
        InfluxOrg = "home",
        InfluxBucket = "stempeluhr",
        InfluxToken = "secret-token",
    };

    private static AdminSettingsUpdateDto Update(
        string? url = null, string? org = null, string? bucket = null, string? token = null) => new(
        BaseUrl: "http://kimai.test",
        AdminPassword: null,
        AdminApiToken: null,
        KeepAdminApiToken: true,
        DefaultProjectId: null,
        DefaultActivityId: null,
        PauseActivityId: null,
        TelegramBotToken: null,
        TelegramChatId: null,
        Employees: [],
        InfluxUrl: url,
        InfluxOrg: org,
        InfluxBucket: bucket,
        InfluxToken: token);

    [Fact]
    public void FromSettings_ReturnsConnectionButOnlyATokenFlag()
    {
        var dto = AdminSettingsDto.FromSettings(Current);

        Assert.Equal(("http://nas:8086", "home", "stempeluhr", true), (dto.InfluxUrl, dto.InfluxOrg, dto.InfluxBucket, dto.HasInfluxToken));
        Assert.False(AdminSettingsDto.FromSettings(new RuntimeSettings()).HasInfluxToken);
        Assert.DoesNotContain("secret-token", System.Text.Json.JsonSerializer.Serialize(dto));
    }

    [Fact]
    public void ToSettings_MissingOrBlankFields_KeepTheCurrentConfiguration()
    {
        foreach (var update in new[] { Update(), Update(" ", "", "  ", " ") })
        {
            var result = update.ToSettings(Current);

            Assert.Equal(("http://nas:8086", "home", "stempeluhr", "secret-token"),
                (result.InfluxUrl, result.InfluxOrg, result.InfluxBucket, result.InfluxToken));
        }
    }

    [Fact]
    public void ToSettings_NewValues_AreTrimmed()
    {
        var result = Update(" http://influx:8086 ", " org ", " bucket ", " new-token ").ToSettings(Current);

        Assert.Equal(("http://influx:8086", "org", "bucket", "new-token"),
            (result.InfluxUrl, result.InfluxOrg, result.InfluxBucket, result.InfluxToken));
        Assert.True(result.InfluxEnabled);
    }
}
