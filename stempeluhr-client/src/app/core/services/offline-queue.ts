import { HttpClient } from '@angular/common/http';
import { inject, Injectable, computed, signal } from '@angular/core';
import { defer, finalize, firstValueFrom, Observable, of, Subject, timeout } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { confirmSyncedStatus } from './offline-cache';
import { LOCAL_NFC_SCAN_PORT } from './local-nfc-scan.service';

import {
  OfflineKioskClockEvent,
  OfflineSyncEventResult,
  OfflineSyncResult,
  RejectedOfflineStamp,
} from '../models/offline.models';

const TERMINAL_AUTH_STORAGE_KEY = 'stempeluhr.terminal-auth.v1';
const QUEUE_STORAGE_KEY = 'stempeluhr.offline-queue.v1';
/**
 * Stamps the server REFUSED during replay (wrong PIN, unknown employee,
 * revoked card). They are dropped from the queue - and without this record
 * nobody would ever learn that the time is missing. Kept separately from the
 * queue so it survives the drop and a kiosk reload.
 */
const REJECTED_STORAGE_KEY = 'stempeluhr.offline-rejected.v1';
const MAX_REJECTED_ENTRIES = 20;
const SYNC_RETRY_MS = 15_000;
// Slower cadence for events the server already accepted into its own outbox
// ("buffered"): they only need re-sending as a safety net against an API
// restart losing that in-memory outbox before it flushes.
const SYNC_RETRY_BUFFERED_MS = 60_000;
/**
 * Must mirror MaxSyncBatchSize in Stempeluhr.Api/Api/KioskEndpoints.cs: the API
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

const SYNC_ENDPOINT = '/api/kiosk/clock/sync';

/** Storage format; `kind` stays for compatibility with queues already on devices. */
interface StoredOfflineEvent {
  kind: 'kiosk';
  event: OfflineKioskClockEvent;
}

/**
 * Queues kiosk clock actions in localStorage while the backend (or the
 * internet) is unreachable and replays them once connectivity returns.
 */
@Injectable({ providedIn: 'root' })
export class OfflineQueueService {
  private readonly http = inject(HttpClient);
  private readonly agentPort = inject(LOCAL_NFC_SCAN_PORT);
  private readonly authenticatedTerminals = this.readAuthenticatedTerminals();
  private readonly queued = signal<StoredOfflineEvent[]>(this.readStorage());
  readonly pendingCount = this.queued.asReadonly();

  private readonly rejectedStamps = signal<RejectedOfflineStamp[]>(this.readRejectedStorage());
  /**
   * Stamps the server refused during replay. They are NOT in the queue any
   * more - the time is missing until somebody repairs it in Kimai, so the
   * kiosk has to say so (issue #34). Acknowledged entries stay in storage
   * (only hidden): pressing the button must not destroy the ONLY trace of a
   * stamp that has not been repaired yet.
   */
  readonly rejected = computed(() => this.rejectedStamps().filter(entry => !entry.acknowledgedAt));

  private readonly recoveredSubject = new Subject<void>();
  /**
   * Emits once per sync run that received at least one successful server
   * response. Hosts without the kiosk poll (the /clock default route has no
   * terminalId) use this as their only connectivity signal.
   */
  readonly recovered: Observable<void> = this.recoveredSubject.asObservable();

  private syncTimer: number | null = null;
  /** True while a flush run is in flight; overlapping syncNow() calls skip. */
  private syncing = false;
  private resyncRequested = false;

  constructor() {
    // Flush a queue left over from a previous browser session (e.g. after a
    // kiosk restart) as soon as the app starts - without waiting for the next
    // failed clock action to trigger the retry timer.
    if (this.queued().length > 0) {
      this.syncNow().subscribe();
    }
  }

  /** Called only after a successful authenticated catalog request. */
  enableTerminalAuth(terminalId: string): void {
    const id = terminalId.trim();
    if (!id || this.authenticatedTerminals.has(id)) return;
    this.authenticatedTerminals.add(id);
    try {
      window.localStorage.setItem(TERMINAL_AUTH_STORAGE_KEY, JSON.stringify([...this.authenticatedTerminals]));
    } catch { /* In-memory capability still works for this session. */ }
    this.queued.update(entries => entries.map(entry => entry.event.legacyTerminalId === id
      ? { ...entry, event: this.withTerminalAuth(entry.event, id) } : entry));
    this.writeStorage(this.queued());
    this.syncNow().subscribe();
  }

  authorizeEmployee(employeeId: string, pin: string): void {
    this.queued.update(entries => entries.map(entry => entry.event.employeeId === employeeId
      && !entry.event.terminalId ? { ...entry, event: { ...entry.event, pin } } : entry));
    this.syncNow().subscribe();
  }

  readonly needsPin = computed(() => this.queued().some(({ event }) => this.isMissingPin(event)));

  enqueueKiosk(event: OfflineKioskClockEvent): void {
    const id = event.terminalId?.trim();
    event = { ...event, needsPin: !!event.pin };
    if (id && this.authenticatedTerminals.has(id)) {
      event = this.withTerminalAuth(event, id);
    } else if (id) {
      event.legacyTerminalId = id;
      delete event.terminalId;
    }
    this.enqueue({ kind: 'kiosk', event });
  }

  private withTerminalAuth(event: OfflineKioskClockEvent, id: string): OfflineKioskClockEvent {
    const { pin, nfcCardId, legacyTerminalId, ...rest } = event;
    return { ...rest, terminalId: id, needsPin: false };
  }

  private isMissingPin(event: OfflineKioskClockEvent): boolean {
    return !event.terminalId && !!event.needsPin && !event.pin && !event.nfcCardId;
  }

  /** A missing credential defers only this employee and their later events. */
  private readyEvents(entries: StoredOfflineEvent[]): OfflineKioskClockEvent[] {
    const blocked = new Set<string>();
    return entries.map(entry => entry.event).filter(event => {
      const employee = event.employeeId.toLowerCase();
      if (this.isMissingPin(event)) blocked.add(employee);
      return !blocked.has(employee);
    });
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
      if (this.syncing && snapshot.length > 0) this.resyncRequested = true;
      if (snapshot.length === 0 || this.syncing) {
        // Empty queue: nothing to do. Overlapping call (constructor, retry
        // timer and connectivity poll can overlap): skip - the in-flight run drains
        // the same queue, and duplicate chunks are absorbed server-side by
        // _syncLock + idempotency. Guarding here avoids the wasteful
        // double-send.
        return of([] as OfflineSyncResult[]);
      }

      this.syncing = true;
      // flushQueue is async because the chunks must be sent SEQUENTIALLY:
      // each response decides whether the next chunk may go out at all.
      return defer(() => this.flushQueue(snapshot)).pipe(
        // Only the run that acquired the guard may release it.
        finalize(() => {
          this.syncing = false;
          if (this.resyncRequested) {
            this.resyncRequested = false;
            // Credentials/capability can arrive during a run whose snapshot
            // could not replay that employee yet. Do not lose that wakeup.
            if (this.readyEvents(this.queued()).length > 0)
              void Promise.resolve().then(() => this.syncNow().subscribe());
          }
        }),
        catchError(() => {
          this.scheduleRetry();
          return of([] as OfflineSyncResult[]);
        }),
      );
    });
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
    const events = this.readyEvents(snapshot);
    const results: OfflineSyncResult[] = [];
    const mentionedIds = new Set<string>();
    const bufferedIds = new Set<string>();
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

    for (let offset = 0; offset < events.length;) {
      const terminalId = events[offset].terminalId;
      const chunk: OfflineKioskClockEvent[] = [];
      for (const event of events.slice(offset, offset + MAX_SYNC_BATCH_SIZE)) {
        if (event.terminalId !== terminalId) break;
        chunk.push(event);
      }
      if (chunk.length === 0) { replayAborted = true; break; }
      offset += chunk.length;
      let result: OfflineSyncResult;
      try {
        result = await firstValueFrom(
          this.http.post<OfflineSyncResult>(terminalId
            ? `http://127.0.0.1:${this.agentPort}/terminal/sync` : SYNC_ENDPOINT, { events: chunk })
            .pipe(timeout(syncRequestTimeoutMs(chunk.length))),
        );
      } catch {
        // Network or 5xx failure mid-run: stop here. Events already resolved
        // by earlier chunks are dropped below, so partial progress survives;
        // the rest retries on the timer.
        replayAborted = true;
        break;
      }
      results.push(result);

      const chunkById = new Map(chunk.map(event => [event.eventId, event]));
      let chunkHasPending = false;
      for (const detail of result.results ?? []) {
        if (!detail.eventId) {
          continue;
        }

        mentionedIds.add(detail.eventId);
        if (detail.status === 'buffered') {
          bufferedIds.add(detail.eventId);
          chunkHasPending = true;
        } else {
          anyProcessed = true;
        }

        if (detail.status === 'rejected') {
          this.recordRejected(detail, chunkById.get(detail.eventId));
        } else if (detail.status === 'applied' && detail.state) {
          const event = chunkById.get(detail.eventId);
          // An earlier result must not confirm a later local projection.
          if (event && this.queued().filter(entry => entry.event.employeeId === event.employeeId)
              .at(-1)?.event.eventId === event.eventId) {
            confirmSyncedStatus(event.employeeId, detail.state);
          }
        }
      }

      // Even partial buffering (including legacy PIN containment) must not
      // let a later chunk overtake earlier events. Missing results also stay queued.
      if (chunkHasPending || chunk.some(event => !mentionedIds.has(event.eventId))) {
        break;
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
    if (this.readyEvents(this.queued()).length > 0) {
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

  /**
   * Keeps the record of a refused stamp so somebody can repair it in Kimai.
   * Best effort by design: the sync response is the only moment the server
   * tells us, and losing the record would only restore the silent loss.
   */
  private recordRejected(detail: OfflineSyncEventResult, kiosk: OfflineKioskClockEvent | undefined): void {
    const record: RejectedOfflineStamp = {
      eventId: detail.eventId,
      employeeId: kiosk?.employeeId ?? '',
      employeeName: kiosk?.employeeName ?? '',
      performedAt: kiosk?.performedAt ?? '',
      rejectedAt: new Date().toISOString(),
      message: detail.message ?? '',
      action: kiosk?.action ?? null,
    };

    this.rejectedStamps.update(entries => [...entries, record].slice(-MAX_REJECTED_ENTRIES));
    this.writeRejectedStorage(this.rejectedStamps());
  }

  /**
   * Marks the refused stamps as dealt with (the time was repaired in Kimai).
   * The records stay in storage - only their notice disappears: pressing the
   * button by mistake must not destroy the last trace of a missing booking.
   */
  acknowledgeRejected(ids: readonly string[]): void {
    const acknowledgedAt = new Date().toISOString();
    const visibleIds = new Set(ids);
    this.rejectedStamps.update(entries =>
      entries.map(entry => (entry.acknowledgedAt || !visibleIds.has(entry.eventId) ? entry : { ...entry, acknowledgedAt })),
    );
    this.writeRejectedStorage(this.rejectedStamps());
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

  private readAuthenticatedTerminals(): Set<string> {
    try {
      const ids = JSON.parse(window.localStorage.getItem(TERMINAL_AUTH_STORAGE_KEY) ?? '[]');
      return new Set(Array.isArray(ids) ? ids.filter(id => typeof id === 'string') : []);
    } catch { return new Set(); }
  }

  private readStorage(): StoredOfflineEvent[] {
    try {
      const raw = window.localStorage.getItem(QUEUE_STORAGE_KEY);
      const parsed = raw ? (JSON.parse(raw) as StoredOfflineEvent[]) : [];
      const terminalId = new URLSearchParams(window.location.search).get('terminalId')?.trim();
      const entries = Array.isArray(parsed) ? parsed.filter(entry => entry?.kind === 'kiosk').map(entry => {
        let event: OfflineKioskClockEvent = { ...entry.event, needsPin: entry.event.needsPin || !!entry.event.pin };
        const id = event.terminalId?.trim() || event.legacyTerminalId || terminalId;
        if (id && (event.terminalId || this.authenticatedTerminals.has(id))) {
          event = this.withTerminalAuth(event, id);
        } else if (id) {
          event.legacyTerminalId = id;
        }
        return { ...entry, event };
      }) : [];
      this.writeStorage(entries); // Strip PINs only for proven terminal auth or plain /clock.
      return entries;
    } catch {
      return [];
    }
  }

  private writeStorage(entries: StoredOfflineEvent[]): void {
    try {
      window.localStorage.setItem(QUEUE_STORAGE_KEY, JSON.stringify(entries.map(entry => {
        if (entry.event.legacyTerminalId) return entry;
        const { pin, ...event } = entry.event;
        return { ...entry, event };
      })));
    } catch {
      // Storage full/blocked: keep the in-memory queue so nothing is lost
      // during this browser session.
    }
  }

  private readRejectedStorage(): RejectedOfflineStamp[] {
    try {
      const raw = window.localStorage.getItem(REJECTED_STORAGE_KEY);
      const parsed = raw ? (JSON.parse(raw) as RejectedOfflineStamp[]) : [];
      return Array.isArray(parsed) ? parsed.filter(entry => typeof entry?.eventId === 'string') : [];
    } catch {
      return [];
    }
  }

  private writeRejectedStorage(entries: RejectedOfflineStamp[]): void {
    try {
      window.localStorage.setItem(REJECTED_STORAGE_KEY, JSON.stringify(entries));
    } catch {
      // Storage full/blocked: the in-memory record still shows the notice
      // until the kiosk is reloaded.
    }
  }
}
