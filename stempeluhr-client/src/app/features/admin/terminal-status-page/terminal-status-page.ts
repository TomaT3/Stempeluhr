import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, DestroyRef, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';

import { AdminTerminalReport, AdminTerminalState, AdminTerminalStatus } from '../../../core/models/admin.models';
import { AdminApi } from '../../../core/services/admin-api';

const StateLabels: Record<AdminTerminalState, string> = {
  online: 'Online',
  problem: 'Problem',
  unreachable: 'Nicht erreichbar',
  never: 'Noch nie gemeldet',
};

const PageLabels: Record<NonNullable<AdminTerminalReport['uiStatus']>, string> = {
  alive: 'läuft',
  missing: 'hängt',
  'not-seen': 'noch kein Lebenszeichen',
};

const BlockedLabels: Record<NonNullable<AdminTerminalReport['blocked']>, string> = {
  none: 'nein',
  request: 'Anfrage läuft',
  backlog: 'Nachtrag läuft',
  status: 'Status wird geladen',
};

@Component({
  selector: 'app-terminal-status-page',
  imports: [DatePipe, RouterLink],
  templateUrl: './terminal-status-page.html',
  styleUrl: './terminal-status-page.scss',
})
export class TerminalStatusPage {
  /** Der Agent meldet jede Minute; öfter laden bringt nichts Neues. */
  static readonly RefreshIntervalMs = 30_000;

  private readonly adminApi = inject(AdminApi);
  private readonly tick = signal(Date.now());
  private refreshId: number | undefined;

  readonly adminPassword = signal('');
  readonly statuses = signal<AdminTerminalStatus[]>([]);
  readonly isBusy = signal(false);
  readonly message = signal('');
  readonly hasLoaded = signal(false);

  readonly onlineCount = computed(() => this.statuses().filter(status => status.state === 'online').length);
  readonly troubleCount = computed(() => this.statuses().length - this.onlineCount());

  constructor() {
    const tickId = window.setInterval(() => this.tick.set(Date.now()), 1000);
    inject(DestroyRef).onDestroy(() => {
      window.clearInterval(tickId);
      window.clearInterval(this.refreshId);
    });
  }

  loadStatuses(): void {
    const password = this.adminPassword().trim();
    if (!password) {
      this.message.set('Bitte Admin-Passwort eingeben.');
      return;
    }

    this.isBusy.set(true);
    this.message.set('');
    this.adminApi.getTerminalStatuses(password).subscribe({
      next: statuses => {
        this.statuses.set(statuses);
        this.hasLoaded.set(true);
        this.isBusy.set(false);
        this.refreshId ??= window.setInterval(() => this.refresh(), TerminalStatusPage.RefreshIntervalMs);
      },
      error: (error: HttpErrorResponse) => {
        // A short backend outage keeps the last list and the refresh running.
        if (error.status === 401 || !this.hasLoaded()) {
          window.clearInterval(this.refreshId);
          this.refreshId = undefined;
          this.statuses.set([]);
          this.hasLoaded.set(false);
        }
        this.message.set(this.errorMessage(error));
        this.isBusy.set(false);
      },
    });
  }

  stateLabel(status: AdminTerminalStatus): string {
    return StateLabels[status.state];
  }

  pageLabel(report: AdminTerminalReport): string {
    return report.uiStatus ? PageLabels[report.uiStatus] : '-';
  }

  blockedLabel(report: AdminTerminalReport): string {
    return report.blocked ? BlockedLabels[report.blocked] : '-';
  }

  lastReportAge(status: AdminTerminalStatus): string {
    const reportedAt = status.lastReportAt ? Date.parse(status.lastReportAt) : NaN;
    if (Number.isNaN(reportedAt)) {
      return 'nie';
    }

    return `vor ${this.duration(Math.max(0, Math.floor((this.tick() - reportedAt) / 1000)))}`;
  }

  uptime(report: AdminTerminalReport): string {
    return report.uptimeSeconds === null ? '-' : this.duration(report.uptimeSeconds);
  }

  megabytes(kilobytes: number | null): string {
    return kilobytes === null ? '-' : `${Math.round(kilobytes / 1024)} MB`;
  }

  value(value: number | null, unit: string): string {
    return value === null ? '-' : `${Math.round(value)} ${unit}`;
  }

  private refresh(): void {
    if (!this.isBusy()) {
      this.loadStatuses();
    }
  }

  private duration(seconds: number): string {
    if (seconds < 60) {
      return `${seconds} s`;
    }

    const minutes = Math.floor(seconds / 60);
    if (minutes < 60) {
      return `${minutes} min`;
    }

    const hours = Math.floor(minutes / 60);
    return hours < 48 ? `${hours} h ${minutes % 60} min` : `${Math.floor(hours / 24)} Tage`;
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
}
