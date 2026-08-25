import { HttpClient } from '@angular/common/http';
import { inject, Injectable, InjectionToken } from '@angular/core';
import { catchError, map, of } from 'rxjs';

/**
 * Port of the Pi NFC agent's loopback scan server. Overridable via
 * `provide: LOCAL_NFC_SCAN_PORT, useValue: ...` (e.g. from an environment
 * file); the agent defaults to the same port.
 */
export const LOCAL_NFC_SCAN_PORT = new InjectionToken<number>('LOCAL_NFC_SCAN_PORT', {
  factory: () => 8737,
});

export interface LocalNfcScan {
  cardId: string;
  scannedAt: string;
  consumed: boolean;
}

/**
 * Polls the local NFC agent's loopback HTTP server for the latest card scan
 * while the backend is unreachable. Errors (agent not running) are swallowed
 * silently - polling simply yields nothing.
 *
 * A scan is only emitted once per physical card touch: it must be unconsumed
 * AND either carry a scannedAt newer than anything seen before or a cardId
 * that differs from the last seen one.
 */
@Injectable({ providedIn: 'root' })
export class LocalNfcScanService {
  private readonly http = inject(HttpClient);
  private readonly port = inject(LOCAL_NFC_SCAN_PORT);

  private lastScannedAt: string | null = null;
  private lastCardId: string | null = null;

  /**
   * Fetches /scan/latest once. Emits the scan only if it is new and
   * unconsumed; every other outcome (no scan, repeat scan, consumed scan,
   * network error) emits null so callers can treat this as fire-and-forget.
   */
  poll() {
    return this.http.get<LocalNfcScan>(`http://127.0.0.1:${this.port}/scan/latest`).pipe(
      catchError(() => of<LocalNfcScan | null>(null)),
      // A failed agent request must never throw - it just yields no scan.
      map(scan => (this.isNewScan(scan) ? scan : null)),
    );
  }

  /** Marks the current agent scan as consumed so it is not re-emitted. */
  ack() {
    return this.http.post(`http://127.0.0.1:${this.port}/scan/ack`, {}).pipe(catchError(() => of(null)));
  }

  /**
   * Pure dedupe check kept separate from poll() so it stays trivially
   * testable: a scan counts as new when it was never seen before, is
   * fresher than the newest seen timestamp, or switches the card.
   */
  isNewScan(scan: LocalNfcScan | null): scan is LocalNfcScan {
    if (!scan || scan.consumed) {
      return false;
    }

    const isNewTimestamp = this.lastScannedAt === null || scan.scannedAt > this.lastScannedAt;
    const isDifferentCard = this.lastCardId !== null && scan.cardId !== this.lastCardId;
    const isFirstScanEver = this.lastScannedAt === null && this.lastCardId === null;
    if (isFirstScanEver || isNewTimestamp || isDifferentCard) {
      this.lastScannedAt = scan.scannedAt;
      this.lastCardId = scan.cardId;
      return true;
    }

    return false;
  }
}
