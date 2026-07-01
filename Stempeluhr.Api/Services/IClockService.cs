using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

public interface IClockService
{
    Task<KioskEmployeeSessionDto?> LoginWithPinAsync(string? pin, CancellationToken cancellationToken = default);

    Task<ClockStatusDto?> GetStatusAsync(ClockRequest request, CancellationToken cancellationToken = default);

    Task<ClockStatusDto?> StartAsync(ClockRequest request, CancellationToken cancellationToken = default);

    Task<ClockStatusDto?> StopAsync(ClockRequest request, CancellationToken cancellationToken = default);

    Task<ClockActionResponse> ClockAsync(KioskClockRequest request, CancellationToken cancellationToken = default);

    Task<NfcClockEventDto> IdentifyWithNfcCardAsync(NfcClockRequest request, CancellationToken cancellationToken = default);
}
