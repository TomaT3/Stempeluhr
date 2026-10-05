import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';

import { AdminRejectedOfflineEvent } from '../../../core/models/admin.models';
import { AdminApi } from '../../../core/services/admin-api';
import { AdminSession } from '../../../core/services/admin-session';

@Component({
  selector: 'app-rejected-offline-page',
  imports: [DatePipe, RouterLink],
  templateUrl: './rejected-offline-page.html',
  styleUrl: './rejected-offline-page.scss',
})
export class RejectedOfflinePage {
  private readonly adminApi = inject(AdminApi);
  private request: Subscription | undefined;
  readonly adminSession = inject(AdminSession);
  readonly adminPassword = signal(this.adminSession.password());
  readonly entries = signal<AdminRejectedOfflineEvent[]>([]);
  readonly hasLoaded = signal(false);
  readonly isBusy = signal(false);
  readonly message = signal('');

  constructor() {
    inject(DestroyRef).onDestroy(() => this.request?.unsubscribe());
    if (this.adminSession.isLoggedIn()) {
      this.load();
    }
  }

  load(): void {
    const password = this.adminPassword().trim();
    if (!password) {
      this.message.set('Bitte Admin-Passwort eingeben.');
      return;
    }
    this.isBusy.set(true);
    this.message.set('');
    this.request?.unsubscribe();
    this.request = this.adminApi.getRejectedOfflineEvents(password).subscribe({
      next: entries => {
        this.adminSession.remember(password);
        this.entries.set(entries);
        this.hasLoaded.set(true);
        this.isBusy.set(false);
      },
      error: (error: HttpErrorResponse) => {
        this.hasLoaded.set(false);
        this.entries.set([]);
        this.forgetWrongPassword(error);
        this.message.set(error.status === 401 ? 'Admin-Passwort stimmt nicht.' : 'Einträge konnten nicht geladen werden.');
        this.isBusy.set(false);
      },
    });
  }

  resolve(entry: AdminRejectedOfflineEvent): void {
    this.isBusy.set(true);
    this.request?.unsubscribe();
    this.request = this.adminApi.resolveRejectedOfflineEvent(this.adminPassword().trim(), entry.eventId).subscribe({
      next: () => this.load(),
      error: (error: HttpErrorResponse) => {
        if (this.forgetWrongPassword(error)) {
          this.hasLoaded.set(false);
          this.entries.set([]);
        }
        this.message.set(error.status === 401
          ? 'Admin-Passwort stimmt nicht.'
          : 'Eintrag konnte nicht als nachgetragen markiert werden.');
        this.isBusy.set(false);
      },
    });
  }

  logout(): void {
    // Eine noch laufende Antwort darf die Sitzung nicht wieder anlegen.
    this.request?.unsubscribe();
    this.adminSession.clear();
    this.adminPassword.set('');
    this.entries.set([]);
    this.hasLoaded.set(false);
    this.message.set('');
    this.isBusy.set(false);
  }

  actionLabel(action: string): string {
    return ({ start: 'Einstempeln', stop: 'Ausstempeln', pauseStart: 'Pausenbeginn',
      pauseEnd: 'Pausenende', switch: 'Tätigkeitswechsel' } as Record<string, string>)[action] ?? action;
  }

  private forgetWrongPassword(error: HttpErrorResponse): boolean {
    if (error.status !== 401) {
      return false;
    }

    this.adminSession.clear();
    this.adminPassword.set('');
    return true;
  }
}
