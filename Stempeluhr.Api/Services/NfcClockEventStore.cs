using System.Collections.Concurrent;
using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

public sealed class NfcClockEventStore : INfcClockEventStore
{
    private readonly ConcurrentDictionary<string, NfcClockEventDto> _latestEvents = new(StringComparer.OrdinalIgnoreCase);

    public void Publish(NfcClockEventDto clockEvent)
    {
        _latestEvents[NormalizeTerminalId(clockEvent.TerminalId)] = clockEvent;
    }

    public NfcClockEventDto? GetLatest(string? terminalId)
    {
        return _latestEvents.TryGetValue(NormalizeTerminalId(terminalId), out var clockEvent)
            ? clockEvent
            : null;
    }

    private static string NormalizeTerminalId(string? terminalId)
    {
        return string.IsNullOrWhiteSpace(terminalId) ? "default" : terminalId.Trim();
    }
}
