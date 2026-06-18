import { computed, Injectable, signal } from '@angular/core';

import { ClockStatus } from '../models/kiosk.models';

@Injectable({
  providedIn: 'root',
})
export class ClockState {
  private readonly tick = signal(Date.now());
  private loadedAt = Date.now();

  readonly status = signal<ClockStatus | null>(null);

  readonly elapsed = computed(() => {
    const status = this.status();
    if (!status?.isRunning) {
      return status?.durationSeconds ?? 0;
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
}
