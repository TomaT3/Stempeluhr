using Stempeluhr.Api.Models;
using Xunit;

namespace Stempeluhr.Api.Tests;

/// <summary>
/// The admin settings DTOs round-trip the RuntimeSettings. Without explicit
/// fields there, every admin UI save would silently DROP the telegram config
/// (ToSettings builds a new RuntimeSettings from an explicit field list).
/// Old admin clients that don't know the telegram fields send null - the
/// keep-current-when-null semantics must preserve the stored values.
/// </summary>
public sealed class AdminSettingsTelegramTests
{
    private static RuntimeSettings SettingsWithTelegram() => new()
    {
        BaseUrl = "http://kimai.test",
        TelegramBotToken = "123456:ABC-secret",
        TelegramChatId = "-1001234567890"
    };

    [Fact]
    public void FromSettings_WithTokenAndChatId_SetsHasFlagAndPassesChatId()
    {
        var dto = AdminSettingsDto.FromSettings(SettingsWithTelegram());

        Assert.True(dto.HasTelegramBotToken);
        Assert.Equal("-1001234567890", dto.TelegramChatId);
    }

    [Fact]
    public void FromSettings_WithoutToken_HasFlagIsFalse()
    {
        var settings = new RuntimeSettings { BaseUrl = "http://kimai.test" };

        var dto = AdminSettingsDto.FromSettings(settings);

        Assert.False(dto.HasTelegramBotToken);
        Assert.Null(dto.TelegramChatId);
    }

    [Fact]
    public void ToSettings_NullUpdateFields_PreservesCurrentTelegramConfig()
    {
        // Simulates an OLD admin client that does not know the telegram fields.
        var update = new AdminSettingsUpdateDto(
            BaseUrl: "http://kimai.test",
            AdminPassword: null,
            AdminApiToken: null,
            KeepAdminApiToken: true,
            DefaultProjectId: null,
            DefaultActivityId: null,
            PauseActivityId: null,
            Employees: [],
            TelegramBotToken: null,
            TelegramChatId: null);

        var result = update.ToSettings(SettingsWithTelegram());

        Assert.Equal("123456:ABC-secret", result.TelegramBotToken);
        Assert.Equal("-1001234567890", result.TelegramChatId);
    }

    [Fact]
    public void ToSettings_EmptyUpdateFields_PreservesCurrentTelegramConfig()
    {
        var update = new AdminSettingsUpdateDto(
            BaseUrl: "http://kimai.test",
            AdminPassword: null,
            AdminApiToken: null,
            KeepAdminApiToken: true,
            DefaultProjectId: null,
            DefaultActivityId: null,
            PauseActivityId: null,
            Employees: [],
            TelegramBotToken: "   ",
            TelegramChatId: "");

        var result = update.ToSettings(SettingsWithTelegram());

        Assert.Equal("123456:ABC-secret", result.TelegramBotToken);
        Assert.Equal("-1001234567890", result.TelegramChatId);
    }

    [Fact]
    public void ToSettings_NewValues_AreAppliedAndTrimmed()
    {
        var update = new AdminSettingsUpdateDto(
            BaseUrl: "http://kimai.test",
            AdminPassword: null,
            AdminApiToken: null,
            KeepAdminApiToken: true,
            DefaultProjectId: null,
            DefaultActivityId: null,
            PauseActivityId: null,
            Employees: [],
            TelegramBotToken: "  789:NEW-token ",
            TelegramChatId: " -42 ");

        var result = update.ToSettings(SettingsWithTelegram());

        Assert.Equal("789:NEW-token", result.TelegramBotToken);
        Assert.Equal("-42", result.TelegramChatId);
    }
}
