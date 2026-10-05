using System.Text.Json;
using System.Text.Json.Serialization;
using Stempeluhr.Api.Models;
using Xunit;

namespace Stempeluhr.Api.Tests;

/// <summary>
/// <c>TelegramCorrectionChatId</c> and <c>TelegramApproverUserIds</c> go through
/// RuntimeSettings, the admin DTO, the mapping and the save like the other
/// Telegram fields. An old admin client does not know them and sends null:
/// a save must keep the stored values. An empty value deliberately clears them
/// (the admin UI has fields for these two).
/// </summary>
public sealed class AdminSettingsTelegramCorrectionTests
{
    private static RuntimeSettings Current() => new()
    {
        BaseUrl = "http://kimai.test",
        TelegramBotToken = "123456:ABC-secret",
        TelegramChatId = "-100",
        TelegramCorrectionChatId = "-300",
        TelegramApproverUserIds = [11, 22],
    };

    private static AdminSettingsUpdateDto Update(string? correctionChatId, IReadOnlyCollection<long>? approvers) => new(
        BaseUrl: "http://kimai.test",
        AdminPassword: null,
        AdminApiToken: null,
        KeepAdminApiToken: true,
        DefaultProjectId: null,
        DefaultActivityId: null,
        PauseActivityId: null,
        Employees: [],
        TelegramBotToken: null,
        TelegramChatId: null,
        TelegramCorrectionChatId: correctionChatId,
        TelegramApproverUserIds: approvers);

    [Fact]
    public void FromSettings_ReturnsBothFieldsAndNeverTheToken()
    {
        var dto = AdminSettingsDto.FromSettings(Current());

        Assert.Equal("-300", dto.TelegramCorrectionChatId);
        Assert.Equal([11L, 22L], dto.TelegramApproverUserIds);
        Assert.True(dto.HasTelegramBotToken);
        Assert.DoesNotContain("ABC-secret", JsonSerializer.Serialize(dto));
    }

    [Fact]
    public void FromSettings_Unset_IsEmpty()
    {
        var dto = AdminSettingsDto.FromSettings(new RuntimeSettings());

        Assert.Null(dto.TelegramCorrectionChatId);
        Assert.Empty(dto.TelegramApproverUserIds);
    }

    [Fact]
    public void ToSettings_NullFromAnOldClient_KeepsBothFields()
    {
        var result = Update(null, null).ToSettings(Current());

        Assert.Equal("-300", result.TelegramCorrectionChatId);
        Assert.Equal([11L, 22L], result.TelegramApproverUserIds);
        Assert.Equal("123456:ABC-secret", result.TelegramBotToken);
    }

    [Fact]
    public void ToSettings_NewValues_AreTrimmedAndCleaned()
    {
        var result = Update(" -400 ", [33, 33, 0, -5, 44]).ToSettings(Current());

        Assert.Equal("-400", result.TelegramCorrectionChatId);
        Assert.Equal([33L, 44L], result.TelegramApproverUserIds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ToSettings_EmptyValues_ClearBothFields(string chatId)
    {
        var result = Update(chatId, []).ToSettings(Current());

        Assert.Null(result.TelegramCorrectionChatId);
        Assert.Empty(result.TelegramApproverUserIds);
        Assert.False(result.TelegramCorrectionsEnabled);
    }

    [Fact]
    public void ToSettings_DoesNotShareTheApproverListWithTheCurrentSettings()
    {
        var current = Current();

        var result = Update(null, null).ToSettings(current);
        result.TelegramApproverUserIds.Add(99);

        Assert.Equal([11L, 22L], current.TelegramApproverUserIds);
    }

    [Fact]
    public void Settings_SurviveTheSettingsFileRoundTrip()
    {
        // Same options as RuntimeSettingsStore.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        var json = JsonSerializer.Serialize(Current(), options);
        var loaded = JsonSerializer.Deserialize<RuntimeSettings>(json, options)!;

        Assert.Contains("\"telegramApproverUserIds\"", json);
        Assert.DoesNotContain("telegramCorrectionsEnabled", json);
        Assert.Equal("-300", loaded.TelegramCorrectionChatId);
        Assert.Equal([11L, 22L], loaded.TelegramApproverUserIds);
        Assert.True(loaded.TelegramCorrectionsEnabled);
        Assert.Empty(JsonSerializer.Deserialize<RuntimeSettings>("""{"telegramApproverUserIds":null}""", options)!.TelegramApproverUserIds);
    }

    [Fact]
    public void TelegramCorrectionsEnabled_NeedsTokenAndCorrectionChat()
    {
        Assert.True(new RuntimeSettings { TelegramBotToken = "t", TelegramCorrectionChatId = "-1" }.TelegramCorrectionsEnabled);
        Assert.False(new RuntimeSettings { TelegramBotToken = "t", TelegramChatId = "-1" }.TelegramCorrectionsEnabled);
        Assert.False(new RuntimeSettings { TelegramCorrectionChatId = "-1" }.TelegramCorrectionsEnabled);
    }
}
