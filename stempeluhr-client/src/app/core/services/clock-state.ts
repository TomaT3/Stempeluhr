import { computed, Injectable, signal } from '@angular/core';

import { ClockAction, ClockStatus } from '../models/kiosk.models';

/**
 * State a locally queued OFFLINE action leads to. The wording mirrors the
 * server's own stateText values (ClockService) so the kiosk does not switch
 * vocabulary between online and offline.
 */
export function projectClockStatus(
  current: ClockStatus | null,
  action: ClockAction,
  performedAt: string,
): ClockStatus {
  switch (action) {
    case 'start':
    case 'pauseEnd':
      return {
        isRunning: true,
        activeTimesheetId: current?.activeTimesheetId ?? null,
        startedAt: performedAt,
        durationSeconds: 0,
        state: 'working',
        stateText: 'Eingestempelt',
      };
    case 'pauseStart':
      return {
        isRunning: true,
        activeTimesheetId: current?.activeTimesheetId ?? null,
        startedAt: performedAt,
        durationSeconds: 0,
        state: 'paused',
        stateText: 'In Pause',
      };
    case 'stop':
      return {
        isRunning: false,
        activeTimesheetId: null,
        startedAt: null,
        durationSeconds: 0,
        state: 'clockedOut',
        stateText: 'Ausgestempelt',
      };
  }
}

@Injectable({
  providedIn: 'root',
})
export class ClockState {
  private readonly tick = signal(Date.now());
  private readonly _employeeMode = signal(false);
  private loadedAt = Date.now();

  readonly status = signal<ClockStatus | null>(null);
  readonly employeeMode = this._employeeMode.asReadonly();
  readonly now = computed(() => new Date(this.tick()));
  readonly isWorking = computed(() => this.status()?.state === 'working');
  readonly isPaused = computed(() => this.status()?.state === 'paused');
  readonly isClockedOut = computed(() => !this.status() || this.status()?.state === 'clockedOut');

  readonly elapsed = computed(() => {
    const status = this.status();
    if (!status?.isRunning || status.state === 'clockedOut') {
      return status?.durationSeconds ?? 0;
    }

    const startedAt = this.parseStartedAt(status.startedAt);
    if (startedAt !== null) {
      return Math.max(0, Math.floor((this.tick() - startedAt) / 1000));
    }

    return Math.max(0, status.durationSeconds + Math.floor((this.tick() - this.loadedAt) / 1000));
  });

  constructor() {
    window.setInterval(() => this.tick.set(Date.now()), 1000);
  }

  setStatus(status: ClockStatus): void {
    this.loadedAt = Date.now();
    this.status.set(status);
  }

  clear(): void {
    this.status.set(null);
  }

  setEmployeeMode(isEmployeeMode: boolean): void {
    this._employeeMode.set(isEmployeeMode);
  }

  private parseStartedAt(value: string | null): number | null {
    if (!value) {
      return null;
    }

    const parsed = Date.parse(value);
    return Number.isNaN(parsed) ? null : parsed;
  }
}
