import { HttpClient } from '@angular/common/http';
import { inject, Injectable, signal } from '@angular/core';
import { defer, finalize, firstValueFrom, Observable, of, Subject, timeout } from 'rxjs';
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
/**
 * Upper bound for a single flush REQUEST, coupled to the chunk size: the API
 * processes each event under its global _syncLock with 2-4 Kimai roundtrips
 * (status check + start/stop; pause actions up to 3-4 calls). Against a slow
 * Kimai (~300-500 ms/call) a full MAX_SYNC_BATCH_SIZE chunk therefore needs
 * well over 30 s - a fixed deadline would abort legitimate requests, and
 * every resend would re-take the server lock and repeat the per-event status
 * checks (correct thanks to idempotency, but a very slow drain under load).
 * Fixed overhead plus a budget per event covers that worst case while still
 * freeing the in-flight guard on a dead connection.
 */
const SYNC_REQUEST_TIMEOUT_BASE_MS = 10_000;
const SYNC_REQUEST_TIMEOUT_PER_EVENT_MS = 2_500;

/** Deadline for one sync request carrying chunkSize events. */
function syncRequestTimeoutMs(chunkSize: number): number {
  return SYNC_REQUEST_TIMEOUT_BASE_MS + chunkSize * SYNC_REQUEST_TIMEOUT_PER_EVENT_MS;
}

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
  /** True while a flush run is in flight; overlapping syncNow() calls skip. */
  private syncing = false;

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

  /**
   * Attempts to flush everything; returns an observable that completes when
   * done. Check AND arm of the in-flight guard live INSIDE the defer, i.e.
   * at SUBSCRIBE time and atomically in one place: a caller that stores the
   * observable and subscribes later (or subscribes twice) can never slip
   * between a free and a set guard - the second subscriber simply sees
   * syncing === true and skips. Callers must still subscribe exactly once
   * for a flush to happen at all.
   */
  syncNow(): Observable<OfflineSyncResult[]> {
    return defer(() => {
      const snapshot = this.queued();
      if (snapshot.length === 0 || this.syncing) {
        // Empty queue: nothing to do. Overlapping call (constructor, retry
        // timer and NFC poll can overlap): skip - the in-flight run drains
        // the same queue, and duplicate chunks are absorbed server-side by
        // _syncLock + idempotency. Guarding here avoids the wasteful
        // double-send.
        return of([] as OfflineSyncResult[]);
      }

      this.syncing = true;
      // flushQueue is async because the chunks must be sent SEQUENTIALLY:
      // each response decides whether the next chunk may go out at all.
      return this.flushQueue(snapshot);
    }).pipe(
      finalize(() => {
        this.syncing = false;
      }),
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
    // The backend "recovered" only means something for the host UI when at
    // least one event was actually PROCESSED (applied/duplicate/rejected) -
    // not when the whole batch was merely buffered (API up, Kimai down). In
    // the buffered-only case a PIN login is still impossible, so the terminal
    // must stay unlocked (no back()/reset) - see clock-workflow.
    let anyProcessed = false;
    // Set when a chunk dies on a transport error (network/timeout/5xx): the
    // run did NOT complete, so "recovered" must not fire even though earlier
    // chunks already processed events - PIN logins are still impossible.
    let replayAborted = false;

    replay:
    for (const [endpoint, events] of groups) {
      for (let offset = 0; offset < events.length; offset += MAX_SYNC_BATCH_SIZE) {
        if (bufferingEverything) {
          break replay;
        }

        try {
          const chunk = events.slice(offset, offset + MAX_SYNC_BATCH_SIZE);
          const result = await firstValueFrom(
            this.http.post<OfflineSyncResult>(endpoint, {
              events: chunk,
            }).pipe(timeout(syncRequestTimeoutMs(chunk.length))),
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
              anyProcessed = true;
            }
          }
          bufferingEverything = chunkFullyBuffered;
        } catch {
          // Network or 5xx failure mid-run: stop here entirely. Events already
          // resolved by earlier chunks are dropped below, so partial
          // progress survives; the rest retries on the timer. Breaking out of
          // BOTH loops (not just the chunk loop) keeps the remaining groups
          // unsent - the network is down, they would fail the same way.
          replayAborted = true;
          break replay;
        }
      }
    }

    this.dropResolved(bufferedIds, mentionedIds);

    // The backend answered and actually processed events AND the run finished
    // without a transport error - connectivity (and Kimai) are back. A
    // buffered-only run must NOT emit (the PIN login is still impossible and
    // hosts use this signal to release the terminal), and neither may an
    // ABORTED run whose earlier chunks processed events while a later chunk
    // hit a network/timeout failure.
    if (results.length > 0 && anyProcessed && !replayAborted) {
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
