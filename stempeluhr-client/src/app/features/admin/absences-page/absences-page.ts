import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Observable, Subscription, catchError, forkJoin, map, of, switchMap } from 'rxjs';

import {
  AbsenceKind,
  AbsenceStatus,
  AdminAbsence,
  AdminSettings,
  AdminVacationBalance,
} from '../../../core/models/admin.models';
import { AdminApi } from '../../../core/services/admin-api';
import { AdminSession } from '../../../core/services/admin-session';
import {
  BlockedDay,
  DayPickerCalendar,
  addDays,
  addMonths,
  localToday,
  weekday,
} from '../../../shared/components/day-picker-calendar/day-picker-calendar';

export type AbsenceTab = 'requests' | 'create' | 'balances';
export type AbsenceFilter = 'open' | 'all';
type CreateStep = 'edit' | 'summary' | 'result';

/** Entwurf einer Zeile im Urlaubskonto; Zahlen als Text, damit „1,“ beim Tippen bleibt. */
interface BalanceDraft {
  carryOver: string;
  correction: string;
  note: string;
}

/** Rest eines Jahres vor und nach der Erfassung. */
export interface RemainderLine {
  year: number;
  before: number;
  after: number;
}

export const AbsenceKindLabels: Record<AbsenceKind, string> = {
  vacation: 'Urlaub',
  sick: 'Krank',
  flexDay: 'Gleittag',
};

const StatusLabels: Record<AbsenceStatus, string> = {
  pending: 'Offen',
  applied: 'In Kimai eingetragen',
  rejected: 'Abgelehnt',
  failed: 'Fehlgeschlagen',
  withdrawn: 'Zurückgezogen',
  resolvedManually: 'Manuell erledigt',
  cancelled: 'Storniert',
};

/** Belegt ein Antrag seine Tage (wie im Backend: offen oder eingetragen)? */
const BlockingStatuses: AbsenceStatus[] = ['pending', 'failed', 'applied', 'resolvedManually'];

/** Zeitfenster der Admin-Erfassung (wie AbsenceValidator.Window mit isAdmin). */
export function adminWindow(kind: AbsenceKind, today: string): { min: string; max: string } {
  const max = kind === 'vacation' ? addMonths(today, 18) : kind === 'sick' ? addDays(today, 14) : addMonths(today, 12);
  return { min: addDays(today, -90), max };
}

/**
 * Tage kompakt, z. B. „Mo 03.–Fr 07.11., Di 11.11.“. Aufeinanderfolgende
 * Tage werden zu Bereichen; ein anderes Jahr als `currentYear` steht dabei.
 */
export function compactDays(days: string[], currentYear: number): string {
  const sorted = [...days].sort();
  const ranges: [string, string][] = [];
  for (const day of sorted) {
    const last = ranges.at(-1);
    if (last && addDays(last[1], 1) === day) {
      last[1] = day;
    } else {
      ranges.push([day, day]);
    }
  }

  const format = (day: string, withMonth: boolean, withYear: boolean) =>
    `${weekday(day)} ${day.slice(8, 10)}.${withMonth ? `${day.slice(5, 7)}.` : ''}${withYear ? day.slice(0, 4) : ''}`;
  return ranges.map(([first, last]) => {
    const otherYear = (day: string) => Number(day.slice(0, 4)) !== currentYear;
    if (first === last) {
      return format(first, true, otherYear(first));
    }
    const sameYear = first.slice(0, 4) === last.slice(0, 4);
    const sameMonth = first.slice(0, 7) === last.slice(0, 7);
    return `${format(first, !sameMonth, !sameYear)}–${format(last, true, !sameYear || otherYear(last))}`;
  }).join(', ');
}

/** Tage als „1 Tag“ bzw. „2,5 Tage“. */
export function dayCount(value: number): string {
  const text = value.toLocaleString('de-DE', { maximumFractionDigits: 2 });
  return `${text} ${value === 1 ? 'Tag' : 'Tage'}`;
}

@Component({
  selector: 'app-absences-page',
  imports: [DatePipe, DayPickerCalendar, RouterLink],
  templateUrl: './absences-page.html',
  styleUrl: './absences-page.scss',
})
export class AbsencesPage {
  static readonly MaxNoteLength = 300;

  private readonly adminApi = inject(AdminApi);
  private request: Subscription | undefined;
  readonly adminSession = inject(AdminSession);
  readonly adminPassword = signal(this.adminSession.password());
  readonly maxNoteLength = AbsencesPage.MaxNoteLength;
  readonly kinds: AbsenceKind[] = ['vacation', 'sick', 'flexDay'];
  readonly today = localToday();
  readonly currentYear = Number(this.today.slice(0, 4));

  readonly tab = signal<AbsenceTab>('requests');
  readonly settings = signal<AdminSettings | null>(null);
  readonly hasLoaded = signal(false);
  readonly isBusy = signal(false);
  readonly message = signal('');

  // ---- Anträge
  readonly filter = signal<AbsenceFilter>('open');
  readonly year = signal<number | null>(null);
  readonly entries = signal<AdminAbsence[]>([]);
  /** Urlaubskonten je Jahr und Mitarbeiter, für den Rest an den Karten und die Zusammenfassung. */
  readonly balancesByYear = signal<Record<number, Record<string, AdminVacationBalance>>>({});
  readonly rejectingId = signal<string | null>(null);
  readonly rejectNote = signal('');

  // ---- Erfassen
  readonly createEmployeeId = signal('');
  readonly createKind = signal<AbsenceKind>('vacation');
  readonly createDays = signal<string[]>([]);
  readonly createComment = signal('');
  readonly createStep = signal<CreateStep>('edit');
  readonly createResult = signal<AdminAbsence | null>(null);
  /** Alle Anträge, für die belegten Tage im Kalender. */
  readonly allAbsences = signal<AdminAbsence[]>([]);

  // ---- Urlaubskonten
  readonly balanceYear = signal(this.currentYear);
  readonly balances = signal<AdminVacationBalance[]>([]);
  readonly drafts = signal<Record<string, BalanceDraft>>({});

  readonly yearOptions = [this.currentYear - 2, this.currentYear - 1, this.currentYear, this.currentYear + 1];

  readonly absencesConfigured = computed(() => {
    const settings = this.settings();
    return !settings || (settings.absenceProjectId != null && settings.vacationActivityId != null
      && settings.sickActivityId != null && settings.flexDayActivityId != null);
  });

  /** Aktive Mitarbeiter mit Kimai-Token: nur für sie kann die API buchen. */
  readonly bookableEmployees = computed(() =>
    (this.settings()?.employees ?? []).filter(employee => employee.isEnabled && employee.hasApiToken));

  readonly createWindow = computed(() => adminWindow(this.createKind(), this.today));

  readonly blockedDays = computed<Record<string, BlockedDay>>(() => {
    const employeeId = this.createEmployeeId();
    const blocked: Record<string, BlockedDay> = {};
    for (const absence of this.allAbsences()) {
      if (absence.employeeId !== employeeId || !BlockingStatuses.includes(absence.status)) {
        continue;
      }
      const label = `${AbsenceKindLabels[absence.kind]} (${absence.status === 'pending' ? 'offen' : StatusLabels[absence.status]})`;
      for (const day of absence.days) {
        blocked[day] = { label, tone: absence.kind };
      }
    }
    return blocked;
  });

  readonly createEmployeeName = computed(() =>
    this.bookableEmployees().find(employee => employee.id === this.createEmployeeId())?.displayName ?? '');

  /** Rest je Jahr vor und nach dem Erfassen (nur Urlaub). */
  readonly remainders = computed<RemainderLine[]>(() => {
    if (this.createKind() !== 'vacation') {
      return [];
    }
    const perYear = new Map<number, number>();
    for (const day of this.createDays()) {
      const year = Number(day.slice(0, 4));
      perYear.set(year, (perYear.get(year) ?? 0) + 1);
    }
    return [...perYear].sort(([a], [b]) => a - b).flatMap(([year, count]) => {
      const balance = this.balancesByYear()[year]?.[this.createEmployeeId()];
      return balance ? [{ year, before: balance.balance.remaining, after: balance.balance.remaining - count }] : [];
    });
  });

  readonly overRemainder = computed(() => this.remainders().some(line => line.after < 0));

  constructor() {
    inject(DestroyRef).onDestroy(() => this.request?.unsubscribe());
    if (this.adminSession.isLoggedIn()) {
      this.load();
    }
  }

  load(resultMessage = ''): void {
    const password = this.adminPassword().trim();
    if (!password) {
      this.message.set('Bitte Admin-Passwort eingeben.');
      return;
    }

    const settings$ = this.adminApi.getSettings(password);
    switch (this.tab()) {
      case 'create':
        this.run(password, forkJoin({ settings: settings$, all: this.adminApi.getAbsences(password, 'all') }), ({ settings, all }) => {
          this.settings.set(settings);
          this.allAbsences.set(all);
          if (!this.bookableEmployees().some(employee => employee.id === this.createEmployeeId())) {
            this.createEmployeeId.set(this.bookableEmployees()[0]?.id ?? '');
          }
        }, resultMessage);
        return;
      case 'balances': {
        const year = this.balanceYear();
        this.run(password, forkJoin({
          settings: settings$,
          balances: this.adminApi.getVacationBalances(password, year),
          previous: this.adminApi.getVacationBalances(password, year - 1),
        }), ({ settings, balances, previous }) => {
          this.settings.set(settings);
          this.balances.set(balances);
          this.drafts.set({});
          this.cacheBalances(year, balances);
          this.cacheBalances(year - 1, previous);
        }, resultMessage);
        return;
      }
      default:
        this.run(password, forkJoin({ settings: settings$, entries: this.listWithBalances(password) }), ({ settings, entries }) => {
          this.settings.set(settings);
          this.entries.set(entries);
        }, resultMessage);
    }
  }

  setTab(tab: AbsenceTab): void {
    if (this.tab() === tab || this.isBusy()) {
      return;
    }
    this.tab.set(tab);
    this.message.set('');
    if (this.hasLoaded()) {
      this.load();
    }
  }

  logout(): void {
    this.request?.unsubscribe();
    this.adminSession.clear();
    this.adminPassword.set('');
    this.reset();
  }

  // ------------------------------------------------------------ Anträge

  setFilter(filter: AbsenceFilter): void {
    if (this.filter() !== filter && !this.isBusy()) {
      this.filter.set(filter);
      this.load();
    }
  }

  setYear(value: string): void {
    if (!this.isBusy()) {
      this.year.set(value ? Number(value) : null);
      this.load();
    }
  }

  approve(entry: AdminAbsence): void {
    this.decide(password => this.adminApi.approveAbsence(password, entry.id));
  }

  startReject(entry: AdminAbsence): void {
    this.rejectingId.set(entry.id);
    this.rejectNote.set('');
  }

  cancelReject(): void {
    this.rejectingId.set(null);
    this.rejectNote.set('');
  }

  sendReject(entry: AdminAbsence): void {
    const note = this.rejectNote().trim();
    this.decide(password => this.adminApi.rejectAbsence(password, entry.id, note));
  }

  retry(entry: AdminAbsence): void {
    this.decide(password => this.adminApi.retryAbsence(password, entry.id));
  }

  resolve(entry: AdminAbsence): void {
    this.decide(password => this.adminApi.resolveAbsence(password, entry.id));
  }

  cancelAbsence(entry: AdminAbsence): void {
    const count = entry.appliedDays.length;
    const question = `${entry.employeeName}: ${AbsenceKindLabels[entry.kind]} ${compactDays(entry.days, this.currentYear)} stornieren? `
      + `Das löscht ${count === 1 ? '1 Eintrag' : `${count} Einträge`} in Kimai.`;
    if (window.confirm(question)) {
      this.decide(password => this.adminApi.cancelAbsence(password, entry.id));
    }
  }

  kindLabel(kind: AbsenceKind): string {
    return AbsenceKindLabels[kind] ?? kind;
  }

  statusLabel(status: AbsenceStatus): string {
    return StatusLabels[status] ?? status;
  }

  sourceLabel(source: string): string {
    return source === 'admin' ? 'Admin' : source === 'clock' ? '/clock' : `Terminal ${source}`;
  }

  days(days: string[]): string {
    return compactDays(days, this.currentYear);
  }

  count(value: number): string {
    return dayCount(value);
  }

  /** Pending, aber das Eintragen hat schon begonnen (Neustart mittendrin). */
  isInterrupted(entry: AdminAbsence): boolean {
    return entry.status === 'pending' && entry.applyStartedAt !== null;
  }

  /** Rest der Urlaubsjahre eines Antrags; beantragte Tage sind darin schon abgezogen. */
  remainderOf(entry: AdminAbsence): { year: number; remaining: number }[] {
    if (entry.kind !== 'vacation' || !BlockingStatuses.includes(entry.status)) {
      return [];
    }
    const years = [...new Set(entry.days.map(day => Number(day.slice(0, 4))))].sort();
    return years.flatMap(year => {
      const balance = this.balancesByYear()[year]?.[entry.employeeId];
      return balance ? [{ year, remaining: balance.balance.remaining }] : [];
    });
  }

  // ------------------------------------------------------------ Erfassen

  selectEmployee(employeeId: string): void {
    this.createEmployeeId.set(employeeId);
    this.createDays.set([]);
  }

  selectKind(kind: AbsenceKind): void {
    this.createKind.set(kind);
    // Tage außerhalb des neuen Zeitfensters fallen heraus.
    const { min, max } = adminWindow(kind, this.today);
    this.createDays.update(days => days.filter(day => day >= min && day <= max));
  }

  /** Zur Zusammenfassung; bei Urlaub mit den Urlaubskonten der betroffenen Jahre. */
  showSummary(): void {
    if (!this.createEmployeeId() || this.createDays().length === 0) {
      return;
    }

    const password = this.adminPassword().trim();
    const years = this.createKind() === 'vacation'
      ? [...new Set(this.createDays().map(day => Number(day.slice(0, 4))))]
      : [];
    if (years.length === 0) {
      this.createStep.set('summary');
      return;
    }

    this.run(password, forkJoin(years.map(year => this.adminApi.getVacationBalances(password, year).pipe(map(balances => ({ year, balances }))))),
      results => {
        for (const { year, balances } of results) {
          this.cacheBalances(year, balances);
        }
        this.createStep.set('summary');
      });
  }

  backToEdit(): void {
    this.createStep.set('edit');
  }

  submitCreate(): void {
    const password = this.adminPassword().trim();
    const call = this.adminApi.createAbsence(
      password, this.createEmployeeId(), this.createKind(), this.createDays(), this.createComment().trim());
    this.run(password, call.pipe(
      // Danach die belegten Tage neu laden; ein Fehler dabei verdeckt das Ergebnis nicht.
      switchMap(result => this.adminApi.getAbsences(password, 'all').pipe(
        map(all => ({ result, all })),
        catchError(() => of({ result, all: this.allAbsences() })),
      )),
    ), ({ result, all }) => {
      this.allAbsences.set(all);
      this.createResult.set(result);
      this.createStep.set('result');
    });
  }

  createAnother(): void {
    this.createDays.set([]);
    this.createComment.set('');
    this.createResult.set(null);
    this.createStep.set('edit');
  }

  // ------------------------------------------------------------ Urlaubskonten

  setBalanceYear(value: string): void {
    if (!this.isBusy()) {
      this.balanceYear.set(Number(value));
      this.load();
    }
  }

  draft(row: AdminVacationBalance): BalanceDraft {
    return this.drafts()[row.employeeId] ?? {
      carryOver: String(row.balance.carryOver),
      correction: String(row.balance.correction),
      note: row.note ?? '',
    };
  }

  updateDraft(row: AdminVacationBalance, patch: Partial<BalanceDraft>): void {
    this.drafts.update(drafts => ({ ...drafts, [row.employeeId]: { ...this.draft(row), ...patch } }));
  }

  isDirty(row: AdminVacationBalance): boolean {
    return row.employeeId in this.drafts();
  }

  /** Rest des Vorjahres als Vorschlag für den Übertrag (nur bei positivem Rest). */
  previousRemainder(row: AdminVacationBalance): number | null {
    const previous = this.balancesByYear()[this.balanceYear() - 1]?.[row.employeeId];
    return previous && previous.balance.remaining > 0 ? previous.balance.remaining : null;
  }

  takePreviousRemainder(row: AdminVacationBalance): void {
    const remainder = this.previousRemainder(row);
    if (remainder !== null) {
      this.updateDraft(row, { carryOver: String(remainder) });
    }
  }

  draftInvalid(row: AdminVacationBalance): boolean {
    const draft = this.draft(row);
    return this.parseNumber(draft.carryOver) === null || this.parseNumber(draft.correction) === null;
  }

  saveBalance(row: AdminVacationBalance): void {
    const draft = this.draft(row);
    const carryOver = this.parseNumber(draft.carryOver);
    const correction = this.parseNumber(draft.correction);
    if (carryOver === null || correction === null) {
      this.message.set('Übertrag und Korrektur müssen Zahlen sein.');
      return;
    }

    const password = this.adminPassword().trim();
    const year = this.balanceYear();
    this.run(password, this.adminApi.updateVacationYear(password, row.employeeId, year, carryOver, correction, draft.note.trim()),
      saved => {
        this.balances.update(rows => rows.map(candidate => candidate.employeeId === saved.employeeId ? saved : candidate));
        this.cacheBalances(year, this.balances());
        this.drafts.update(drafts => {
          const { [row.employeeId]: _, ...rest } = drafts;
          return rest;
        });
        this.message.set(`${saved.employeeName}: Urlaubskonto ${year} gespeichert.`);
      });
  }

  /** Fehlende Stammdaten, die den Anspruch bestimmen. */
  missingMasterData(row: AdminVacationBalance): string[] {
    return [
      ...(row.vacationDaysPerYear === null ? ['Urlaubstage pro Jahr fehlen'] : []),
      ...(row.entryDate === null ? ['Eintritt fehlt'] : []),
    ];
  }

  fullMonthsLabel(row: AdminVacationBalance): string {
    return `${row.fullMonths} ${row.fullMonths === 1 ? 'voller Monat' : 'volle Monate'}`;
  }

  // ------------------------------------------------------------ Hilfen

  private parseNumber(text: string): number | null {
    const value = Number(text.trim().replace(',', '.'));
    return text.trim() !== '' && Number.isFinite(value) ? value : null;
  }

  private cacheBalances(year: number, balances: AdminVacationBalance[]): void {
    this.balancesByYear.update(cache => ({
      ...cache,
      [year]: Object.fromEntries(balances.map(balance => [balance.employeeId, balance])),
    }));
  }

  /** Anträge samt Urlaubskonten der Jahre, in denen Urlaubstage liegen. */
  private listWithBalances(password: string): Observable<AdminAbsence[]> {
    return this.adminApi.getAbsences(password, this.filter(), this.year()).pipe(
      switchMap(entries => {
        const years = [...new Set(entries
          .filter(entry => entry.kind === 'vacation')
          .flatMap(entry => entry.days.map(day => Number(day.slice(0, 4)))))];
        if (years.length === 0) {
          return of(entries);
        }
        // Nur ein Zusatz: ohne Konten bleibt die Liste nutzbar.
        return forkJoin(years.map(year => this.adminApi.getVacationBalances(password, year).pipe(
          map(balances => this.cacheBalances(year, balances)),
          catchError(() => of(undefined)),
        ))).pipe(map(() => entries));
      }),
    );
  }

  /** Eine Entscheidung zur Zeit; danach die Liste mit dem neuen Stand. */
  private decide(call: (password: string) => Observable<AdminAbsence>): void {
    if (this.isBusy()) {
      return;
    }
    const password = this.adminPassword().trim();
    this.run(password, call(password).pipe(
      switchMap(result => this.listWithBalances(password).pipe(map(entries => ({ result, entries })))),
    ), ({ result, entries }) => {
      this.cancelReject();
      this.entries.set(entries);
      this.message.set(this.resultMessage(result));
    }, '', () => {
      // 404/409: der Antrag hat sich inzwischen geändert - den neuen Stand zeigen.
      this.cancelReject();
      this.load(this.message());
    });
  }

  /**
   * Führt einen Aufruf aus: hält die Seite währenddessen gesperrt, merkt sich
   * das Passwort beim Erfolg und meldet bei 401 ab, wie auf der
   * Korrekturen-Seite.
   */
  private run<T>(
    password: string,
    source: Observable<T>,
    next: (value: T) => void,
    resultMessage = '',
    afterError?: () => void,
  ): void {
    this.isBusy.set(true);
    this.message.set('');
    this.request?.unsubscribe();
    this.request = source.subscribe({
      next: value => {
        this.adminSession.remember(password);
        this.hasLoaded.set(true);
        this.isBusy.set(false);
        this.message.set(resultMessage);
        next(value);
      },
      error: (error: HttpErrorResponse) => {
        this.isBusy.set(false);
        if (error.status === 401) {
          this.adminSession.clear();
          this.adminPassword.set('');
          this.reset();
          this.message.set('Admin-Passwort stimmt nicht.');
          return;
        }
        this.message.set(this.errorMessage(error));
        if (afterError && this.hasLoaded()) {
          afterError();
        }
      },
    });
  }

  private reset(): void {
    this.hasLoaded.set(false);
    this.settings.set(null);
    this.entries.set([]);
    this.allAbsences.set([]);
    this.balances.set([]);
    this.balancesByYear.set({});
    this.drafts.set({});
    this.cancelReject();
    this.createDays.set([]);
    this.createStep.set('edit');
    this.createResult.set(null);
    this.message.set('');
    this.isBusy.set(false);
  }

  private resultMessage(result: AdminAbsence): string {
    const name = result.employeeName || result.employeeId;
    switch (result.status) {
      case 'applied':
        return result.error ? `${name}: ${result.error}` : `${name}: In Kimai eingetragen.`;
      case 'rejected':
        return `${name}: Antrag abgelehnt.`;
      case 'resolvedManually':
        return `${name}: Als manuell erledigt markiert.`;
      case 'cancelled':
        return `${name}: Storniert, die Einträge in Kimai sind gelöscht.`;
      case 'withdrawn':
        return `${name}: Der Antrag wurde inzwischen zurückgezogen.`;
      case 'failed':
        return `${name}: Nicht in Kimai eingetragen – ${result.error ?? 'unbekannter Fehler'}`;
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
}
