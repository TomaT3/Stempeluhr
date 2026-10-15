import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, ElementRef, OnInit, computed, effect, inject, input, output, signal, viewChild } from '@angular/core';
import { Observable, Subscription } from 'rxjs';

import {
  Absence,
  AbsenceKind,
  AbsenceStatus,
  KioskAbsenceOverview,
  SubmitAbsence,
  VacationBalance,
} from '../../../core/models/absence.models';
import { CorrectionAuth } from '../../../core/models/kiosk.models';
import { KioskApi } from '../../../core/services/kiosk-api';
import { AbsenceKindLabels, compactDays, dayCount, dayNumber } from '../../../shared/absence-format';
import { BlockedDay, DayPickerCalendar } from '../../../shared/components/day-picker-calendar/day-picker-calendar';
import { CORRECTION_IDLE_MS, MAX_COMMENT_LENGTH } from '../correction-flow/correction-flow';

/** Ohne Tipp geht der Kiosk zurück in den Ruhezustand (wie im Korrekturablauf). */
export const ABSENCE_IDLE_MS = CORRECTION_IDLE_MS;

/**
 * Nach jedem Schrittwechsel liegt unter dem Finger ein anderer Knopf („Weiter“
 * wird „Absenden“, „Zurückziehen“ wird „Ja, zurückziehen“). So lange werden
 * Tipps verworfen - ein prellender Doppeltipp löst nichts aus (Issue #59).
 */
export const ABSENCE_TAP_GUARD_MS = 400;

type Step = 'kind' | 'days' | 'summary' | 'sent' | 'mine';

/** Status aus Sicht des Mitarbeiters (die Admin-Seite sagt z. B. „Manuell erledigt“). */
export const ABSENCE_STATUS_LABELS: Record<AbsenceStatus, string> = {
  pending: 'Wartet auf Freigabe',
  applied: 'Eingetragen',
  rejected: 'Abgelehnt',
  failed: 'Fehlgeschlagen',
  withdrawn: 'Zurückgezogen',
  resolvedManually: 'Vom Chef eingetragen',
  cancelled: 'Storniert',
};

/** Rest eines Urlaubsjahres vor und nach den gewählten Tagen. */
export interface RemainderLine {
  year: number;
  before: number;
  after: number;
}

const BlockedLabels: Partial<Record<AbsenceStatus, string>> = {
  pending: 'beantragt',
  failed: 'beantragt',
  applied: 'eingetragen',
  resolvedManually: 'eingetragen',
};

/**
 * Abwesenheit beantragen (Epic #132): Art wählen, Tage im Kalender antippen,
 * Zusammenfassung, absenden - dazu Urlaubskonto und „Meine Abwesenheiten“.
 * Gemeinsam für Kiosk und /clock; die Seite gibt nur Anmeldung und Layout
 * vor. Keine Offline-Queue: jeder Schritt fragt die API, ein Ausfall wird
 * gemeldet statt gepuffert. Ein Antrag bucht nichts - erst die Freigabe
 * trägt die Tage in Kimai ein.
 *
 * Die Komponente lebt genau so lange wie die Sitzung: ein Identitätswechsel,
 * „X“ oder der Abbruch zerstören sie samt laufender Anfragen, eine verspätete
 * Antwort erreicht also nie die Anzeige eines anderen Mitarbeiters.
 */
@Component({
  selector: 'app-absence-flow',
  imports: [DayPickerCalendar],
  templateUrl: './absence-flow.html',
  styleUrls: ['../correction-flow/correction-flow.scss', './absence-flow.scss'],
  host: {
    '[class.layout-terminal]': "layout() === 'terminal'",
    '[class.layout-clock]': "layout() === 'clock'",
    '[class.tap-guard]': 'tapGuard()',
    '(pointerdown)': 'touch()',
    '(keydown)': 'touch()',
  },
})
export class AbsenceFlow implements OnInit {
  private readonly kioskApi = inject(KioskApi);
  private readonly destroyRef = inject(DestroyRef);

  /** Angemeldeter Mitarbeiter: ID plus PIN oder Karte (Karten-Session: `pin` leer). */
  readonly auth = input.required<CorrectionAuth>();
  /** Das Terminal hat keine Tastatur und kein Kommentarfeld; /clock schon. */
  readonly layout = input<'terminal' | 'clock'>('terminal');
  /** Terminal-ID oder `clock`. */
  readonly source = input.required<string>();

  /** Zurück in die Sitzung (Mitarbeiter bleibt angemeldet). */
  readonly closed = output<void>();
  /** 2 min ohne Tipp: die Seite meldet ab. */
  readonly timedOut = output<void>();

  protected readonly kindLabels = AbsenceKindLabels;
  protected readonly statusLabels = ABSENCE_STATUS_LABELS;
  protected readonly maxCommentLength = MAX_COMMENT_LENGTH;
  protected readonly dayNumber = dayNumber;
  protected readonly dayCount = dayCount;
  protected readonly kinds: AbsenceKind[] = ['vacation', 'sick', 'flexDay'];

  readonly step = signal<Step>('kind');
  readonly isBusy = signal(false);
  readonly error = signal('');
  readonly overview = signal<KioskAbsenceOverview | null>(null);
  readonly kind = signal<AbsenceKind | null>(null);
  readonly days = signal<string[]>([]);
  readonly comment = signal('');
  readonly mine = signal<Absence[] | null>(null);
  /** Rückmeldung unter „Meine Abwesenheiten“ (z. B. nach dem Zurückziehen). */
  readonly mineNotice = signal('');
  /** Antrag, für den „Wirklich zurückziehen?“ offen ist. */
  readonly confirmingId = signal<string | null>(null);
  readonly withdrawingId = signal<string | null>(null);
  /** Läuft nach jedem Schrittwechsel ABSENCE_TAP_GUARD_MS; Tipps bleiben solange wirkungslos. */
  readonly tapGuard = signal(false);

  private readonly heading = viewChild<ElementRef<HTMLElement>>('heading');

  private history: Step[] = [];
  private request: Subscription | null = null;
  /** Stilles Nachladen des Kontos nach Absenden und Zurückziehen. */
  private refresh: Subscription | null = null;
  /** Zählt jede Anfrage und jedes Verlassen eines Schritts: nur die jüngste darf anzeigen. */
  private requestToken = 0;
  private idleTimer: number | null = null;
  private guardTimer: number | null = null;

  protected readonly title = computed(() => {
    switch (this.step()) {
      case 'kind':
      case 'sent':
        return 'Abwesenheit';
      case 'days':
        // Die Art steht im Titel, die Zeile darunter zählt die Tage: so passen
        // am Terminal auch sechs Kalenderwochen ohne Scrollen.
        return `${AbsenceKindLabels[this.kind() ?? 'vacation']}: Tage wählen`;
      case 'summary':
        return 'Zusammenfassung';
      case 'mine':
        return 'Meine Abwesenheiten';
    }
  });

  protected readonly subtitle = computed(() => {
    const kind = this.kind();
    return kind && this.step() === 'summary' ? AbsenceKindLabels[kind] : '';
  });

  /** Jahr von „heute“ in der Kimai-Zeitzone; bestimmt Konto und die Jahreszahl in Tageslisten. */
  protected readonly currentYear = computed(() => Number((this.overview()?.today ?? '').slice(0, 4)) || new Date().getFullYear());

  /** Konto des laufenden Jahres, nur wenn Urlaub für den Mitarbeiter eingerichtet ist. */
  protected readonly balance = computed<VacationBalance | null>(() => {
    const overview = this.overview();
    if (!overview || this.missing('vacation')) {
      return null;
    }
    return overview.balances.find(balance => balance.year === this.currentYear()) ?? null;
  });

  protected readonly window = computed(() => {
    const kind = this.kind();
    const window = this.overview()?.kinds.find(candidate => candidate.kind === kind);
    return { min: window?.earliest ?? null, max: window?.latest ?? null };
  });

  /** Belegte Tage (offen oder eingetragen, jede Art) sind im Kalender markiert und nicht wählbar. */
  protected readonly blocked = computed<Record<string, BlockedDay>>(() => {
    const blocked: Record<string, BlockedDay> = {};
    for (const day of this.overview()?.days ?? []) {
      blocked[day.day] = {
        label: `${AbsenceKindLabels[day.kind]} (${BlockedLabels[day.status] ?? ABSENCE_STATUS_LABELS[day.status]})`,
        tone: day.kind,
      };
    }
    return blocked;
  });

  /** Rest je Jahr vor und nach den gewählten Tagen (nur Urlaub). */
  protected readonly remainders = computed<RemainderLine[]>(() => {
    if (this.kind() !== 'vacation') {
      return [];
    }
    const perYear = new Map<number, number>();
    for (const day of this.days()) {
      const year = Number(day.slice(0, 4));
      perYear.set(year, (perYear.get(year) ?? 0) + 1);
    }
    const balances = this.overview()?.balances ?? [];
    return [...perYear].sort(([a], [b]) => a - b).flatMap(([year, count]) => {
      const balance = balances.find(candidate => candidate.year === year);
      return balance ? [{ year, before: balance.remaining, after: balance.remaining - count }] : [];
    });
  });

  /** Der Server weist Urlaub über dem Rest ab; „Weiter“ geht dann gar nicht erst. */
  protected readonly overRemainder = computed(() => this.remainders().some(line => line.after < 0));

  /** „3 Tage gewählt · Rest danach 17“ über dem Kalender. */
  protected readonly selectionLine = computed(() => {
    const count = this.days().length;
    if (count === 0) {
      return 'Bitte Tage antippen';
    }
    const remainders = this.remainders();
    const rest = remainders.length === 1
      ? ` · Rest danach ${dayNumber(remainders[0].after)}`
      : remainders.map(line => ` · Rest ${line.year}: ${dayNumber(line.after)}`).join('');
    return `${dayCount(count)} gewählt${rest}`;
  });

  protected readonly compactSelection = computed(() => compactDays(this.days(), this.currentYear()));

  /** Konto nach dem Absenden (still nachgeladen), für die Bestätigung bei Urlaub. */
  protected readonly sentRemainder = computed(() =>
    this.kind() === 'vacation' ? this.balance()?.remaining ?? null : null);

  constructor() {
    // Nach jedem Schritt steht der Fokus auf der Überschrift: ein Knopf, der
    // gerade verschwindet, ließe ihn sonst im Nichts zurück.
    effect(() => {
      this.step();
      this.heading()?.nativeElement.focus({ preventScroll: true });
    });
  }

  ngOnInit(): void {
    this.touch();
    this.guardTaps();
    this.destroyRef.onDestroy(() => {
      this.request?.unsubscribe();
      this.refresh?.unsubscribe();
      if (this.idleTimer !== null) {
        window.clearTimeout(this.idleTimer);
      }
      if (this.guardTimer !== null) {
        window.clearTimeout(this.guardTimer);
      }
    });
    this.runRequest(
      this.kioskApi.absenceOverview(this.auth()),
      overview => this.overview.set(overview),
      error => this.error.set(this.describe(error, 'list')),
    );
  }

  /** Jeder Tipp im Ablauf verschiebt das Inaktivitäts-Ende. */
  touch(): void {
    if (this.idleTimer !== null) {
      window.clearTimeout(this.idleTimer);
    }
    this.idleTimer = window.setTimeout(() => this.timedOut.emit(), ABSENCE_IDLE_MS);
  }

  // ------------------------------------------------------------ Schritte

  chooseKind(kind: AbsenceKind): void {
    this.touch();
    if (this.isBusy() || this.tapGuard()) {
      return;
    }
    this.error.set('');
    // Ohne Konto (Ladefehler) fehlen Fenster und belegte Tage: erst nachladen.
    if (!this.overview()) {
      this.runRequest(
        this.kioskApi.absenceOverview(this.auth()),
        overview => {
          this.overview.set(overview);
          this.openDays(kind);
        },
        error => this.error.set(this.describe(error, 'list')),
      );
      return;
    }
    this.openDays(kind);
  }

  /**
   * Der Kalender hält seine Auswahl selbst; die Tipp-Sperre greift dort per
   * `pointer-events` (`.tap-guard`), damit Anzeige und Auswahl gleich bleiben.
   */
  setDays(days: string[]): void {
    this.touch();
    this.days.set(days);
  }

  next(): void {
    this.touch();
    if (this.step() !== 'days' || this.isBusy() || this.tapGuard() || this.days().length === 0 || this.overRemainder()) {
      return;
    }
    this.error.set('');
    this.go('summary');
  }

  back(): void {
    this.touch();
    if (this.tapGuard()) {
      return;
    }
    // Eine laufende Anfrage gehört zum Schritt, den der Mitarbeiter verlässt.
    this.requestToken++;
    this.request?.unsubscribe();
    this.request = null;
    this.isBusy.set(false);
    this.withdrawingId.set(null);
    this.confirmingId.set(null);
    this.error.set('');
    this.mineNotice.set('');
    const previous = this.history.pop();
    if (!previous) {
      this.closed.emit();
      return;
    }
    this.step.set(previous);
    this.guardTaps();
  }

  done(): void {
    this.touch();
    if (!this.tapGuard()) {
      this.closed.emit();
    }
  }

  submit(): void {
    this.touch();
    const kind = this.kind();
    if (!kind || this.step() !== 'summary' || this.isBusy() || this.tapGuard() || this.days().length === 0) {
      return;
    }
    const body: SubmitAbsence = {
      ...this.auth(),
      kind,
      days: this.days(),
      // Das Terminal hat keine Tastatur: der Kommentar gehört nur zu /clock.
      comment: this.layout() === 'clock' ? (this.comment().trim().slice(0, MAX_COMMENT_LENGTH) || null) : null,
      source: this.source(),
    };
    this.error.set('');
    this.runRequest(
      this.kioskApi.submitAbsence(body),
      () => {
        // Der Antrag ist raus: Rücksprung gibt es nicht mehr.
        this.history = [];
        this.go('sent', false);
        this.reloadOverview();
      },
      // 400: Servermeldung, der Mitarbeiter bleibt in der Zusammenfassung und kann zurück und ändern.
      error => this.error.set(this.describe(error, 'submit')),
    );
  }

  setComment(event: Event): void {
    this.touch();
    this.comment.set((event.target as HTMLTextAreaElement).value.slice(0, MAX_COMMENT_LENGTH));
  }

  openMine(): void {
    this.touch();
    if (this.isBusy() || this.tapGuard()) {
      return;
    }
    this.error.set('');
    this.mineNotice.set('');
    this.mine.set(null);
    this.runRequest(
      this.kioskApi.myAbsences(this.auth()),
      list => {
        this.mine.set(list);
        this.go('mine');
      },
      error => this.error.set(this.describe(error, 'list')),
    );
  }

  /** Erster Tipp auf „Zurückziehen“: fragt nach, statt gleich zurückzuziehen. */
  askWithdraw(absence: Absence): void {
    this.touch();
    if (this.isBusy() || this.tapGuard()) {
      return;
    }
    this.mineNotice.set('');
    this.error.set('');
    this.confirmingId.set(absence.id);
    this.guardTaps();
  }

  cancelWithdraw(): void {
    this.touch();
    if (!this.tapGuard()) {
      this.confirmingId.set(null);
    }
  }

  withdraw(absence: Absence): void {
    this.touch();
    if (this.isBusy() || this.tapGuard() || this.confirmingId() !== absence.id) {
      return;
    }
    this.error.set('');
    this.mineNotice.set('');
    this.withdrawingId.set(absence.id);
    this.runRequest(
      this.kioskApi.withdrawAbsence(this.auth(), absence.id),
      updated => {
        this.withdrawingId.set(null);
        this.confirmingId.set(null);
        this.mine.update(list => (list ?? []).map(item => (item.id === updated.id ? updated : item)));
        this.mineNotice.set('Antrag zurückgezogen.');
        this.reloadOverview();
      },
      error => {
        this.withdrawingId.set(null);
        this.confirmingId.set(null);
        this.error.set(this.describe(error, 'withdraw'));
        // 404/409: der Antrag ist inzwischen entschieden - den neuen Stand zeigen.
        if (error.status === 404 || error.status === 409) {
          this.reloadMine();
        }
      },
    );
  }

  // ------------------------------------------------------------ Anzeige

  /** Fehlende Einstellungen einer Art (Server-Meldung) oder null. */
  protected missing(kind: AbsenceKind): string | null {
    return this.overview()?.kinds.find(candidate => candidate.kind === kind)?.missing ?? null;
  }

  protected compact(days: string[]): string {
    return compactDays(days, this.currentYear());
  }

  /** Nur ein unentschiedener Antrag; ein begonnenes Eintragen zählt als entschieden. */
  protected canWithdraw(absence: Absence): boolean {
    return absence.status === 'pending' && !absence.applyStartedAt;
  }

  protected statusOf(absence: Absence): string {
    return absence.status === 'pending' && absence.applyStartedAt ? 'Wird eingetragen' : ABSENCE_STATUS_LABELS[absence.status];
  }

  // ------------------------------------------------------------ Hilfen

  private openDays(kind: AbsenceKind): void {
    const missing = this.missing(kind);
    if (missing) {
      this.error.set(missing);
      return;
    }
    if (this.kind() !== kind) {
      this.days.set([]);
    }
    this.kind.set(kind);
    this.go('days');
  }

  /** Schrittwechsel mit Rücksprungpunkt; danach greift die Tipp-Sperre. */
  private go(step: Step, remember = true): void {
    if (remember) {
      this.history.push(this.step());
    }
    this.step.set(step);
    this.guardTaps();
  }

  private guardTaps(): void {
    if (this.guardTimer !== null) {
      window.clearTimeout(this.guardTimer);
    }
    this.tapGuard.set(true);
    this.guardTimer = window.setTimeout(() => {
      this.guardTimer = null;
      this.tapGuard.set(false);
    }, ABSENCE_TAP_GUARD_MS);
  }

  /**
   * Konto und belegte Tage nach Absenden oder Zurückziehen; still und neben
   * einer laufenden Anfrage. Gilt nur für den Mitarbeiter, der sie stellt.
   */
  private reloadOverview(): void {
    const employeeId = this.auth().employeeId;
    this.refresh?.unsubscribe();
    this.refresh = this.kioskApi.absenceOverview(this.auth()).subscribe({
      next: overview => {
        if (this.auth().employeeId === employeeId) {
          this.overview.set(overview);
        }
      },
      error: () => undefined,
    });
  }

  private reloadMine(): void {
    this.runRequest(
      this.kioskApi.myAbsences(this.auth()),
      list => this.mine.set(list),
      () => undefined,
    );
  }

  /**
   * Eine Anfrage zur Zeit; Antworten gelten nur für den Mitarbeiter, der sie
   * gestellt hat, und nur, solange der Schritt nicht verlassen wurde.
   */
  private runRequest<T>(source: Observable<T>, onNext: (value: T) => void, onError: (error: HttpErrorResponse) => void): void {
    const employeeId = this.auth().employeeId;
    const token = ++this.requestToken;
    const current = () => token === this.requestToken && this.auth().employeeId === employeeId;
    this.request?.unsubscribe();
    this.isBusy.set(true);
    this.request = source.subscribe({
      next: value => {
        if (current()) {
          this.isBusy.set(false);
          onNext(value);
        }
      },
      error: (error: HttpErrorResponse) => {
        if (current()) {
          this.isBusy.set(false);
          onError(error);
        }
      },
    });
  }

  private describe(error: HttpErrorResponse, action: 'list' | 'submit' | 'withdraw'): string {
    const serverMessage = typeof error?.error?.message === 'string' ? error.error.message : '';
    switch (error?.status ?? 0) {
      case 400:
      case 404:
      case 409:
        return serverMessage || 'Die Anfrage war nicht gültig.';
      case 401:
        return 'Anmeldung nicht mehr gültig. Bitte abmelden und neu anmelden.';
      case 429:
        return serverMessage || 'Zu viele Anfragen. Bitte kurz warten.';
      case 503:
        return serverMessage || 'Kimai ist gerade nicht erreichbar. Bitte später erneut versuchen.';
      case 0:
        // Timeout oder kein Netz: bei Absenden/Zurückziehen weiß der Kiosk nicht, ob es angekommen ist.
        return action === 'list'
          ? 'Keine Verbindung zum Server. Bitte später erneut versuchen.'
          : 'Keine Antwort vom Server. Bitte unter „Meine Abwesenheiten“ prüfen, ob es angekommen ist.';
      default:
        return 'Der Server konnte die Anfrage nicht verarbeiten. Bitte später erneut versuchen.';
    }
  }
}
