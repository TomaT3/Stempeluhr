import { Component, inject, signal } from '@angular/core';

import { Employee } from '../../../core/models/kiosk.models';
import { AudioFeedback } from '../../../core/services/audio-feedback';
import { ClockState } from '../../../core/services/clock-state';
import { KioskApi } from '../../../core/services/kiosk-api';
import { Avatar } from '../../../shared/components/avatar/avatar';
import { StatusBadge } from '../../../shared/components/status-badge/status-badge';

@Component({
  selector: 'app-clock-page',
  imports: [Avatar, StatusBadge],
  templateUrl: './clock-page.html',
  styleUrl: './clock-page.scss',
})
export class ClockPage {
  private readonly kioskApi = inject(KioskApi);
  private readonly audioFeedback = inject(AudioFeedback);
  readonly clockState = inject(ClockState);

  readonly selectedEmployee = signal<Employee | null>(null);
  readonly pin = signal('');
  readonly isUnlocked = signal(false);
  readonly isBusy = signal(false);
  readonly message = signal('');

  private resetTimer: number | null = null;

  pressDigit(digit: string): void {
    if (this.pin().length < 8) {
      this.pin.update(current => current + digit);
    }
  }

  clearPin(): void {
    this.pin.set('');
    this.message.set('');
  }

  confirmPin(): void {
    if (!this.pin()) {
      return;
    }

    this.isBusy.set(true);
    this.message.set('');
    this.kioskApi.pinLogin(this.pin()).subscribe({
      next: session => {
        this.selectedEmployee.set(session.employee);
        this.clockState.setStatus(session.status);
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

  back(): void {
    if (this.resetTimer) {
      window.clearTimeout(this.resetTimer);
      this.resetTimer = null;
    }

    this.selectedEmployee.set(null);
    this.clockState.clear();
    this.pin.set('');
    this.isUnlocked.set(false);
    this.message.set('');
  }

  private sendClockAction(action: 'start' | 'stop'): void {
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
}
