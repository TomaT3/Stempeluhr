import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';

import { AdminRejectedOfflineEvent } from '../../../core/models/admin.models';
import { AdminApi } from '../../../core/services/admin-api';

@Component({
  selector: 'app-rejected-offline-page',
  imports: [DatePipe, RouterLink],
  templateUrl: './rejected-offline-page.html',
  styleUrl: './rejected-offline-page.scss',
})
export class RejectedOfflinePage {
  private readonly adminApi = inject(AdminApi);
  readonly adminPassword = signal('');
  readonly entries = signal<AdminRejectedOfflineEvent[]>([]);
  readonly hasLoaded = signal(false);
  readonly isBusy = signal(false);
  readonly message = signal('');

  load(): void {
    const password = this.adminPassword().trim();
    if (!password) {
      this.message.set('Bitte Admin-Passwort eingeben.');
      return;
    }
    this.isBusy.set(true);
    this.message.set('');
    this.adminApi.getRejectedOfflineEvents(password).subscribe({
      next: entries => {
        this.entries.set(entries);
        this.hasLoaded.set(true);
        this.isBusy.set(false);
      },
      error: (error: HttpErrorResponse) => {
        this.hasLoaded.set(false);
        this.entries.set([]);
        this.message.set(error.status === 401 ? 'Admin-Passwort stimmt nicht.' : 'Einträge konnten nicht geladen werden.');
        this.isBusy.set(false);
      },
    });
  }

  resolve(entry: AdminRejectedOfflineEvent): void {
    this.isBusy.set(true);
    this.adminApi.resolveRejectedOfflineEvent(this.adminPassword().trim(), entry.eventId).subscribe({
      next: () => this.load(),
      error: () => {
        this.message.set('Eintrag konnte nicht als nachgetragen markiert werden.');
        this.isBusy.set(false);
      },
    });
  }

  actionLabel(action: string): string {
    return ({ start: 'Einstempeln', stop: 'Ausstempeln', pauseStart: 'Pausenbeginn',
      pauseEnd: 'Pausenende', switch: 'Tätigkeitswechsel' } as Record<string, string>)[action] ?? action;
  }
}
