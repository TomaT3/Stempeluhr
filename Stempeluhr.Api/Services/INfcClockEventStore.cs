using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

public interface INfcClockEventStore
{
    void Publish(NfcClockEventDto clockEvent);

    NfcClockEventDto? GetLatest(string? terminalId);
}
