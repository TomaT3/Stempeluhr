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
        // Only drop events the server actually accepted or classified as duplicates.
        // Buffered entries stay queued until Kimai has caught up.
        const bufferedIds = new Set<string>();
        for (const result of results) {
          for (const detail of ((result as unknown as { results?: Array<{ eventId?: string; status?: string }> }).results ?? [])) {
            if (detail?.status === 'buffered' && detail.eventId) {
              bufferedIds.add(detail.eventId);
            }
          }
        }

        this.queued.update(entries => entries.filter(entry =>
          !('eventId' in entry.event) || bufferedIds.has((entry.event as { eventId: string }).eventId),
        ));
        this.writeStorage(this.queued());
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

  private scheduleRetry(): void {
    if (this.syncTimer !== null) {
      return;
    }

    this.syncTimer = window.setTimeout(() => {
      this.syncTimer = null;
      if (this.queued().length > 0) {
        this.syncNow().subscribe();
      }
    }, SYNC_RETRY_MS);
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
    try {
      window.localStorage.setItem(QUEUE_STORAGE_KEY, JSON.stringify(entries));
    } catch {
      // Storage full/blocked: keep the in-memory queue so nothing is lost
      // during this browser session.
    }
  }
}
