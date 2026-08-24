import { HttpClient } from '@angular/common/http';
import { inject, Injectable, signal } from '@angular/core';
import { forkJoin, Observable, of } from 'rxjs';
import { catchError, map } from 'rxjs/operators';

import {
  OfflineKioskClockEvent,
  OfflineNfcClockEvent,
  OfflineSyncResult,
} from '../models/offline.models';

const QUEUE_STORAGE_KEY = 'stempeluhr.offline-queue.v1';
const SYNC_RETRY_MS = 15_000;
// Slower cadence for events the server already accepted into its own outbox
// ("buffered"): they only need re-sending as a safety net against an API
// restart losing that in-memory outbox before it flushes.
const SYNC_RETRY_BUFFERED_MS = 60_000;

interface StoredOfflineEvent {
  kind: 'nfc' | 'kiosk';
  event: OfflineNfcClockEvent | OfflineKioskClockEvent;
}

/**
 * Queues clock events in localStorage while the backend (or the internet) is
 * unreachable and replays them once connectivity returns. NFC scans are
 * synced via the reader token; PIN-driven kiosk actions use the public
 * kiosk sync endpoint.
 */
@Injectable({ providedIn: 'root' })
export class OfflineQueueService {
  private readonly http = inject(HttpClient);
  private readonly queued = signal<StoredOfflineEvent[]>(this.readStorage());
  readonly pendingCount = this.queued.asReadonly();

  private syncTimer: number | null = null;

  constructor() {
    // Flush a queue left over from a previous browser session (e.g. after a
    // kiosk restart) as soon as the app starts - without waiting for the next
    // failed clock action to trigger the retry timer.
    if (this.queued().length > 0) {
      this.syncNow().subscribe();
    }
  }

  /**
   * Queues an NFC reader event for the /api/nfc/clock/sync replay. Note:
   * this path requires a trusted X-Nfc-Reader-Token header, which browsers
   * must not hold - it exists only for non-browser integrations that share
   * this service. Kiosk/browser offline stamping goes through
   * enqueueKiosk() instead.
   */
  enqueueNfc(event: OfflineNfcClockEvent): void {
    this.enqueue({ kind: 'nfc', event });
  }

  enqueueKiosk(event: OfflineKioskClockEvent): void {
    this.enqueue({ kind: 'kiosk', event });
  }

  /** Attempts to flush everything; returns an observable that completes when done. */
  syncNow(): Observable<OfflineSyncResult[]> {
    const snapshot = this.queued();
    if (snapshot.length === 0) {
      return of([]);
    }

    const nfcEvents = snapshot
      .filter(entry => entry.kind === 'nfc')
      .map(entry => entry.event as OfflineNfcClockEvent);
    const kioskEvents = snapshot
      .filter(entry => entry.kind === 'kiosk')
      .map(entry => entry.event as OfflineKioskClockEvent);

    const calls: Observable<OfflineSyncResult>[] = [];
    if (nfcEvents.length > 0) {
      calls.push(
        this.http.post<OfflineSyncResult>('/api/nfc/clock/sync', { events: nfcEvents }),
      );
    }
    if (kioskEvents.length > 0) {
      calls.push(
        this.http.post<OfflineSyncResult>('/api/kiosk/clock/sync', { events: kioskEvents }),
      );
    }

    return forkJoin(calls).pipe(
      map(results => {
        // Drop everything the server definitively resolved (applied, duplicate
        // or permanently rejected). Only events the server still holds in its
        // outbox ("buffered") or that were not mentioned in the response stay
        // queued until the next attempt.
        const bufferedIds = new Set<string>();
        const mentionedIds = new Set<string>();
        for (const result of results) {
          for (const detail of result.results ?? []) {
            if (!detail.eventId) {
              continue;
            }

            mentionedIds.add(detail.eventId);
            if (detail.status === 'buffered') {
              bufferedIds.add(detail.eventId);
            }
          }
        }

        this.queued.update(entries => entries.filter(entry =>
          bufferedIds.has(entry.event.eventId) || !mentionedIds.has(entry.event.eventId),
        ));
        this.writeStorage(this.queued());

        // Keep a slow retry running while events remain queued: "buffered"
        // events sit in the API's IN-MEMORY outbox with their event IDs
        // already freed, so if the API restarts before its outbox flush,
        // this client queue is the only copy left - it must be re-sent
        // (they will then apply normally) instead of silently expiring.
        if (this.queued().length > 0) {
          this.scheduleRetry(SYNC_RETRY_BUFFERED_MS);
        }

        return results;
      }),
      catchError(() => {
        this.scheduleRetry();
        return of([]);
      }),
    );
  }

  private enqueue(entry: StoredOfflineEvent): void {
    this.queued.update(entries => [...entries, entry]);
    this.writeStorage(this.queued());
    this.scheduleRetry();
  }

  private scheduleRetry(delayMs: number = SYNC_RETRY_MS): void {
    if (this.syncTimer !== null) {
      return;
    }

    this.syncTimer = window.setTimeout(() => {
      this.syncTimer = null;
      if (this.queued().length > 0) {
        this.syncNow().subscribe();
      }
    }, delayMs);
  }

  private readStorage(): StoredOfflineEvent[] {
    try {
      const raw = window.localStorage.getItem(QUEUE_STORAGE_KEY);
      const parsed = raw ? (JSON.parse(raw) as StoredOfflineEvent[]) : [];
      return Array.isArray(parsed) ? parsed : [];
    } catch {
      return [];
    }
  }

  private writeStorage(entries: StoredOfflineEvent[]): void {
    // Accepted trade-off (documented in the README): kiosk events are
    // persisted with their PIN so an offline stamp survives a kiosk restart.
    // localStorage is readable by anyone with access to the kiosk device or
    // via a successful XSS - treated as trusted hardware here. A terminal /
    // reader token would remove this and is tracked as a follow-up.
    try {
      window.localStorage.setItem(QUEUE_STORAGE_KEY, JSON.stringify(entries));
    } catch {
      // Storage full/blocked: keep the in-memory queue so nothing is lost
      // during this browser session.
    }
  }
}
