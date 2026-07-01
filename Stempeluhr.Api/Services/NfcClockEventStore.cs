using System.Collections.Concurrent;
using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

public sealed class NfcClockEventStore : INfcClockEventStore
{
    private readonly ConcurrentDictionary<string, NfcClockEventDto> _latestEvents = new(StringComparer.OrdinalIgnoreCase);
    private NfcClockEventDto? _latestEvent;

    public void Publish(NfcClockEventDto clockEvent)
    {
        _latestEvents[NormalizeTerminalId(clockEvent.TerminalId)] = clockEvent;
        _latestEvent = clockEvent;
    }

    public NfcClockEventDto? GetLatest(string? terminalId, bool fallbackToAny = false)
    {
        if (_latestEvents.TryGetValue(NormalizeTerminalId(terminalId), out var clockEvent))
        {
            return clockEvent;
        }

        return fallbackToAny ? _latestEvent : null;
    }

    private static string NormalizeTerminalId(string? terminalId)
    {
        return string.IsNullOrWhiteSpace(terminalId) ? "default" : terminalId.Trim();
    }
}
