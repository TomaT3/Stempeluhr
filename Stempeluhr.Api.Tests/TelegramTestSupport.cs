using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Stempeluhr.Api.Models;
using Stempeluhr.Api.Services;

namespace Stempeluhr.Api.Tests;

/// <summary>One call the code under test made to the (fake) Telegram Bot API.</summary>
internal sealed record TelegramCall(string ClientName, string Token, string Method, JsonElement Body)
{
    public string? Text => Body.TryGetProperty("text", out var text) ? text.GetString() : null;

    /// <summary>The <c>callback_data</c> of all buttons in <c>reply_markup</c>, row by row.</summary>
    public IReadOnlyList<string> ButtonData => Buttons("callback_data");

    public IReadOnlyList<string> ButtonTexts => Buttons("text");

    public bool KeyboardIsEmpty => Body.TryGetProperty("reply_markup", out var markup)
        && markup.GetProperty("inline_keyboard").GetArrayLength() == 0;

    private IReadOnlyList<string> Buttons(string property)
        => Body.TryGetProperty("reply_markup", out var markup) && markup.TryGetProperty("inline_keyboard", out var rows)
            ? rows.EnumerateArray().SelectMany(row => row.EnumerateArray()).Select(button => button.GetProperty(property).GetString()!).ToArray()
            : [];
}

/// <summary>
/// HttpMessageHandler fake for the Telegram Bot API. Records every call by
/// method and answers like Telegram unless <see cref="Responder"/> says otherwise.
/// </summary>
internal sealed class FakeTelegram : HttpMessageHandler
{
    public const string Token = "123456:ABC-secret";
    public const string CorrectionChat = "-1001234567890";
    public const long CorrectionChatNumber = -1001234567890;
    public const long FirstMessageId = 42;

    private readonly object _gate = new();
    private readonly List<TelegramCall> _calls = [];
    private long _nextMessageId = FirstMessageId;

    /// <summary>Overrides the answer for a call; return null for the default answer.</summary>
    public Func<TelegramCall, HttpResponseMessage?>? Responder { get; set; }

    /// <summary>Queue of getUpdates answers; empty queue answers with no updates.</summary>
    public Queue<HttpResponseMessage> Updates { get; } = new();

    /// <summary>Calls of a method wait until the test completes its source.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource> Holds { get; } = new();

    public IReadOnlyList<TelegramCall> Calls
    {
        get
        {
            lock (_gate) return _calls.ToArray();
        }
    }

    public IReadOnlyList<TelegramCall> CallsTo(string method) => Calls.Where(call => call.Method == method).ToArray();

    public static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    /// <summary>A getUpdates answer with <c>callback_query</c> updates.</summary>
    public static HttpResponseMessage UpdatesOf(params object[] updates) => Json(new { ok = true, result = updates });

    /// <summary>A button tap in the correction chat.</summary>
    public static object Callback(
        long updateId, string data, long userId = 7, string firstName = "Max", string? username = "chef",
        long? chatId = CorrectionChatNumber, long messageId = FirstMessageId, string queryId = "q")
        => new
        {
            update_id = updateId,
            callback_query = new
            {
                id = $"{queryId}{updateId}",
                from = new { id = userId, first_name = firstName, username },
                message = chatId is { } chat ? new { message_id = messageId, chat = new { id = chat } } : null,
                data,
            },
        };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        var call = new TelegramCall(
            request.Options.TryGetValue(new HttpRequestOptionsKey<string>("client"), out var client) ? client : string.Empty,
            path[0]["bot".Length..], path[1], document.RootElement.Clone());
        lock (_gate) _calls.Add(call);

        // A hanging Telegram: the call is recorded, the answer waits for the test.
        if (Holds.TryGetValue(call.Method, out var hold))
        {
            await hold.Task.WaitAsync(cancellationToken);
        }

        if (Responder?.Invoke(call) is { } custom)
        {
            return custom;
        }

        switch (call.Method)
        {
            case "sendMessage":
                long messageId;
                lock (_gate) messageId = _nextMessageId++;
                return Json(new { ok = true, result = new { message_id = messageId, chat = new { id = CorrectionChatNumber } } });
            case "getUpdates":
                lock (_gate)
                {
                    if (Updates.Count > 0) return Updates.Dequeue();
                }
                return Json(new { ok = true, result = Array.Empty<object>() });
            default:
                return Json(new { ok = true, result = true });
        }
    }
}

/// <summary>Creates clients over one handler and remembers which named client asked.</summary>
internal sealed class FakeTelegramClientFactory(FakeTelegram handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        var client = new HttpClient(new NamedHandler(name, handler), disposeHandler: false)
        {
            BaseAddress = new Uri("https://api.telegram.org"),
        };
        return client;
    }

    private sealed class NamedHandler(string name, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Options.Set(new HttpRequestOptionsKey<string>("client"), name);
            return base.SendAsync(request, cancellationToken);
        }
    }
}

/// <summary>Settings that can change between calls, like the settings file does.</summary>
internal sealed class MutableSettingsStore(RuntimeSettings settings) : IRuntimeSettingsStore
{
    public RuntimeSettings Settings { get; set; } = settings;

    public RuntimeSettings Load() => Settings;

    public Task SaveAsync(RuntimeSettings settings, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

/// <summary>Collects log entries so tests can count warnings (no exception flood).</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries) Entries.Add((logLevel, formatter(state, exception), exception));
    }

    public int Count(LogLevel level) => Entries.Count(entry => entry.Level == level);
}
