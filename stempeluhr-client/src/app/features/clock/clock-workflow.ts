import { Directive, OnDestroy, inject, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';

import { Employee, NfcClockEvent } from '../../core/models/kiosk.models';
import { AudioFeedback } from '../../core/services/audio-feedback';
import { ClockState } from '../../core/services/clock-state';
import { KioskApi } from '../../core/services/kiosk-api';
import { OfflineQueueService } from '../../core/services/offline-queue';

const PIN_LENGTH = 4;

@Directive()
export abstract class ClockWorkflow implements OnDestroy {
  private readonly kioskApi = inject(KioskApi);
  private readonly audioFeedback = inject(AudioFeedback);
  private readonly route = inject(ActivatedRoute);
  protected readonly offlineQueue = inject(OfflineQueueService);
  readonly clockState = inject(ClockState);

  readonly selectedEmployee = signal<Employee | null>(null);
  readonly pin = signal('');
  readonly isUnlocked = signal(false);
  readonly isBusy = signal(false);
  readonly message = signal('');

  /** True while the backend cannot be reached; drives the offline banner. */
  readonly isOffline = signal(false);

  private resetTimer: number | null = null;
  private nfcPollTimer: number | null = null;
  private lastNfcEventId: string | null = null;
  private hasInitializedNfcPolling = false;
  /**
   * True while a connectivity loss (failed NFC poll OR an offline-queued
   * action whose request died) has not yet seen a follow-up successful
   * poll. The FIRST successful poll afterwards triggers exactly ONE
   * immediate queue flush. Tracked separately from isOffline on purpose:
   * the banner clears only after the sync really processed an event, so
   * poll success alone is no longer a state edge - keying the flush on
   * isOffline would re-send every second while the server keeps buffering
   * (API up, Kimai down).
   */
  private pendingRecoveryFlush = false;
  private nfcCardId: string | null = null;
  /** Unsubscribes the offline-queue recovery listener (see constructor). */
  private recoveryUnsubscribe: (() => void) | null = null;
  /**
   * Set when an offline-stamped action deliberately skipped the reset to
   * the idle screen (unlocking again needs a PIN login, which is impossible
   * offline). The reset then runs once connectivity has recovered.
   */
  private pendingResetOnRecovery = false;
  private readonly terminalId = this.readTerminalId();

  constructor() {
    // Hosts without a terminalId (the /clock default route) have no NFC
    // poll, so they never see the connection recover on their own. Use the
    // offline queue's own recovery signal to release a terminal that was
    // kept unlocked for offline stamping and clear the stale banner. The
    // signal fires ONLY when queued events were actually PROCESSED
    // (applied/duplicate/rejected) - a buffered-only flush (API up, Kimai
    // down) emits nothing, so the terminal stays unlocked while a PIN login
    // is still impossible.
    const recoveredSubscription = this.offlineQueue.recovered.subscribe(() => {
      this.isOffline.set(false);
      // Race guard against an in-flight ONLINE action: while a queue flush
      // runs (up to its chunk deadline), the employee can act again because
      // the API answers live. That action owns the screen and arms its OWN
      // teardown in every outcome (success and 4xx via scheduleReset(); the
      // 5xx branch re-queues and re-arms pendingResetOnRecovery itself). A
      // back() here would tear the session out from under the running
      // request: its late response would paint status/message onto the idle
      // screen, and its 2.2 s reset could wipe the next employee's fresh PIN
      // entry. Dropping the deferred reset is safe - the in-flight action
      // supersedes it.
      if (this.isBusy()) {
        this.pendingResetOnRecovery = false;
        return;
      }
      if (this.pendingResetOnRecovery) {
        this.pendingResetOnRecovery = false;
        this.back();
      }
    });
    this.recoveryUnsubscribe = () => recoveredSubscription.unsubscribe();

    if (!this.terminalId) {
      return;
    }

    this.pollNfcEvents();
    this.nfcPollTimer = window.setInterval(() => this.pollNfcEvents(), 1000);
  }

  pressDigit(digit: string): void {
    if (this.isBusy() || this.pin().length >= PIN_LENGTH) {
      return;
    }

    const nextPin = `${this.pin()}${digit}`;
    this.pin.set(nextPin);

    if (nextPin.length === PIN_LENGTH) {
      this.confirmPin();
    }
  }

  clearPin(): void {
    this.pin.set('');
    this.message.set('');
  }

  confirmPin(): void {
    if (!this.pin() || this.isBusy()) {
      return;
    }

    this.isBusy.set(true);
    this.message.set('');
    this.kioskApi.pinLogin(this.pin()).subscribe({
      next: session => {
        this.selectedEmployee.set(session.employee);
        this.clockState.setStatus(session.status);
        this.clockState.setEmployeeMode(true);
        this.isUnlocked.set(true);
        this.nfcCardId = null;
        this.message.set('');
        this.isBusy.set(false);
      },
      error: (err) => {
        const status = err?.status ?? 0;
        // Network/server errors mean the PIN could NOT be checked -
        // claiming "PIN nicht gefunden" would be wrong and would lock
        // colleagues out of the terminal for the rest of an outage.
        this.message.set(status === 0 || status >= 500
          ? 'Offline - PIN kann derzeit nicht geprueft werden.'
          : 'PIN nicht gefunden');
        this.pin.set('');
        this.isUnlocked.set(false);
        this.isBusy.set(false);
        this.audioFeedback.playBeeps(2);
      },
    });
  }

  start(): void {
    this.sendClockAction('start');
  }

  stop(): void {
    this.sendClockAction('stop');
  }

  startPause(): void {
    this.sendClockAction('pauseStart');
  }

  endPause(): void {
    this.sendClockAction('pauseEnd');
  }

  back(): void {
    if (this.resetTimer) {
      window.clearTimeout(this.resetTimer);
      this.resetTimer = null;
    }

    this.selectedEmployee.set(null);
    this.clockState.clear();
    this.clockState.setEmployeeMode(this.keepFocusedShellAfterReset());
    this.pin.set('');
    this.nfcCardId = null;
    this.isUnlocked.set(false);
    this.message.set('');
    this.isBusy.set(false);
    this.pendingResetOnRecovery = false;
  }

  ngOnDestroy(): void {
    if (this.resetTimer) {
      window.clearTimeout(this.resetTimer);
    }

    if (this.nfcPollTimer) {
      window.clearInterval(this.nfcPollTimer);
    }

    this.recoveryUnsubscribe?.();

    this.clockState.setEmployeeMode(false);
  }

  private pollNfcEvents(): void {
    if (this.isBusy() || !this.terminalId) {
      return;
    }

    this.kioskApi.latestNfcEvent(this.terminalId).subscribe({
      next: latest => {
        if (this.pendingRecoveryFlush) {
          // Connection just recovered: flush the offline queue ONCE immediately
          // instead of waiting for the retry timer. The deferred back() is NOT
          // done here - the recovered signal above decides, and it fires only
          // when events were actually PROCESSED. The banner clears on the SAME
          // proof: isOffline goes false only when the sync really processed at
          // least one event; a buffered-only flush (API up, Kimai down) keeps
          // it true because a PIN login is still impossible.
          this.pendingRecoveryFlush = false;
          this.offlineQueue.syncNow().subscribe(results => {
            if (results.some(result => result.results?.some(detail => detail.status !== 'buffered'))) {
              this.isOffline.set(false);
            }
          });
        }
        this.handleLatestNfcEvent(latest.event);
      },
      error: () => {
        this.hasInitializedNfcPolling = true;
        // Backend unreachable: keep polling (it will recover automatically).
        this.isOffline.set(true);
        this.pendingRecoveryFlush = true;
      },
    });
  }

  private handleLatestNfcEvent(event: NfcClockEvent | null): void {
    if (!this.hasInitializedNfcPolling) {
      this.lastNfcEventId = event?.eventId ?? null;
      this.hasInitializedNfcPolling = true;
      return;
    }

    if (!event || event.eventId === this.lastNfcEventId) {
      return;
    }

    this.lastNfcEventId = event.eventId;
    if (event.success && event.employee && event.status) {
      this.selectedEmployee.set(event.employee);
      this.clockState.setStatus(event.status);
      this.clockState.setEmployeeMode(true);
      this.isUnlocked.set(true);
      this.pin.set('');
      this.nfcCardId = event.cardId;
      this.message.set(event.message);
      this.audioFeedback.playBeeps(1);
      return;
    }

    this.selectedEmployee.set(null);
    this.clockState.clear();
    this.clockState.setEmployeeMode(this.keepFocusedShellAfterReset());
    this.isUnlocked.set(false);
    this.pin.set('');
    this.nfcCardId = null;
    this.message.set(event.message || 'NFC-Karte nicht erkannt');
    this.audioFeedback.playBeeps(2);
  }

  private sendClockAction(action: 'start' | 'stop' | 'pauseStart' | 'pauseEnd'): void {
    this.isBusy.set(true);
    // Capture the stamp time SYNCHRONOUSLY at button press: a hanging request
    // (kioskApi.clock has no timeout) reports its failure only seconds to
    // minutes later - the queued event must carry the moment the employee
    // acted (payroll data), not the late error time.
    const performedAt = new Date().toISOString();
    this.kioskApi.clock(this.selectedEmployee()?.id ?? '', this.pin(), action, this.nfcCardId).subscribe({
      next: status => {
        this.isOffline.set(false);
        this.clockState.setStatus(status);
        this.message.set(status.stateText);
        this.isBusy.set(false);
        this.audioFeedback.playBeeps(1);
        this.scheduleReset();
      },
      error: (err) => {
        const status = err?.status ?? 0;
        if (status === 0 || status >= 500) {
          // Backend/Kimai unreachable (network error or server failure): queue
          // the action with its real timestamp so it is replayed once
          // connectivity returns. 4xx responses are permanent (wrong PIN,
          // deleted employee, ...) - showing the error is better than queuing
          // an event that can never succeed.
          const employeeId = this.selectedEmployee()?.id ?? '';
          this.offlineQueue.enqueueKiosk({
            eventId: this.generateEventId(),
            employeeId,
            pin: this.pin(),
            action,
            performedAt,
            // Live-path parity: a session unlocked by NFC touch has NO pin -
            // the replay resolves the employee via the card instead.
            nfcCardId: this.nfcCardId,
          });
          this.isOffline.set(true);
          // Let the next successful NFC poll catch the queue up immediately.
          this.pendingRecoveryFlush = true;
          this.message.set(
            'Offline gespeichert - wird automatisch nachgetragen.',
          );
          this.audioFeedback.playBeeps(1);
          // Reset busy state - otherwise the terminal stays locked after the
          // first offline-stamped action (all buttons and the NFC poll check
          // isBusy()).
          this.isBusy.set(false);
          // Stay unlocked instead of resetting to the idle screen: coming
          // back requires a PIN login, and that is impossible while the
          // backend is unreachable - the terminal could then queue exactly
          // ONE stamp per outage. The current employee keeps stamping at
          // THIS terminal (the documented kiosk limitation anyway); once
          // connectivity recovers, the NFC poll runs the deferred back().
          this.pendingResetOnRecovery = true;
          return;
        }

        this.message.set('Kimai konnte nicht speichern');
        this.isBusy.set(false);
        this.audioFeedback.playBeeps(2);
        // Permanent error (wrong PIN, deleted employee, ...): return to the
        // idle screen like every other completed action instead of leaving
        // the message stuck on the terminal.
        this.scheduleReset();
      },
    });
  }

  private generateEventId(): string {
    if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
      return crypto.randomUUID().replaceAll('-', '');
    }

    return `${Date.now().toString(16)}${Math.floor(Math.random() * 0xffffffff).toString(16).padStart(8, '0')}`;
  }

  private scheduleReset(): void {
    if (this.resetTimer) {
      window.clearTimeout(this.resetTimer);
    }

    this.resetTimer = window.setTimeout(() => this.back(), 2200);
  }

  private readTerminalId(): string | null {
    const terminalId = this.route.snapshot.queryParamMap.get('terminalId')?.trim();
    return terminalId || null;
  }

  protected keepFocusedShellAfterReset(): boolean {
    return false;
  }
}
