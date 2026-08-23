using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

public interface IOfflineClockService
{
    /// <summary>
    /// Processes a batch of queued NFC scans in order. Each event is applied
    /// against Kimai with its original scan timestamp (backdating). Duplicate
    /// event IDs are acknowledged without re-processing (idempotency).
    /// Kimai outages are handled by buffering events in an internal outbox
    /// that is retried automatically.
    /// </summary>
    Task<OfflineSyncResultDto> SyncAsync(IReadOnlyList<OfflineNfcClockEventDto> events, CancellationToken cancellationToken = default);

    /// <summary>
    /// Processes a batch of queued kiosk actions (PIN/touch driven) in order,
    /// with the same idempotency and outbox semantics as <see cref="SyncAsync"/>.
    /// </summary>
    Task<OfflineSyncResultDto> SyncKioskAsync(IReadOnlyList<OfflineKioskClockEventDto> events, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies anything still waiting in the internal outbox (events that were
    /// buffered while Kimai was unreachable). Called after each sync and
    /// periodically by a background service, so outbox events are retried even
    /// without a new request from a client.
    /// </summary>
    Task FlushOutboxAsync(CancellationToken cancellationToken = default);
}
