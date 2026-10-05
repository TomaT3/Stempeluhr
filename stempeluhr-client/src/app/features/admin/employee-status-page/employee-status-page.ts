import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, DestroyRef, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';

import { AdminEmployeeStatus } from '../../../core/models/admin.models';
import { AdminApi } from '../../../core/services/admin-api';
import { AdminSession } from '../../../core/services/admin-session';
import { StatusBadge } from '../../../shared/components/status-badge/status-badge';
import { DurationPipe } from '../../../shared/pipes/duration-pipe';

@Component({
  selector: 'app-employee-status-page',
  imports: [DatePipe, DurationPipe, RouterLink, StatusBadge],
  templateUrl: './employee-status-page.html',
  styleUrl: './employee-status-page.scss',
})
export class EmployeeStatusPage {
  private readonly adminApi = inject(AdminApi);
  private readonly destroyRef = inject(DestroyRef);
  private readonly tick = signal(Date.now());
  private loadedAt = Date.now();
  private request: Subscription | undefined;

  readonly adminSession = inject(AdminSession);
  readonly adminPassword = signal(this.adminSession.password());
  readonly statuses = signal<AdminEmployeeStatus[]>([]);
  readonly isBusy = signal(false);
  readonly message = signal('');
  readonly hasLoaded = signal(false);

  readonly runningCount = computed(() => this.statuses().filter(status => status.isRunning).length);
  readonly availableCount = computed(() => this.statuses().filter(status => status.isAvailable).length);

  constructor() {
    const intervalId = window.setInterval(() => this.tick.set(Date.now()), 1000);
    this.destroyRef.onDestroy(() => {
      window.clearInterval(intervalId);
      this.request?.unsubscribe();
    });

    if (this.adminSession.isLoggedIn()) {
      this.loadStatuses();
    }
  }

  loadStatuses(): void {
    const password = this.adminPassword().trim();
    if (!password) {
      this.message.set('Bitte Admin-Passwort eingeben.');
      return;
    }

    this.isBusy.set(true);
    this.message.set('');
    this.request?.unsubscribe();
    this.request = this.adminApi.getEmployeeStatuses(password).subscribe({
      next: statuses => {
        this.adminSession.remember(password);
        this.loadedAt = Date.now();
        this.statuses.set(statuses);
        this.hasLoaded.set(true);
        this.isBusy.set(false);
      },
      error: (error: HttpErrorResponse) => {
        if (error.status === 401) {
          this.adminSession.clear();
          this.adminPassword.set('');
        }
        this.statuses.set([]);
        this.hasLoaded.set(false);
        this.message.set(this.errorMessage(error));
        this.isBusy.set(false);
      },
    });
  }

  logout(): void {
    // Eine noch laufende Antwort darf die Sitzung nicht wieder anlegen.
    this.request?.unsubscribe();
    this.adminSession.clear();
    this.adminPassword.set('');
    this.statuses.set([]);
    this.hasLoaded.set(false);
    this.message.set('');
    this.isBusy.set(false);
  }

  durationFor(status: AdminEmployeeStatus): number {
    if (!status.isRunning) {
      return status.durationSeconds;
    }

    const startedAt = this.parseStartedAt(status.startedAt);
    if (startedAt !== null) {
      return Math.max(0, Math.floor((this.tick() - startedAt) / 1000));
    }

    return Math.max(0, status.durationSeconds + Math.floor((this.tick() - this.loadedAt) / 1000));
  }

  private errorMessage(error: HttpErrorResponse): string {
    if (error.status === 401) {
      return 'Admin-Passwort stimmt nicht.';
    }

    if (error.status === 0 || error.status === 404) {
      return 'Backend nicht erreichbar.';
    }

    return `Status konnte nicht geladen werden (${error.status}).`;
  }

  private parseStartedAt(value: string | null): number | null {
    if (!value) {
      return null;
    }

    const parsed = Date.parse(value);
    return Number.isNaN(parsed) ? null : parsed;
  }
}
