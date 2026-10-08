import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, ElementRef, OnInit, computed, effect, inject, input, output, signal, viewChild } from '@angular/core';
import { Observable, Subscription } from 'rxjs';

import {
  CorrectionAuth,
  CorrectionEntry,
  CorrectionKind,
  CorrectionShift,
  CorrectionTimesheets,
  EmployeeTask,
  KioskCorrection,
  LocalDateTime,
  SubmitCorrection,
} from '../../../core/models/kiosk.models';
import { KioskApi } from '../../../core/services/kiosk-api';
import {
  addMinutes,
  earlier,
  floorToStep,
  formatRange,
  later,
  minutesBetween,
  nowInZone,
} from '../../../shared/components/time-stepper/local-time';
import { TimeStepper } from '../../../shared/components/time-stepper/time-stepper';
import { CorrectionChange, CorrectionLine, KIND_LABELS, STATUS_LABELS, afterLines, beforeLines } from './correction-format';

/** Ohne Tipp geht der Kiosk zurück in den Ruhezustand (Abmeldung). */
export const CORRECTION_IDLE_MS = 120_000;

export const MAX_COMMENT_LENGTH = 300;

/**
 * Direkt in die Zeitschritte für genau dieses Timesheet: „Vergessen
 * auszustempeln?“ öffnet `setEnd`, „Pause vergessen?“ `addPause` (auch für
 * den laufenden Eintrag).
 */
export interface CorrectionStart {
  kind: 'addPause' | 'setEnd';
  timesheetId: number;
}

type Step = 'kind' | 'entry' | 'task' | 'time' | 'pauseAsk' | 'summary' | 'sent' | 'mine';
type Field = 'begin' | 'end' | 'pauseBegin' | 'pauseEnd';

/** Rücksprungpunkt der „Zurück“-Knöpfe. */
interface Position {
  step: Step;
  fieldIndex: number;
}

const FIELD_LABELS: Record<Field, string> = {
  begin: 'Beginn',
  end: 'Ende',
  pauseBegin: 'Pause beginnt',
  pauseEnd: 'Pause endet',
};

const MINUTES_PER_DAY = 24 * 60;
/** Wie weit die API zurück korrigiert (31 Tage) und wie lang Schicht und Pause höchstens sind. */
const MAX_AGE_MINUTES = 31 * MINUTES_PER_DAY;
const MAX_SHIFT_MINUTES = 16 * 60;
const MAX_PAUSE_MINUTES = 4 * 60;
const DEFAULT_PAUSE_MINUTES = 30;
const DEFAULT_SHIFT_MINUTES = 8 * 60;

/**
 * Korrektur beantragen (Epic #94): Art wählen, Eintrag wählen, Zeiten mit dem
 * Stepper einstellen, Zusammenfassung, absenden - und „Meine Anträge“.
 * Gemeinsam für Kiosk und /clock; die Seite gibt nur Anmeldung, Tätigkeiten
 * und Layout vor. Keine Offline-Queue: jeder Schritt fragt die API, ein
 * Ausfall wird gemeldet statt gepuffert.
 *
 * Die Komponente lebt genau so lange wie die Sitzung: ein Identitätswechsel,
 * „X“ oder der Abbruch zerstören sie samt laufender Anfragen, eine verspätete
 * Antwort erreicht also nie die Anzeige eines anderen Mitarbeiters.
 */
@Component({
  selector: 'app-correction-flow',
  imports: [TimeStepper],
  templateUrl: './correction-flow.html',
  styleUrl: './correction-flow.scss',
  host: {
    '[class.layout-terminal]': "layout() === 'terminal'",
    '[class.layout-clock]': "layout() === 'clock'",
    '(pointerdown)': 'touch()',
    '(keydown)': 'touch()',
  },
})
export class CorrectionFlow implements OnInit {
  private readonly kioskApi = inject(KioskApi);
  private readonly destroyRef = inject(DestroyRef);

  /** Angemeldeter Mitarbeiter: ID plus PIN oder Karte (Karten-Session: `pin` leer). */
  readonly auth = input.required<CorrectionAuth>();
  /** Das Terminal hat keine Tastatur und kein Kommentarfeld; /clock schon. */
  readonly layout = input<'terminal' | 'clock'>('terminal');
  /** Terminal-ID oder `clock`. */
  readonly source = input.required<string>();
  readonly tasks = input<EmployeeTask[]>([]);
  readonly defaultTaskLabel = input('Standard-Tätigkeit');
  readonly start = input<CorrectionStart | null>(null);

  /** Zurück in die Sitzung (Mitarbeiter bleibt angemeldet). */
  readonly closed = output<void>();
  /** 2 min ohne Tipp: die Seite meldet ab. */
  readonly timedOut = output<void>();

  protected readonly formatRange = formatRange;
  protected readonly kindLabels = KIND_LABELS;
  protected readonly statusLabels = STATUS_LABELS;
  protected readonly maxCommentLength = MAX_COMMENT_LENGTH;
  protected readonly kinds: CorrectionKind[] = ['addPause', 'setEnd', 'addShift', 'changeTimes'];

  readonly step = signal<Step>('kind');
  readonly isBusy = signal(false);
  readonly error = signal('');
  readonly kind = signal<CorrectionKind | null>(null);
  readonly timesheets = signal<CorrectionTimesheets | null>(null);
  readonly entry = signal<CorrectionEntry | null>(null);
  readonly begin = signal<LocalDateTime>('');
  readonly end = signal<LocalDateTime>('');
  readonly pauseBegin = signal<LocalDateTime>('');
  readonly pauseEnd = signal<LocalDateTime>('');
  /** Weitere Tätigkeit der nachgetragenen Schicht; null = Haupttätigkeit. */
  readonly taskId = signal<string | null>(null);
  readonly withPause = signal(false);
  readonly fieldIndex = signal(0);
  readonly comment = signal('');
  readonly mine = signal<KioskCorrection[] | null>(null);
  /** Rückmeldung unter „Meine Anträge“ (z. B. nach dem Zurückziehen). */
  readonly mineNotice = signal('');
  readonly withdrawingId = signal<string | null>(null);

  private readonly heading = viewChild<ElementRef<HTMLElement>>('heading');

  /** Jetzt in der Zeitzone der Zeiten; Obergrenze der Steppers. */
  private readonly now = signal('');
  private history: Position[] = [];
  private request: Subscription | null = null;
  /** Zählt jede Anfrage und jeden Verlassen eines Schritts: nur die jüngste darf anzeigen. */
  private requestToken = 0;
  private idleTimer: number | null = null;

  protected readonly title = computed(() => {
    switch (this.step()) {
      case 'kind':
        return 'Korrektur';
      case 'entry':
        return 'Eintrag wählen';
      case 'task':
        return 'Tätigkeit wählen';
      case 'time':
        return this.fieldLabel();
      case 'pauseAsk':
        return 'Pause eintragen?';
      case 'summary':
        return 'Zusammenfassung';
      case 'sent':
        return 'Korrektur';
      case 'mine':
        return 'Meine Anträge';
    }
  });

  protected readonly subtitle = computed(() => {
    const kind = this.kind();
    return kind && this.step() !== 'kind' && this.step() !== 'mine' && this.step() !== 'sent' ? KIND_LABELS[kind] : '';
  });

  /** Felder der gewählten Art in der Reihenfolge der Eingabe. */
  protected readonly fields = computed<Field[]>(() => {
    switch (this.kind()) {
      case 'addPause':
        return ['pauseBegin', 'pauseEnd'];
      case 'setEnd':
        return ['end'];
      case 'changeTimes':
        return ['begin', 'end'];
      case 'addShift':
        return this.withPause() ? ['begin', 'end', 'pauseBegin', 'pauseEnd'] : ['begin', 'end'];
      default:
        return [];
    }
  });

  protected readonly field = computed<Field | null>(() => this.fields()[this.fieldIndex()] ?? null);
  protected readonly fieldLabel = computed(() => {
    const field = this.field();
    return field ? FIELD_LABELS[field] : '';
  });
  protected readonly isLastField = computed(() => this.fieldIndex() >= this.fields().length - 1);

  /** Der Eintrag, auf den sich der Antrag bezieht, als Zeile für die Zeitschritte. */
  protected readonly entryLine = computed(() => {
    const entry = this.entry();
    return entry ? `${entry.label} ${formatRange(entry.begin, entry.end)}` : '';
  });

  /** Einträge, die zur Art passen, in Schichten gruppiert (neueste zuerst, wie die API sie liefert). */
  protected readonly selectableShifts = computed<CorrectionShift[]>(() => {
    const kind = this.kind();
    const sheets = this.timesheets();
    if (!kind || !sheets) {
      return [];
    }
    return sheets.shifts
      .map(shift => ({ ...shift, entries: shift.entries.filter(entry => this.isSelectable(kind, entry)) }))
      .filter(shift => shift.entries.length > 0);
  });

  protected readonly taskOptions = computed(() => [
    { id: null as string | null, label: this.defaultTaskLabel() },
    ...this.tasks().map(task => ({ id: task.id as string | null, label: task.label })),
  ]);

  protected readonly change = computed<CorrectionChange | null>(() => {
    const kind = this.kind();
    if (!kind) {
      return null;
    }
    const entry = this.entry();
    const original = entry ? { label: entry.label, begin: entry.begin, end: entry.end } : null;
    const taskLabel = this.taskOptions().find(option => option.id === this.taskId())?.label ?? null;
    const pause = kind === 'addPause' || (kind === 'addShift' && this.withPause());
    return {
      kind,
      original,
      begin: this.sentBegin(),
      end: this.sentEnd(),
      pauseBegin: pause ? this.pauseBegin() : null,
      pauseEnd: pause ? this.pauseEnd() : null,
      taskLabel,
    };
  });

  protected readonly before = computed(() => {
    const change = this.change();
    return change ? beforeLines(change) : [];
  });
  protected readonly after = computed(() => {
    const change = this.change();
    return change ? afterLines(change) : [];
  });

  /**
   * Die Regeln der API für die eingestellten Zeiten (TimeCorrectionValidator),
   * damit „Weiter“ und „Absenden“ erst gehen, wenn alles im Bereich liegt -
   * auch wenn ein Wert schon außerhalb gestartet ist (Eintrag über 16 h).
   */
  protected readonly invalid = computed<string | null>(() => this.validationMessage());

  /** `changeTimes` ohne geänderte Zeit ist kein Antrag. */
  protected readonly unchanged = computed(() => this.kind() === 'changeTimes' && !this.sentBegin() && !this.sentEnd());

  /** Pause im laufenden Eintrag: der Mitarbeiter stempelt bis zur Freigabe normal weiter. */
  protected readonly isRunningPause = computed(() => this.kind() === 'addPause' && this.entry()?.end === null);

  constructor() {
    // Nach jedem Schritt steht der Fokus auf der Überschrift: ein Knopf, der
    // gerade verschwindet, ließe ihn sonst im Nichts zurück.
    effect(() => {
      this.step();
      this.fieldIndex();
      this.heading()?.nativeElement.focus({ preventScroll: true });
    });
  }

  ngOnInit(): void {
    this.touch();
    this.destroyRef.onDestroy(() => {
      this.request?.unsubscribe();
      if (this.idleTimer !== null) {
        window.clearTimeout(this.idleTimer);
      }
    });
    const start = this.start();
    if (start) {
      this.chooseKind(start.kind, start.timesheetId);
    }
  }

  /** Jeder Tipp im Ablauf verschiebt das Inaktivitäts-Ende. */
  touch(): void {
    if (this.idleTimer !== null) {
      window.clearTimeout(this.idleTimer);
    }
    this.idleTimer = window.setTimeout(() => this.timedOut.emit(), CORRECTION_IDLE_MS);
  }

  // ------------------------------------------------------------ Schritte

  chooseKind(kind: CorrectionKind, timesheetId: number | null = null): void {
    this.touch();
    if (this.isBusy()) {
      return;
    }
    this.kind.set(kind);
    this.entry.set(null);
    this.withPause.set(false);
    this.taskId.set(null);
    this.fieldIndex.set(0);
    this.error.set('');
    // Die Auswahlliste steht in jedem Fall neu beim Server: Zeitzone, Zeiten
    // und „offener Antrag“ ändern sich zwischen zwei Besuchen.
    this.loadTimesheets(() => {
      this.now.set(nowInZone(this.timesheets()?.timeZone));
      if (timesheetId !== null) {
        this.openEntry(timesheetId);
      } else if (kind === 'addShift') {
        this.push('kind');
        this.initShift();
        this.step.set(this.tasks().length > 0 ? 'task' : 'time');
      } else {
        this.push('kind');
        this.step.set('entry');
      }
    });
  }

  chooseEntry(entry: CorrectionEntry): void {
    this.touch();
    this.entry.set(entry);
    this.initEntryTimes(entry);
    this.push('entry');
    this.fieldIndex.set(0);
    this.step.set('time');
  }

  chooseTask(taskId: string | null): void {
    this.touch();
    this.taskId.set(taskId);
    this.push('task');
    this.fieldIndex.set(0);
    this.step.set('time');
  }

  /** Antwort auf „Pause eintragen?“ bei einer nachgetragenen Schicht. */
  choosePause(withPause: boolean): void {
    this.touch();
    this.withPause.set(withPause);
    this.push('pauseAsk');
    if (withPause) {
      this.initPause(this.begin(), this.end());
      this.fieldIndex.set(2);
      this.step.set('time');
    } else {
      this.step.set('summary');
    }
  }

  next(): void {
    this.touch();
    if (this.step() !== 'time' || this.isBusy()) {
      return;
    }
    if (!this.isLastField()) {
      this.push('time');
      // Beginn oder Ende können sich seit der Vorbelegung der Pause geändert haben.
      if (this.kind() === 'addShift' && this.fieldIndex() === 1) {
        this.initPause(this.begin(), this.end());
      }
      this.fieldIndex.update(index => index + 1);
      return;
    }
    if (this.kind() === 'addShift' && !this.withPause()) {
      this.push('time');
      this.step.set('pauseAsk');
      return;
    }
    this.push('time');
    this.error.set('');
    this.step.set('summary');
  }

  back(): void {
    this.touch();
    // Eine laufende Anfrage gehört zum Schritt, den der Mitarbeiter verlässt.
    this.requestToken++;
    this.request?.unsubscribe();
    this.request = null;
    this.isBusy.set(false);
    this.withdrawingId.set(null);
    this.error.set('');
    this.mineNotice.set('');
    const previous = this.history.pop();
    if (!previous) {
      this.closed.emit();
      return;
    }
    this.fieldIndex.set(previous.fieldIndex);
    this.step.set(previous.step);
  }

  done(): void {
    this.touch();
    this.closed.emit();
  }

  openMine(): void {
    this.touch();
    if (this.isBusy()) {
      return;
    }
    this.error.set('');
    this.mineNotice.set('');
    this.mine.set(null);
    const auth = this.auth();
    this.runRequest(
      this.kioskApi.myCorrections(auth),
      list => {
        this.mine.set(list);
        this.push('kind');
        this.step.set('mine');
      },
      error => this.error.set(this.describe(error, 'list')),
    );
  }

  withdraw(correction: KioskCorrection): void {
    this.touch();
    if (this.isBusy()) {
      return;
    }
    this.error.set('');
    this.mineNotice.set('');
    this.withdrawingId.set(correction.id);
    this.runRequest(
      this.kioskApi.withdrawCorrection(this.auth(), correction.id),
      updated => {
        this.withdrawingId.set(null);
        this.mine.update(list => (list ?? []).map(item => (item.id === updated.id ? updated : item)));
        this.mineNotice.set('Antrag zurückgezogen.');
      },
      error => {
        this.withdrawingId.set(null);
        this.error.set(this.describe(error, 'withdraw'));
        // 404/409: der Antrag ist inzwischen entschieden - den neuen Stand zeigen.
        if (error.status === 404 || error.status === 409) {
          this.reloadMine();
        }
      },
    );
  }

  submit(): void {
    this.touch();
    const kind = this.kind();
    if (!kind || this.isBusy() || this.unchanged() || this.invalid()) {
      return;
    }
    const change = this.change()!;
    const body: SubmitCorrection = {
      ...this.auth(),
      kind,
      timesheetId: kind === 'addShift' ? null : (this.entry()?.id ?? null),
      begin: change.begin,
      end: change.end,
      pauseBegin: change.pauseBegin,
      pauseEnd: change.pauseEnd,
      taskId: kind === 'addShift' ? this.taskId() : null,
      // Das Terminal hat keine Tastatur: der Kommentar gehört nur zu /clock.
      comment: this.layout() === 'clock' ? (this.comment().trim().slice(0, MAX_COMMENT_LENGTH) || null) : null,
      source: this.source(),
    };
    this.error.set('');
    this.runRequest(
      this.kioskApi.submitCorrection(body),
      () => {
        // Der Antrag ist raus: Rücksprung gibt es nicht mehr.
        this.history = [];
        this.step.set('sent');
      },
      // 400: Servermeldung, der Mitarbeiter bleibt in der Zusammenfassung und kann zurück und korrigieren.
      error => this.error.set(this.describe(error, 'submit')),
    );
  }

  setComment(event: Event): void {
    this.touch();
    this.comment.set((event.target as HTMLTextAreaElement).value.slice(0, MAX_COMMENT_LENGTH));
  }

  // ------------------------------------------------------------ Zeiten

  protected valueOf(field: Field): LocalDateTime {
    return this.fieldSignal(field)();
  }

  protected setField(field: Field, value: LocalDateTime): void {
    this.touch();
    const previous = this.valueOf(field);
    this.fieldSignal(field).set(value);
    // Die Pause behält ihre Länge, wenn ihr Beginn wandert - nie über das Ende des Eintrags hinaus.
    if (field === 'pauseBegin') {
      const length = Math.max(1, minutesBetween(previous, this.pauseEnd()));
      this.pauseEnd.set(this.clamp(addMinutes(value, length), this.bounds('pauseEnd', value)));
    }
    // Eine nachgetragene Schicht behält ihre Länge, wenn ihr Beginn wandert - höchstens 16 h und nie über jetzt hinaus.
    if (field === 'begin' && this.kind() === 'addShift') {
      const length = Math.max(1, minutesBetween(previous, this.end()));
      this.end.set(this.clamp(addMinutes(value, length), this.bounds('end', value)));
    }
  }

  /** Grenzen eines Feldes; die API prüft dieselben Regeln noch einmal. */
  protected boundsOf(field: Field): { min: LocalDateTime | null; max: LocalDateTime | null } {
    return this.bounds(field, this.begin());
  }

  private bounds(field: Field, begin: LocalDateTime): { min: LocalDateTime | null; max: LocalDateTime | null } {
    const now = this.now();
    const entry = this.entry();
    switch (this.kind()) {
      case 'addPause': {
        if (!entry) {
          return { min: null, max: null };
        }
        // Läuft der Eintrag noch, endet die Pause höchstens jetzt.
        const until = entry.end ?? now;
        return field === 'pauseBegin'
          ? { min: addMinutes(entry.begin, 1), max: addMinutes(until, -1) }
          : { min: addMinutes(this.pauseBegin(), 1), max: earlier(until, addMinutes(this.pauseBegin(), MAX_PAUSE_MINUTES)) };
      }
      case 'setEnd':
        // Ein Ende in der Minute des bisherigen Endes wäre keine Änderung.
        return entry?.end ? { min: addMinutes(entry.begin, 1), max: addMinutes(entry.end, -1) } : { min: null, max: null };
      case 'changeTimes':
        return field === 'begin'
          ? { min: addMinutes(this.end(), -MAX_SHIFT_MINUTES), max: addMinutes(this.end(), -1) }
          : { min: addMinutes(this.begin(), 1), max: earlier(now, addMinutes(this.begin(), MAX_SHIFT_MINUTES)) };
      case 'addShift':
        switch (field) {
          // Nicht am Ende festgemacht: das Ende wandert mit (setField), sonst sperrt ein Tag zurück den Weg vor.
          case 'begin':
            return { min: addMinutes(now, -MAX_AGE_MINUTES), max: addMinutes(now, -1) };
          case 'end':
            return { min: addMinutes(begin, 1), max: earlier(now, addMinutes(begin, MAX_SHIFT_MINUTES)) };
          case 'pauseBegin':
            return { min: addMinutes(this.begin(), 1), max: addMinutes(this.end(), -2) };
          case 'pauseEnd':
            return {
              min: addMinutes(this.pauseBegin(), 1),
              max: earlier(addMinutes(this.end(), -1), addMinutes(this.pauseBegin(), MAX_PAUSE_MINUTES)),
            };
        }
    }
    return { min: null, max: null };
  }

  private clamp(value: LocalDateTime, bounds: { min: LocalDateTime | null; max: LocalDateTime | null }): LocalDateTime {
    const capped = bounds.max === null ? value : earlier(value, bounds.max);
    return bounds.min === null ? capped : later(capped, bounds.min);
  }

  private validationMessage(): string | null {
    const kind = this.kind();
    const entry = this.entry();
    const begin = this.begin();
    const end = this.end();
    const pauseBegin = this.pauseBegin();
    const pauseEnd = this.pauseEnd();
    const pauseError = (): string | null => {
      if (pauseEnd <= pauseBegin) {
        return 'Das Ende der Pause muss nach ihrem Beginn liegen.';
      }
      return minutesBetween(pauseBegin, pauseEnd) > MAX_PAUSE_MINUTES ? 'Eine Pause darf höchstens 4 Stunden dauern.' : null;
    };
    switch (kind) {
      case 'addPause':
        if (!entry) {
          return null;
        }
        if (!entry.end) {
          return pauseError()
            ?? (pauseBegin <= entry.begin ? 'Die Pause muss nach dem Beginn des Eintrags liegen.' : null)
            ?? (pauseEnd <= this.now() ? null : 'Die Pause darf nicht in der Zukunft enden.');
        }
        return pauseError() ?? (pauseBegin > entry.begin && pauseEnd <= entry.end ? null : 'Die Pause muss innerhalb des Eintrags liegen.');
      case 'setEnd':
        if (!entry?.end) {
          return null;
        }
        if (end <= entry.begin) {
          return 'Das Ende muss nach dem Beginn liegen.';
        }
        return end < entry.end ? null : 'Das neue Ende muss vor dem bisherigen Ende liegen.';
      case 'changeTimes':
        if (end <= begin) {
          return 'Das Ende muss nach dem Beginn liegen.';
        }
        return minutesBetween(begin, end) > MAX_SHIFT_MINUTES
          ? 'Ein Eintrag darf höchstens 16 Stunden dauern. Bitte Beginn oder Ende anpassen.'
          : null;
      case 'addShift':
        if (end <= begin) {
          return 'Das Ende muss nach dem Beginn liegen.';
        }
        if (minutesBetween(begin, end) > MAX_SHIFT_MINUTES) {
          return 'Eine Schicht darf höchstens 16 Stunden dauern.';
        }
        if (!this.withPause()) {
          return null;
        }
        return pauseError() ?? (pauseBegin > begin && pauseEnd < end ? null : 'Die Pause muss innerhalb der Schicht liegen.');
      default:
        return null;
    }
  }

  /** Vorbelegung bei der Wahl eines vorhandenen Eintrags. */
  private initEntryTimes(entry: CorrectionEntry): void {
    const entryEnd = entry.end ?? entry.begin;
    const length = Math.max(0, minutesBetween(entry.begin, entryEnd));
    switch (this.kind()) {
      case 'addPause': {
        if (entry.end === null) {
          // Läuft der Eintrag noch: die letzten 30 min bis jetzt (auf 5 min abgerundet), nie vor dem Beginn.
          const pauseEnd = floorToStep(this.now(), 5);
          this.pauseBegin.set(later(addMinutes(pauseEnd, -DEFAULT_PAUSE_MINUTES), addMinutes(entry.begin, 1)));
          this.pauseEnd.set(pauseEnd);
          break;
        }
        // Pause = 30 min um die Mitte des Eintrags, nie über den Eintrag hinaus.
        const begin = floorToStep(addMinutes(entry.begin, Math.floor(length / 2) - DEFAULT_PAUSE_MINUTES / 2), 5);
        this.initPauseWithin(begin, entry.begin, entryEnd);
        break;
      }
      case 'setEnd': {
        // Ein vergessenes Ausstempeln: eine übliche Schicht ab Beginn, sonst die Mitte.
        const guess = length > DEFAULT_SHIFT_MINUTES
          ? addMinutes(entry.begin, DEFAULT_SHIFT_MINUTES)
          : addMinutes(entry.begin, Math.floor(length / 2));
        this.end.set(this.clamp(guess, { min: addMinutes(entry.begin, 1), max: addMinutes(entryEnd, -1) }));
        break;
      }
      default:
        this.begin.set(entry.begin);
        this.end.set(entryEnd);
    }
  }

  private initPauseWithin(begin: LocalDateTime, entryBegin: LocalDateTime, entryEnd: LocalDateTime): void {
    const pauseBegin = this.clamp(begin, { min: addMinutes(entryBegin, 1), max: addMinutes(entryEnd, -1) });
    this.pauseBegin.set(pauseBegin);
    this.pauseEnd.set(earlier(addMinutes(pauseBegin, DEFAULT_PAUSE_MINUTES), entryEnd));
  }

  /** Nachgetragene Schicht: die letzten acht Stunden bis jetzt. */
  private initShift(): void {
    const now = floorToStep(this.now(), 5);
    this.end.set(now);
    this.begin.set(addMinutes(now, -DEFAULT_SHIFT_MINUTES));
  }

  private initPause(begin: LocalDateTime, end: LocalDateTime): void {
    const length = Math.max(0, minutesBetween(begin, end));
    this.initPauseWithin(
      floorToStep(addMinutes(begin, Math.floor(length / 2) - DEFAULT_PAUSE_MINUTES / 2), 5),
      begin,
      addMinutes(end, -1),
    );
  }

  /**
   * Was der Antrag an Zeiten schickt: bei `changeTimes` nur die geänderten
   * (die API übernimmt eine gleich gebliebene Zeit samt Sekunden), bei
   * `setEnd` das Ende, bei `addShift` Beginn und Ende, bei `addPause` nichts.
   */
  private sentBegin(): LocalDateTime | null {
    switch (this.kind()) {
      case 'addShift':
        return this.begin();
      case 'changeTimes':
        return this.begin() !== this.entry()?.begin ? this.begin() : null;
      default:
        return null;
    }
  }

  private sentEnd(): LocalDateTime | null {
    switch (this.kind()) {
      case 'addShift':
      case 'setEnd':
        return this.end();
      case 'changeTimes':
        return this.end() !== (this.entry()?.end ?? null) ? this.end() : null;
      default:
        return null;
    }
  }

  private fieldSignal(field: Field) {
    switch (field) {
      case 'begin':
        return this.begin;
      case 'end':
        return this.end;
      case 'pauseBegin':
        return this.pauseBegin;
      case 'pauseEnd':
        return this.pauseEnd;
    }
  }

  // ------------------------------------------------------------ Hilfen

  /**
   * Bei Pause jede Arbeit, auch die laufende; sonst jeder gestoppte Eintrag;
   * nie einer mit offenem Antrag.
   */
  private isSelectable(kind: CorrectionKind, entry: CorrectionEntry): boolean {
    if (entry.hasOpenRequest) {
      return false;
    }
    return kind === 'addPause' ? entry.kind === 'work' : entry.end !== null;
  }

  private openEntry(timesheetId: number): void {
    const entry = this.timesheets()
      ?.shifts.flatMap(shift => shift.entries)
      .find(candidate => candidate.id === timesheetId);
    const kind = this.kind()!;
    if (!entry || !this.isSelectable(kind, entry)) {
      this.error.set(entry?.hasOpenRequest
        ? 'Für diesen Eintrag gibt es schon einen offenen Antrag.'
        : 'Der Eintrag wurde nicht gefunden.');
      return;
    }
    this.push('kind');
    this.entry.set(entry);
    this.initEntryTimes(entry);
    this.fieldIndex.set(0);
    this.step.set('time');
  }

  private loadTimesheets(done: () => void): void {
    this.runRequest(
      this.kioskApi.correctionTimesheets(this.auth()),
      sheets => {
        this.timesheets.set(sheets);
        done();
      },
      error => this.error.set(this.describe(error, 'list')),
    );
  }

  private reloadMine(): void {
    this.runRequest(
      this.kioskApi.myCorrections(this.auth()),
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

  private push(from: Step): void {
    this.history.push({ step: from, fieldIndex: this.fieldIndex() });
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
          : 'Keine Antwort vom Server. Bitte unter „Meine Anträge“ prüfen, ob es angekommen ist.';
      default:
        return 'Der Server konnte die Anfrage nicht verarbeiten. Bitte später erneut versuchen.';
    }
  }

  protected lines(correction: KioskCorrection, which: 'before' | 'after'): CorrectionLine[] {
    const change = this.toChange(correction);
    return which === 'before' ? beforeLines(change) : afterLines(change);
  }

  private toChange(correction: KioskCorrection): CorrectionChange {
    return {
      kind: correction.kind,
      original: correction.original,
      begin: correction.begin,
      end: correction.end,
      pauseBegin: correction.pauseBegin,
      pauseEnd: correction.pauseEnd,
      taskLabel: correction.taskLabel,
      observedEnd: correction.observedAtApply ? correction.observedEndAtApply : null,
    };
  }

  /** Pause im laufenden Eintrag, noch nicht entschieden. */
  protected isPendingRunningPause(correction: KioskCorrection): boolean {
    return correction.status === 'pending' && correction.kind === 'addPause' && correction.original?.end === null;
  }
}
