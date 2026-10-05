import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Observable, Subscription } from 'rxjs';

import { AdminTimeCorrection, TimeCorrectionKind, TimeCorrectionStatus } from '../../../core/models/admin.models';
import { AdminApi } from '../../../core/services/admin-api';
import { AdminSession } from '../../../core/services/admin-session';

export type CorrectionFilter = 'open' | 'all';

/** Ein Eintrag in „Vorher“ oder „Nachher“, z. B. „Pause 12:00–12:30“. */
export interface CorrectionLine {
  label: string;
  range: string;
}

const KindLabels: Record<TimeCorrectionKind, string> = {
  addPause: 'Pause nachtragen',
  setEnd: 'Ausstempeln nachtragen',
  addShift: 'Schicht nachtragen',
  changeTimes: 'Beginn/Ende ändern',
};

const StatusLabels: Record<TimeCorrectionStatus, string> = {
  pending: 'Offen',
  applied: 'In Kimai eingetragen',
  rejected: 'Abgelehnt',
  failed: 'Fehlgeschlagen',
  withdrawn: 'Zurückgezogen',
  resolvedManually: 'Manuell erledigt',
};

const StepLabels: Record<string, string> = {
  shorten: 'Eintrag gekürzt',
  end: 'Ende geändert',
  times: 'Zeiten geändert',
  pause: 'Pause angelegt',
  rest: 'Rest-Arbeit angelegt',
  work: 'Arbeit angelegt',
  work1: 'Arbeit vor der Pause angelegt',
  work2: 'Arbeit nach der Pause angelegt',
};

const Weekdays = ['So', 'Mo', 'Di', 'Mi', 'Do', 'Fr', 'Sa'];

@Component({
  selector: 'app-corrections-page',
  imports: [DatePipe, RouterLink],
  templateUrl: './corrections-page.html',
  styleUrl: './corrections-page.scss',
})
export class CorrectionsPage {
  static readonly MaxNoteLength = 300;

  private readonly adminApi = inject(AdminApi);
  private readonly browserTimeZone = Intl.DateTimeFormat().resolvedOptions().timeZone;
  private request: Subscription | undefined;
  readonly adminSession = inject(AdminSession);
  readonly adminPassword = signal(this.adminSession.password());
  readonly filter = signal<CorrectionFilter>('open');
  readonly entries = signal<AdminTimeCorrection[]>([]);
  readonly hasLoaded = signal(false);
  readonly isBusy = signal(false);
  readonly message = signal('');
  /** Antrag, für den gerade das Grund-Feld der Ablehnung offen ist. */
  readonly rejectingId = signal<string | null>(null);
  readonly rejectNote = signal('');
  readonly maxNoteLength = CorrectionsPage.MaxNoteLength;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.request?.unsubscribe());
    if (this.adminSession.isLoggedIn()) {
      this.load();
    }
  }

  load(): void {
    if (this.isBusy()) {
      return;
    }

    const password = this.adminPassword().trim();
    if (!password) {
      this.message.set('Bitte Admin-Passwort eingeben.');
      return;
    }
    this.message.set('');
    this.fetch(password);
  }

  setFilter(filter: CorrectionFilter): void {
    if (this.filter() === filter || this.isBusy()) {
      return;
    }
    this.filter.set(filter);
    if (this.hasLoaded()) {
      this.load();
    }
  }

  approve(entry: AdminTimeCorrection): void {
    this.decide(password => this.adminApi.approveCorrection(password, entry.id));
  }

  startReject(entry: AdminTimeCorrection): void {
    this.rejectingId.set(entry.id);
    this.rejectNote.set('');
  }

  cancelReject(): void {
    this.rejectingId.set(null);
    this.rejectNote.set('');
  }

  sendReject(entry: AdminTimeCorrection): void {
    const note = this.rejectNote().trim();
    this.decide(password => this.adminApi.rejectCorrection(password, entry.id, note));
  }

  retry(entry: AdminTimeCorrection): void {
    this.decide(password => this.adminApi.retryCorrection(password, entry.id));
  }

  resolve(entry: AdminTimeCorrection): void {
    this.decide(password => this.adminApi.resolveCorrection(password, entry.id));
  }

  logout(): void {
    // Eine noch laufende Antwort darf die Sitzung nicht wieder anlegen.
    this.request?.unsubscribe();
    this.adminSession.clear();
    this.adminPassword.set('');
    this.entries.set([]);
    this.hasLoaded.set(false);
    this.message.set('');
    this.cancelReject();
    this.isBusy.set(false);
  }

  kindLabel(kind: TimeCorrectionKind): string {
    return KindLabels[kind] ?? kind;
  }

  statusLabel(status: TimeCorrectionStatus): string {
    return StatusLabels[status] ?? status;
  }

  sourceLabel(source: string): string {
    return source === 'clock' ? '/clock' : `Terminal ${source}`;
  }

  /** Zeitraum der Schicht bzw. des betroffenen Eintrags. */
  shiftRange(entry: AdminTimeCorrection): string {
    const begin = entry.original?.begin ?? entry.begin;
    const end = entry.original ? entry.original.end : entry.end;
    return begin ? this.range(begin, end) : '–';
  }

  /** Die Zeiten sind in der Kimai-Zeitzone; ein Hinweis nur, wenn das Gerät woanders steht. */
  foreignTimeZone(entry: AdminTimeCorrection): string | null {
    return entry.timeZone && entry.timeZone !== this.browserTimeZone ? entry.timeZone : null;
  }

  /** Schritte, die Kimai bei einem gescheiterten Antrag schon ausgeführt hat. */
  appliedSteps(entry: AdminTimeCorrection): string {
    return entry.appliedSteps.map(step => StepLabels[step] ?? step).join(', ');
  }

  before(entry: AdminTimeCorrection): CorrectionLine[] {
    const original = entry.original;
    return original ? [{ label: original.label, range: this.range(original.begin, original.end) }] : [];
  }

  after(entry: AdminTimeCorrection): CorrectionLine[] {
    const original = entry.original;
    switch (entry.kind) {
      case 'addPause':
        if (!original || !entry.pauseBegin || !entry.pauseEnd) {
          return [];
        }
        return this.split(original.label, original.begin, original.end, entry.pauseBegin, entry.pauseEnd);
      // Auch für Pausen-Einträge: die Bezeichnung kommt aus dem Original.
      case 'setEnd':
        return original ? [{ label: original.label, range: this.range(original.begin, entry.end ?? original.end) }] : [];
      case 'changeTimes':
        return original
          ? [{ label: original.label, range: this.range(entry.begin ?? original.begin, entry.end ?? original.end) }]
          : [];
      case 'addShift': {
        if (!entry.begin) {
          return [];
        }
        const label = entry.taskLabel ?? 'Arbeit';
        return entry.pauseBegin && entry.pauseEnd
          ? this.split(label, entry.begin, entry.end, entry.pauseBegin, entry.pauseEnd)
          : [{ label, range: this.range(entry.begin, entry.end) }];
      }
    }
    return [];
  }

  private split(label: string, begin: string, end: string | null, pauseBegin: string, pauseEnd: string): CorrectionLine[] {
    const lines = [
      { label, range: this.range(begin, pauseBegin) },
      { label: 'Pause', range: this.range(pauseBegin, pauseEnd) },
    ];
    // Endet die Pause am alten Ende, entfällt die Rest-Arbeit.
    if (end !== pauseEnd) {
      lines.push({ label, range: this.range(pauseEnd, end) });
    }
    return lines;
  }

  /** `yyyy-MM-ddTHH:mm` ohne Umrechnung, am selben Tag nur einmal mit Datum. */
  private range(begin: string, end: string | null): string {
    const from = this.dateTime(begin);
    if (!end) {
      return `${from} – läuft`;
    }
    return end.slice(0, 10) === begin.slice(0, 10)
      ? `${from}–${end.slice(11, 16)}`
      : `${from} – ${this.dateTime(end)}`;
  }

  private dateTime(value: string): string {
    const [year, month, day] = value.slice(0, 10).split('-').map(Number);
    const weekday = Weekdays[new Date(Date.UTC(year, month - 1, day)).getUTCDay()] ?? '';
    return `${weekday} ${value.slice(8, 10)}.${value.slice(5, 7)}.${value.slice(0, 4)} ${value.slice(11, 16)}`;
  }

  /** Eine Entscheidung zur Zeit: Doppelklicks lösen keinen zweiten Request aus. */
  private decide(call: (password: string) => Observable<AdminTimeCorrection>): void {
    if (this.isBusy()) {
      return;
    }

    const password = this.adminPassword().trim();
    this.isBusy.set(true);
    this.message.set('');
    this.request?.unsubscribe();
    this.request = call(password).subscribe({
      next: result => {
        this.cancelReject();
        this.fetch(password, this.resultMessage(result));
      },
      error: (error: HttpErrorResponse) => {
        if (this.forgetWrongPassword(error)) {
          this.hasLoaded.set(false);
          this.entries.set([]);
          this.cancelReject();
          this.message.set('Admin-Passwort stimmt nicht.');
          this.isBusy.set(false);
          return;
        }
        // 404/409: Der Antrag hat sich inzwischen geändert - den neuen Stand zeigen.
        this.fetch(password, this.errorMessage(error));
      },
    });
  }

  private fetch(password: string, resultMessage = ''): void {
    this.isBusy.set(true);
    this.request?.unsubscribe();
    this.request = this.adminApi.getCorrections(password, this.filter()).subscribe({
      next: entries => {
        this.adminSession.remember(password);
        this.entries.set(entries);
        this.hasLoaded.set(true);
        this.message.set(resultMessage);
        this.isBusy.set(false);
      },
      error: (error: HttpErrorResponse) => {
        this.hasLoaded.set(false);
        this.entries.set([]);
        this.forgetWrongPassword(error);
        this.message.set(error.status === 401
          ? 'Admin-Passwort stimmt nicht.'
          : [resultMessage, 'Anträge konnten nicht geladen werden.'].filter(Boolean).join(' '));
        this.isBusy.set(false);
      },
    });
  }

  private resultMessage(result: AdminTimeCorrection): string {
    const name = result.employeeName || result.employeeId;
    switch (result.status) {
      case 'applied':
        return `${name}: In Kimai eingetragen.`;
      case 'rejected':
        return `${name}: Antrag abgelehnt.`;
      case 'resolvedManually':
        return `${name}: Als manuell erledigt markiert.`;
      case 'withdrawn':
        return `${name}: Der Antrag wurde inzwischen zurückgezogen.`;
      case 'failed': {
        // Failed heißt nicht "nichts gebucht": Schritte vor dem Fehler sind schon in Kimai.
        const reason = result.error ?? 'unbekannter Fehler';
        return result.appliedSteps.length
          ? `${name}: Nicht vollständig in Kimai gebucht – ${reason}. Schon in Kimai: ${this.appliedSteps(result)}.`
          : `${name}: Nicht in Kimai gebucht – ${reason}`;
      }
      default:
        return `${name}: Antrag ist noch offen.`;
    }
  }

  private errorMessage(error: HttpErrorResponse): string {
    const serverMessage = typeof error.error?.message === 'string' ? error.error.message : '';
    if (serverMessage) {
      return serverMessage;
    }
    return error.status === 0 ? 'Server nicht erreichbar.' : 'Aktion fehlgeschlagen.';
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
