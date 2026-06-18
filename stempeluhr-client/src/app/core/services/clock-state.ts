import { computed, Injectable, signal } from '@angular/core';

import { ClockStatus } from '../models/kiosk.models';

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

  readonly elapsed = computed(() => {
    const status = this.status();
    if (!status?.isRunning) {
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
