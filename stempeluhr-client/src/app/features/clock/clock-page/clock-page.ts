import { DatePipe } from '@angular/common';
import { Component, OnDestroy, inject, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';

import { Employee, NfcClockEvent } from '../../../core/models/kiosk.models';
import { AudioFeedback } from '../../../core/services/audio-feedback';
import { ClockState } from '../../../core/services/clock-state';
import { KioskApi } from '../../../core/services/kiosk-api';
import { Avatar } from '../../../shared/components/avatar/avatar';
import { StatusBadge } from '../../../shared/components/status-badge/status-badge';
import { DurationPipe } from '../../../shared/pipes/duration-pipe';

const PIN_LENGTH = 4;

@Component({
  selector: 'app-clock-page',
  imports: [Avatar, DatePipe, DurationPipe, StatusBadge],
  templateUrl: './clock-page.html',
  styleUrl: './clock-page.scss',
})
export class ClockPage implements OnDestroy {
  private readonly kioskApi = inject(KioskApi);
  private readonly audioFeedback = inject(AudioFeedback);
  private readonly route = inject(ActivatedRoute);
  readonly clockState = inject(ClockState);

  readonly selectedEmployee = signal<Employee | null>(null);
  readonly pin = signal('');
  readonly isUnlocked = signal(false);
  readonly isBusy = signal(false);
  readonly message = signal('');

  private resetTimer: number | null = null;
  private nfcPollTimer: number | null = null;
  private lastNfcEventId: string | null = null;
  private hasInitializedNfcPolling = false;
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
    this.clockState.setEmployeeMode(false);
    this.pin.set('');
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
      next: latest => this.handleLatestNfcEvent(latest.event),
      error: () => {
        this.hasInitializedNfcPolling = true;
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
      this.message.set(event.message);
      this.audioFeedback.playBeeps(1);
      this.scheduleReset();
      return;
    }

    this.selectedEmployee.set(null);
    this.clockState.clear();
    this.clockState.setEmployeeMode(false);
    this.isUnlocked.set(false);
    this.pin.set('');
    this.message.set(event.message || 'NFC-Karte nicht erkannt');
    this.audioFeedback.playBeeps(2);
  }

  private sendClockAction(action: 'start' | 'stop' | 'pauseStart' | 'pauseEnd'): void {
    this.isBusy.set(true);
    this.kioskApi.clock(this.selectedEmployee()?.id ?? '', this.pin(), action).subscribe({
      next: status => {
        this.clockState.setStatus(status);
        this.message.set(status.stateText);
        this.isBusy.set(false);
        this.audioFeedback.playBeeps(1);
        this.scheduleReset();
      },
      error: () => {
        this.message.set('Kimai konnte nicht speichern');
        this.isBusy.set(false);
        this.audioFeedback.playBeeps(2);
        this.scheduleReset();
      },
    });
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
}
