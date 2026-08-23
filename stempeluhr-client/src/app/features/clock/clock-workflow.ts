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
  private nfcCardId: string | null = null;
  private readonly terminalId = this.readTerminalId();

  constructor() {
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
      error: () => {
        this.message.set('PIN nicht gefunden');
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
  }

  ngOnDestroy(): void {
    if (this.resetTimer) {
      window.clearTimeout(this.resetTimer);
    }

    if (this.nfcPollTimer) {
      window.clearInterval(this.nfcPollTimer);
    }

    this.clockState.setEmployeeMode(false);
  }

  private pollNfcEvents(): void {
    if (this.isBusy() || !this.terminalId) {
      return;
    }

    this.kioskApi.latestNfcEvent(this.terminalId).subscribe({
      next: latest => {
        if (this.isOffline()) {
          // Connection just recovered: flush the offline queue immediately
          // instead of waiting for the 15 s retry timer.
          this.offlineQueue.syncNow().subscribe();
        }
        this.isOffline.set(false);
        this.handleLatestNfcEvent(latest.event);
      },
      error: () => {
        this.hasInitializedNfcPolling = true;
        // Backend unreachable: keep polling (it will recover automatically).
        this.isOffline.set(true);
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
            performedAt: new Date().toISOString(),
          });
          this.isOffline.set(true);
          this.message.set(
            'Offline gespeichert - wird automatisch nachgetragen.',
          );
          this.audioFeedback.playBeeps(1);
          this.scheduleReset();
          return;
        }

        this.message.set('Kimai konnte nicht speichern');
        this.isBusy.set(false);
        this.audioFeedback.playBeeps(2);
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
