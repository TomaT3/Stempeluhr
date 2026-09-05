using System.Net;
using System.Text;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

public sealed class TelegramNotifierTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private static readonly DateTimeOffset Stamp = new(2026, 7, 1, 6, 12, 0, TimeSpan.Zero);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
            };

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Responder(request));
        }
    }

    private static TelegramNotifier CreateNotifier(
        RuntimeSettings settings, RecordingHandler handler, string? baseUrl = "https://api.telegram.org")
    {
        var factory = new StubHttpClientFactory(handler, baseUrl!);
        return new TelegramNotifier(new StubSettingsStore(settings), factory);
    }

    /// <summary>
    /// Erzeugt Clients über den übergebenen Handler. Die Factory ist bewusst
    /// kein echter IHttpClientFactory-Pool: Der Notifier disposed seinen
    /// Client pro Sendung (using) - der Handler wird mit disposed, die
    /// Aufzeichnung bleibt aber lesbar.
    /// </summary>
    private sealed class StubHttpClientFactory(HttpMessageHandler handler, string baseUrl) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler) { BaseAddress = new Uri(baseUrl) };
    }

    private sealed class StubSettingsStore(RuntimeSettings settings) : IRuntimeSettingsStore
    {
        public RuntimeSettings Load() => settings;

        public Task SaveAsync(RuntimeSettings settings, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static RuntimeSettings EnabledSettings() => new()
    {
        BaseUrl = "http://kimai.test",
        TelegramBotToken = "123456:ABC-secret",
        TelegramChatId = "-1001234567890"
    };

    [Fact]
    public async Task SendStampNotificationAsync_NotConfigured_DoesNotCallTelegram()
    {
        var handler = new RecordingHandler();
        var notifier = CreateNotifier(new RuntimeSettings { BaseUrl = "http://kimai.test" }, handler);

        await notifier.SendStampNotificationAsync("Max Mustermann", "start", Stamp, Berlin);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SendStampNotificationAsync_Configured_PostsToBotEndpointWithChatIdAndText()
    {
        var handler = new RecordingHandler();
        var notifier = CreateNotifier(EnabledSettings(), handler);

        await notifier.SendStampNotificationAsync("Max Mustermann", "start", Stamp, Berlin);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            new Uri("https://api.telegram.org/bot123456:ABC-secret/sendMessage"),
            request.RequestUri);

        var body = await request.Content!.ReadAsStringAsync();
        Assert.Contains("\"chat_id\":\"-1001234567890\"", body);
        Assert.Contains("Max Mustermann", body);
        Assert.Contains("eingestempelt", body);
        Assert.Contains("08:12", body);
    }

    [Fact]
    public async Task SendStampNotificationAsync_TelegramError_DoesNotThrow()
    {
        var handler = new RecordingHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        };
        var notifier = CreateNotifier(EnabledSettings(), handler);

        await notifier.SendStampNotificationAsync("Max Mustermann", "start", Stamp, Berlin);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SendStampNotificationAsync_NetworkFailure_DoesNotThrow()
    {
        var handler = new RecordingHandler
        {
            Responder = _ => throw new HttpRequestException("connection refused")
        };
        var notifier = CreateNotifier(EnabledSettings(), handler);

        await notifier.SendStampNotificationAsync("Max Mustermann", "start", Stamp, Berlin);

        Assert.Single(handler.Requests);
    }
}
