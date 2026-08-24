import { HttpClient } from '@angular/common/http';
import { inject, Injectable, signal } from '@angular/core';
import { defer, firstValueFrom, Observable, of, Subject } from 'rxjs';
import { catchError } from 'rxjs/operators';

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
/**
 * Must mirror MaxSyncBatchSize in Stempeluhr.Api/Api/NfcEndpoints.cs: the API
 * rejects batches above this size with 400 WITHOUT processing any event, so a
 * queue larger than one batch has to be split here - otherwise every sync
 * would fail forever and events beyond the limit would never reach Kimai.
 */
const MAX_SYNC_BATCH_SIZE = 100;

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

  private readonly recoveredSubject = new Subject<void>();
  /**
   * Emits once per sync run that received at least one successful server
   * response. Hosts without NFC polling (the /clock default route has no
   * terminalId) use this as their only connectivity signal.
   */
  readonly recovered: Observable<void> = this.recoveredSubject.asObservable();

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

    // flushQueue is async because the chunks must be sent SEQUENTIALLY:
    // each response decides whether the next chunk may go out at all.
    return defer(() => this.flushQueue(snapshot)).pipe(
      catchError(() => {
        this.scheduleRetry();
        return of([] as OfflineSyncResult[]);
      }),
    );
  }

  /**
   * Replays the queue in server-sized batches. A long outage can outgrow a
   * single request (the API caps batches at MaxSyncBatchSize and answers
   * bigger ones with 400 - without processing ANY event), so each chunk is
   * sent separately and resolved events are dropped progressively. When the
   * API buffers a whole chunk ("Kimai nicht erreichbar"), the remaining
   * events stay queued: sending more would only pile them onto the same
   * outbox backlog instead of making progress.
   */
  private async flushQueue(snapshot: StoredOfflineEvent[]): Promise<OfflineSyncResult[]> {
    const groups: Array<readonly [string, Array<OfflineNfcClockEvent | OfflineKioskClockEvent>]> = [
      ['/api/nfc/clock/sync', snapshot.filter(e => e.kind === 'nfc').map(e => e.event as OfflineNfcClockEvent)],
      ['/api/kiosk/clock/sync', snapshot.filter(e => e.kind === 'kiosk').map(e => e.event as OfflineKioskClockEvent)],
    ];

    const results: OfflineSyncResult[] = [];
    const mentionedIds = new Set<string>();
    const bufferedIds = new Set<string>();
    let bufferingEverything = false;

    replay:
    for (const [endpoint, events] of groups) {
      for (let offset = 0; offset < events.length; offset += MAX_SYNC_BATCH_SIZE) {
        if (bufferingEverything) {
          break replay;
        }

        try {
          const result = await firstValueFrom(
            this.http.post<OfflineSyncResult>(endpoint, {
              events: events.slice(offset, offset + MAX_SYNC_BATCH_SIZE),
            }),
          );
          results.push(result);

          let chunkFullyBuffered = (result.results?.length ?? 0) > 0;
          for (const detail of result.results ?? []) {
            if (!detail.eventId) {
              continue;
            }

            mentionedIds.add(detail.eventId);
            if (detail.status === 'buffered') {
              bufferedIds.add(detail.eventId);
            } else {
              chunkFullyBuffered = false;
            }
          }
          bufferingEverything = chunkFullyBuffered;
        } catch {
          // Network or 5xx failure mid-run: stop here. Events already
          // resolved by earlier chunks are dropped below, so partial
          // progress survives; the rest retries on the timer.
          break;
        }
      }
    }

    this.dropResolved(bufferedIds, mentionedIds);

    // The backend answered at least once - connectivity is back.
    if (results.length > 0) {
      this.recoveredSubject.next();
    }

    // Keep a retry timer running while events remain queued. "buffered"
    // events sit in the API's IN-MEMORY outbox with their event IDs already
    // freed, so if the API restarts before its outbox flush, this client
    // queue is the only copy left - they get the slow safety-net cadence.
    // Events the server has not even seen yet (interrupted run above) still
    // need the normal cadence.
    if (this.queued().length > 0) {
      const allBuffered = this.queued().every(entry => bufferedIds.has(entry.event.eventId));
      this.scheduleRetry(allBuffered ? SYNC_RETRY_BUFFERED_MS : SYNC_RETRY_MS);
    }

    return results;
  }

  /** Removes every event the server definitively resolved from the queue. */
  private dropResolved(bufferedIds: Set<string>, mentionedIds: Set<string>): void {
    this.queued.update(entries => entries.filter(entry =>
      bufferedIds.has(entry.event.eventId) || !mentionedIds.has(entry.event.eventId),
    ));
    this.writeStorage(this.queued());
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
