using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

public sealed class TelegramMessageFactoryTests
{
    private static TimeZoneInfo Berlin => TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    [Fact]
    public void Build_Start_ReturnsInTextWithNameAndLocalTime()
    {
        var stamp = new DateTimeOffset(2026, 7, 1, 6, 12, 0, TimeSpan.Zero); // CEST → 08:12

        var text = TelegramMessageFactory.Build("Max Mustermann", "start", stamp, Berlin);

        Assert.Equal("🟢 Max Mustermann · eingestempelt um 08:12", text);
    }

    [Fact]
    public void Build_Stop_ReturnsOutText()
    {
        var stamp = new DateTimeOffset(2026, 7, 1, 15, 3, 0, TimeSpan.Zero); // CEST → 17:03

        var text = TelegramMessageFactory.Build("Max Mustermann", "stop", stamp, Berlin);

        Assert.Equal("🔴 Max Mustermann · ausgestempelt um 17:03", text);
    }

    [Fact]
    public void Build_PauseStart_ReturnsPauseText()
    {
        var stamp = new DateTimeOffset(2026, 7, 1, 10, 31, 0, TimeSpan.Zero); // CEST → 12:31

        var text = TelegramMessageFactory.Build("Max Mustermann", "pauseStart", stamp, Berlin);

        Assert.Equal("🟡 Max Mustermann · Pause um 12:31", text);
    }

    [Fact]
    public void Build_PauseEnd_ReturnsPauseEndText()
    {
        var stamp = new DateTimeOffset(2026, 7, 1, 10, 47, 0, TimeSpan.Zero); // CEST → 12:47

        var text = TelegramMessageFactory.Build("Max Mustermann", "pauseEnd", stamp, Berlin);

        Assert.Equal("🟢 Max Mustermann · Pause beendet um 12:47", text);
    }

    [Fact]
    public void Build_WinterTime_UsesCETOffset()
    {
        var stamp = new DateTimeOffset(2026, 1, 15, 8, 12, 0, TimeSpan.Zero); // CET → 09:12

        var text = TelegramMessageFactory.Build("Max Mustermann", "start", stamp, Berlin);

        Assert.Equal("🟢 Max Mustermann · eingestempelt um 09:12", text);
    }

    [Fact]
    public void Build_UnknownAction_Throws()
    {
        var stamp = new DateTimeOffset(2026, 7, 1, 6, 0, 0, TimeSpan.Zero);

        Assert.Throws<ArgumentException>(() =>
            TelegramMessageFactory.Build("Max Mustermann", "toggle", stamp, Berlin));
    }
}
