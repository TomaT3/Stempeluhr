using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Stempeluhr.Api.Api;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

/// <summary>Issue #146: every stamp message says how the stamp was made.</summary>
public sealed class StampOriginTests
{
    private static TimeZoneInfo Berlin => TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private static readonly DateTimeOffset Stamp = new(2026, 7, 1, 6, 12, 0, TimeSpan.Zero); // CEST → 08:12

    [Theory]
    [InlineData(StampMethod.Card, "pi-1", false, "🟢 Max · eingestempelt um 08:12 · Karte am Terminal pi-1")]
    [InlineData(StampMethod.Pin, "pi-1", false, "🟢 Max · eingestempelt um 08:12 · PIN am Terminal pi-1")]
    [InlineData(StampMethod.Pin, null, true, "🟢 Max · eingestempelt um 08:12 · Webbrowser")]
    [InlineData(StampMethod.Card, null, false, "🟢 Max · eingestempelt um 08:12 · Karte am Terminal")]
    [InlineData(null, "pi-1", false, "🟢 Max · eingestempelt um 08:12 · Terminal pi-1")]
    [InlineData(StampMethod.Pin, null, false, "🟢 Max · eingestempelt um 08:12")]
    [InlineData(null, null, false, "🟢 Max · eingestempelt um 08:12")]
    public void Build_NamesTheSource_WhenKnown(StampMethod? method, string? terminalId, bool web, string expected)
    {
        var text = TelegramMessageFactory.Build("Max", "start", Stamp, Berlin, origin: new StampOrigin(method, terminalId, web));

        Assert.Equal(expected, text);
    }

    [Fact]
    public void Build_WithoutOrigin_StaysAsBefore()
    {
        Assert.Equal("🟢 Max · eingestempelt um 08:12", TelegramMessageFactory.Build("Max", "start", Stamp, Berlin));
    }

    [Fact]
    public void Build_ClipsLongTerminalIds()
    {
        var text = TelegramMessageFactory.Build("Max", "stop", Stamp, Berlin,
            origin: new StampOrigin(StampMethod.Card, new string('x', 200)));

        Assert.EndsWith("Karte am Terminal " + new string('x', 39) + "…", text);
    }

    [Fact]
    public void Create_TheVerifiedCredentialWinsOverTheClientHint()
    {
        Assert.Equal(StampMethod.Card, StampOrigin.Create(StampMethod.Card, null, "pin").Method);
        Assert.Equal(StampMethod.Pin, StampOrigin.Create(null, "pi-1", " PIN ").Method);
        Assert.Null(StampOrigin.Create(null, null, "nfc").Method);
        Assert.True(StampOrigin.Create(StampMethod.Pin, null, "web").Web);
        Assert.Null(StampOrigin.Create(null, "  ", null).TerminalId);
    }

    [Fact]
    public void OfflineNotice_SingleStamp_NamesTheOriginalSource()
    {
        var now = new DateTimeOffset(2026, 7, 1, 14, 0, 0, TimeSpan.Zero);
        var stamp = new AppliedOfflineStamp("anna", "Anna", "pauseStart",
            new DateTimeOffset(2026, 7, 1, 10, 3, 0, TimeSpan.Zero), Origin: new StampOrigin(StampMethod.Card, "pi-1"));

        var text = TelegramMessageFactory.BuildOfflineStampNotice("Anna", [stamp], Berlin, now);

        Assert.Equal("🟡 Anna · Pause um 12:03 (nachgetragen · Karte am Terminal pi-1)", text);
    }

    [Fact]
    public void OfflineNotice_SeveralStamps_NameTheSourcePerLine()
    {
        var now = new DateTimeOffset(2026, 7, 1, 14, 0, 0, TimeSpan.Zero);
        var text = TelegramMessageFactory.BuildOfflineStampNotice("Anna", [
            new AppliedOfflineStamp("anna", "Anna", "start", new DateTimeOffset(2026, 7, 1, 6, 0, 0, TimeSpan.Zero),
                Origin: new StampOrigin(StampMethod.Pin, "pi-1")),
            new AppliedOfflineStamp("anna", "Anna", "stop", new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero)),
        ], Berlin, now);

        Assert.Equal(
            "📥 Anna · 2 Stempel nachgetragen\n🟢 eingestempelt um 08:00 · PIN am Terminal pi-1\n🔴 ausgestempelt um 12:00",
            text);
    }

    private static readonly RuntimeSettings Terminals = new() { TerminalTokens = new() { ["pi-1"] = "secret" } };

    private static KioskClockRequest Verify(string? authorization, string? terminalId)
    {
        var request = new DefaultHttpContext().Request;
        if (authorization is not null) request.Headers.Authorization = authorization;
        if (terminalId is not null) request.Headers["X-Terminal-Id"] = terminalId;
        var body = new KioskClockRequest("max", "1234", "start", null) { AuthenticatedTerminalId = "forged" };
        return KioskEndpoints.WithVerifiedTerminal(request, body, Terminals, NullLogger.Instance);
    }

    [Fact]
    public void LiveClock_TakesTheTerminalOnlyFromAValidToken()
    {
        Assert.Equal("pi-1", Verify("Bearer secret", "pi-1").AuthenticatedTerminalId);
        Assert.Null(Verify(null, null).AuthenticatedTerminalId);
    }

    [Theory]
    [InlineData("Bearer rotated", "pi-1")]
    [InlineData("Bearer secret", "pi-2")]
    [InlineData(null, "pi-1")]
    public void LiveClock_WithAnInvalidToken_StillBooksWithoutATerminal(string? authorization, string terminalId)
    {
        // The kiosk drops a 401 as a permanent error: a rotated token must
        // not cost the stamp, PIN or card still authenticate it.
        var request = Verify(authorization, terminalId);

        Assert.Null(request.AuthenticatedTerminalId);
        Assert.Equal("1234", request.Pin);
    }
}
