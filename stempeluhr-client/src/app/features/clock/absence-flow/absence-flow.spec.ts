import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, Subject, throwError } from 'rxjs';
import type { Mock } from 'vitest';

import { KioskAbsenceOverview } from '../../../core/models/absence.models';
import { CorrectionAuth } from '../../../core/models/kiosk.models';
import { KioskApi } from '../../../core/services/kiosk-api';
import { ABSENCE_NOW, ABSENCE_OVERVIEW, absence } from './absence-fixtures';
import { ABSENCE_IDLE_MS, ABSENCE_TAP_GUARD_MS, AbsenceFlow } from './absence-flow';

describe('AbsenceFlow', () => {
  const auth: CorrectionAuth = { employeeId: 'max', pin: '1234', nfcCardId: null };

  let absenceOverview: ReturnType<typeof vi.fn>;
  let submitAbsence: ReturnType<typeof vi.fn>;
  let myAbsences: ReturnType<typeof vi.fn>;
  let withdrawAbsence: ReturnType<typeof vi.fn>;
  let fixture: ComponentFixture<AbsenceFlow>;
  let closed: Mock<() => void>;
  let timedOut: Mock<() => void>;

  beforeEach(async () => {
    absenceOverview = vi.fn(() => of(ABSENCE_OVERVIEW));
    submitAbsence = vi.fn(() => of(absence()));
    myAbsences = vi.fn(() => of([]));
    withdrawAbsence = vi.fn(() => of(absence({ status: 'withdrawn' })));
    closed = vi.fn<() => void>();
    timedOut = vi.fn<() => void>();

    await TestBed.configureTestingModule({
      imports: [AbsenceFlow],
      providers: [{ provide: KioskApi, useValue: { absenceOverview, submitAbsence, myAbsences, withdrawAbsence } }],
    }).compileComponents();

    vi.useFakeTimers();
    vi.setSystemTime(ABSENCE_NOW);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  function create(options: { layout?: 'terminal' | 'clock'; auth?: CorrectionAuth } = {}): void {
    fixture = TestBed.createComponent(AbsenceFlow);
    fixture.componentRef.setInput('auth', options.auth ?? auth);
    fixture.componentRef.setInput('layout', options.layout ?? 'terminal');
    fixture.componentRef.setInput('source', options.layout === 'clock' ? 'clock' : 'term-1');
    fixture.componentInstance.closed.subscribe(() => closed());
    fixture.componentInstance.timedOut.subscribe(() => timedOut());
    fixture.detectChanges();
  }

  const root = () => fixture.nativeElement as HTMLElement;
  const title = () => root().querySelector('.flow-title')?.textContent?.trim();
  const text = () => root().textContent?.replace(/\s+/g, ' ') ?? '';

  /** Text eines Elements mit Leerzeichen zwischen den Teilen (jsdom kennt kein innerText). */
  function words(element: Element): string {
    const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
    const parts: string[] = [];
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const part = node.textContent?.trim();
      if (part) {
        parts.push(part);
      }
    }
    return parts.join(' ');
  }

  function button(label: string): HTMLButtonElement | undefined {
    return ([...root().querySelectorAll('button')] as HTMLButtonElement[]).find(candidate => candidate.textContent?.trim() === label);
  }

  /** Tippt nach Ablauf der Tipp-Sperre - wie ein Mensch, der hinsieht. */
  function press(label: string): void {
    vi.advanceTimersByTime(ABSENCE_TAP_GUARD_MS);
    fixture.detectChanges();
    const target = button(label);
    expect(target, `Knopf "${label}"`).toBeDefined();
    target!.click();
    fixture.detectChanges();
  }

  /** `yyyy-MM-dd` im angezeigten Monat. */
  function day(value: string): HTMLButtonElement {
    const label = `${value.slice(8, 10)}.${value.slice(5, 7)}.${value.slice(0, 4)}`;
    const target = ([...root().querySelectorAll('.day')] as HTMLButtonElement[]).find(candidate =>
      candidate.getAttribute('aria-label')?.includes(label));
    expect(target, `Tag ${value}`).toBeDefined();
    return target!;
  }

  function tapDays(...values: string[]): void {
    vi.advanceTimersByTime(ABSENCE_TAP_GUARD_MS);
    fixture.detectChanges();
    for (const value of values) {
      day(value).click();
      fixture.detectChanges();
    }
  }

  const selection = () => root().querySelector('.absence-selection')?.textContent?.trim();

  describe('Art und Urlaubskonto', () => {
    it('loads the account once and shows entitlement, taken, requested and rest', () => {
      create();

      expect(absenceOverview).toHaveBeenCalledExactlyOnceWith(auth);
      expect(title()).toBe('Abwesenheit');
      const values = [...root().querySelectorAll('.absence-balance > div')].map(words);
      expect(values).toEqual(['Anspruch 32', 'Genommen 20', 'Beantragt 1', 'Rest 2026 11']);
      expect([...root().querySelectorAll('.flow-choice')].map(choice => choice.textContent?.trim())).toEqual([
        'Urlaub',
        'Krank',
        'Gleittag',
        'Meine Abwesenheiten',
      ]);
    });

    it('Zurück leaves the flow', () => {
      create();
      press('Zurück');

      expect(closed).toHaveBeenCalledOnce();
    });

    it('hides the account when vacation is not set up for the employee and names what is missing', () => {
      const missing = 'Urlaub ist noch nicht eingerichtet. Es fehlt: Urlaubstage pro Jahr des Mitarbeiters.';
      absenceOverview.mockReturnValue(of({
        ...ABSENCE_OVERVIEW,
        kinds: ABSENCE_OVERVIEW.kinds.map(kind => (kind.kind === 'vacation' ? { ...kind, missing } : kind)),
      }));
      create();

      expect(root().querySelector('.absence-balance')).toBeNull();
      press('Urlaub');

      expect(title()).toBe('Abwesenheit');
      expect(root().querySelector('.flow-error')?.textContent).toContain('Urlaubstage pro Jahr');
      press('Krank');
      expect(title()).toBe('Krank: Tage wählen');
    });

    it('reports a failed load and loads again on the next tap', () => {
      absenceOverview.mockReturnValueOnce(throwError(() => ({ status: 0 })));
      create();

      expect(root().querySelector('.flow-error')?.textContent).toContain('Keine Verbindung');
      press('Gleittag');

      expect(absenceOverview).toHaveBeenCalledTimes(2);
      expect(title()).toBe('Gleittag: Tage wählen');
      expect(root().querySelector('.flow-error')).toBeNull();
    });
  });

  describe('Tage wählen', () => {
    it('offers only days in the window of the kind and never a day already taken', () => {
      create();
      press('Krank');

      expect(title()).toBe('Krank: Tage wählen');
      expect(day('2026-10-16').disabled).toBe(false);
      // Already requested (vacation) and already sick: marked, not selectable.
      expect(day('2026-10-20').disabled).toBe(true);
      expect(day('2026-10-20').getAttribute('aria-label')).toContain('Urlaub (beantragt)');
      expect(day('2026-10-05').getAttribute('aria-label')).toContain('Krank (eingetragen)');
      // Sick reaches 14 days ahead: the 31.10. is outside.
      expect(day('2026-10-30').disabled).toBe(false);
      expect(day('2026-10-31').disabled).toBe(true);
      expect(button('Weiter')?.disabled).toBe(true);
    });

    it('shows the count and, for vacation, the rest after the chosen days', () => {
      create();
      press('Urlaub');
      expect(selection()).toBe('Bitte Tage antippen');

      tapDays('2026-10-26', '2026-10-27', '2026-10-28');

      expect(selection()).toBe('3 Tage gewählt · Rest danach 8');
      expect(button('Weiter')?.disabled).toBe(false);
    });

    it('warns above the rest and does not go on - the server would refuse it', () => {
      absenceOverview.mockReturnValue(of({
        ...ABSENCE_OVERVIEW,
        balances: [{ ...ABSENCE_OVERVIEW.balances[0], remaining: 1 }, ABSENCE_OVERVIEW.balances[1]],
      }));
      create();
      press('Urlaub');
      tapDays('2026-10-26', '2026-10-27');

      expect(selection()).toBe('2 Tage gewählt · Rest danach −1');
      expect(root().querySelector('.flow-warn')?.textContent).toContain('Mehr Tage als Resturlaub');
      expect(button('Weiter')?.disabled).toBe(true);
      fixture.componentInstance.next();
      expect(title()).toBe('Urlaub: Tage wählen');
    });

    it('counts the rest per year across the turn of the year', () => {
      create();
      press('Urlaub');
      vi.advanceTimersByTime(ABSENCE_TAP_GUARD_MS);
      // December, then January.
      for (let month = 0; month < 2; month++) {
        (root().querySelector('.nav[aria-label="Nächster Monat"]') as HTMLButtonElement).click();
        fixture.detectChanges();
      }
      tapDays('2026-12-31');
      (root().querySelector('.nav[aria-label="Nächster Monat"]') as HTMLButtonElement).click();
      fixture.detectChanges();
      tapDays('2027-01-04');

      expect(selection()).toBe('2 Tage gewählt · Rest 2026: 10 · Rest 2027: 29');
    });

    it('clears the days when another kind is chosen', () => {
      create();
      press('Urlaub');
      tapDays('2026-10-26');
      press('Zurück');
      press('Gleittag');

      expect(selection()).toBe('Bitte Tage antippen');
    });
  });

  describe('Zusammenfassung und Absenden', () => {
    function toSummary(kind: string, ...days: string[]): void {
      press(kind);
      tapDays(...days);
      press('Weiter');
    }

    it('shows kind, days compact with the count and the rest before and after', () => {
      create();
      toSummary('Urlaub', '2026-10-26', '2026-10-27', '2026-10-28', '2026-10-30');

      expect(title()).toBe('Zusammenfassung');
      expect(root().querySelector('.flow-subtitle')?.textContent?.trim()).toBe('Urlaub');
      const lines = [...root().querySelectorAll('.flow-line')].map(line => line.textContent?.trim());
      expect(lines).toEqual(['Mo 26.–Mi 28.10., Fr 30.10.', '4 Tage', '2026: 11 → 7']);
    });

    it('sends the days with the source and confirms - then the account is loaded again', () => {
      create();
      toSummary('Krank', '2026-10-15', '2026-10-16');
      absenceOverview.mockReturnValue(of({ ...ABSENCE_OVERVIEW, today: '2026-10-16' }));
      press('Absenden');

      expect(submitAbsence).toHaveBeenCalledExactlyOnceWith({
        ...auth,
        kind: 'sick',
        days: ['2026-10-15', '2026-10-16'],
        comment: null,
        source: 'term-1',
      });
      expect(root().querySelector('.flow-sent')?.textContent?.trim()).toBe('Antrag gesendet – wartet auf Freigabe');
      expect(absenceOverview).toHaveBeenCalledTimes(2);
      press('Fertig');
      expect(closed).toHaveBeenCalledOnce();
    });

    it('shows the new rest after a vacation request', () => {
      create();
      toSummary('Urlaub', '2026-10-26', '2026-10-27');
      absenceOverview.mockReturnValue(of({
        ...ABSENCE_OVERVIEW,
        balances: [{ ...ABSENCE_OVERVIEW.balances[0], planned: 3, remaining: 9 }, ABSENCE_OVERVIEW.balances[1]],
      }));
      press('Absenden');

      expect(root().querySelector('.absence-sent-rest')?.textContent?.trim()).toBe('Resturlaub 2026: 9');
    });

    it('shows the server message on 400 and stays in the summary', () => {
      submitAbsence.mockReturnValue(throwError(() => ({
        status: 400,
        error: { message: 'Am Tag mit Arbeit bitte ab dem Folgetag krankmelden.' },
      })));
      create();
      toSummary('Krank', '2026-10-16');
      press('Absenden');

      expect(title()).toBe('Zusammenfassung');
      expect(root().querySelector('.flow-error')?.textContent).toContain('ab dem Folgetag krankmelden');
      press('Zurück');
      expect(title()).toBe('Krank: Tage wählen');
      expect(root().querySelector('.flow-error')).toBeNull();
    });

    it('points to Meine Abwesenheiten when the answer is missing (the request may have arrived)', () => {
      submitAbsence.mockReturnValue(throwError(() => ({ status: 0 })));
      create();
      toSummary('Gleittag', '2026-10-23');
      press('Absenden');

      expect(root().querySelector('.flow-error')?.textContent).toContain('„Meine Abwesenheiten“ prüfen');
    });

    it('sends only once while the request runs', () => {
      submitAbsence.mockReturnValue(new Subject());
      create();
      toSummary('Gleittag', '2026-10-23');
      press('Absenden');
      fixture.componentInstance.submit();

      expect(submitAbsence).toHaveBeenCalledOnce();
      expect(button('Sendet …')?.disabled).toBe(true);
    });

    it('offers a comment only on /clock, trimmed and limited to 300 characters', () => {
      create({ layout: 'clock' });
      toSummary('Urlaub', '2026-10-26');
      const area = root().querySelector('textarea') as HTMLTextAreaElement;
      area.value = `  ${'x'.repeat(310)}`;
      area.dispatchEvent(new Event('input'));
      press('Absenden');

      expect(submitAbsence).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ comment: 'x'.repeat(298), source: 'clock' }));
    });

    it('has no comment field on the terminal - it has no keyboard', () => {
      create();
      toSummary('Urlaub', '2026-10-26');

      expect(root().querySelector('textarea')).toBeNull();
    });
  });

  describe('Doppeltipp (Issue #59)', () => {
    it('a second tap on Weiter right away does not send - it lands on Absenden', () => {
      create();
      press('Urlaub');
      tapDays('2026-10-26');
      press('Weiter');

      // Same place, second tap within the guard.
      button('Absenden')!.click();
      fixture.componentInstance.submit();
      fixture.detectChanges();
      expect(submitAbsence).not.toHaveBeenCalled();
      expect(fixture.nativeElement.classList).toContain('tap-guard');

      press('Absenden');
      expect(submitAbsence).toHaveBeenCalledOnce();
    });

    it('opening the flow does not take a tap on a kind right away', () => {
      create();
      fixture.componentInstance.chooseKind('vacation');
      fixture.detectChanges();

      expect(title()).toBe('Abwesenheit');
    });
  });

  describe('Meine Abwesenheiten', () => {
    it('lists status, days, count and the reason of a rejection', () => {
      myAbsences.mockReturnValue(of([
        absence({ id: 'p1' }),
        absence({ id: 'r1', kind: 'flexDay', status: 'rejected', days: ['2026-10-09'], decisionNote: 'Personal fehlt' }),
        absence({ id: 's1', kind: 'sick', status: 'applied', days: ['2026-10-05'], source: 'admin' }),
        absence({ id: 'f1', status: 'failed', error: 'Die Abwesenheit konnte nicht in Kimai eingetragen werden. Der Chef kümmert sich darum.' }),
      ]));
      create();
      press('Meine Abwesenheiten');

      expect(myAbsences).toHaveBeenCalledExactlyOnceWith(auth);
      expect(title()).toBe('Meine Abwesenheiten');
      const items = [...root().querySelectorAll('.flow-request')].map(words);
      expect(items[0]).toContain('Urlaub Wartet auf Freigabe Mo 26.–Di 27.10. 2 Tage');
      expect(items[1]).toContain('Gleittag Abgelehnt Fr 09.10. 1 Tag');
      expect(items[1]).toContain('Grund: Personal fehlt');
      expect(items[2]).toContain('Krank Eingetragen Mo 05.10. 1 Tag · vom Chef erfasst');
      expect(items[3]).toContain('Der Chef kümmert sich darum');
      expect(root().querySelectorAll('.flow-request button').length).toBe(1);
    });

    it('offers no Zurückziehen once the boss started to enter it', () => {
      myAbsences.mockReturnValue(of([absence({ applyStartedAt: '2026-10-16T08:00:00Z' })]));
      create();
      press('Meine Abwesenheiten');

      expect(text()).toContain('Wird eingetragen');
      expect(button('Zurückziehen')).toBeUndefined();
    });

    it('withdraws only after a confirmation and updates the account', () => {
      myAbsences.mockReturnValue(of([absence({ id: 'p1' })]));
      withdrawAbsence.mockReturnValue(of(absence({ id: 'p1', status: 'withdrawn' })));
      create();
      press('Meine Abwesenheiten');
      press('Zurückziehen');

      expect(withdrawAbsence).not.toHaveBeenCalled();
      expect(text()).toContain('Wirklich zurückziehen?');
      // A bouncing double tap hits "Ja" at once: nothing happens.
      button('Ja')!.click();
      expect(withdrawAbsence).not.toHaveBeenCalled();

      press('Ja');
      expect(withdrawAbsence).toHaveBeenCalledExactlyOnceWith(auth, 'p1');
      expect(text()).toContain('Zurückgezogen');
      expect(text()).toContain('Antrag zurückgezogen.');
      expect(button('Zurückziehen')).toBeUndefined();
      // Loaded on start, with the list and after withdrawing.
      expect(absenceOverview).toHaveBeenCalledTimes(3);
    });

    it('Nein keeps the request', () => {
      myAbsences.mockReturnValue(of([absence({ id: 'p1' })]));
      create();
      press('Meine Abwesenheiten');
      press('Zurückziehen');
      press('Nein');

      expect(withdrawAbsence).not.toHaveBeenCalled();
      expect(button('Zurückziehen')).toBeDefined();
    });

    it('shows the new state when the request was decided meanwhile (409)', () => {
      myAbsences.mockReturnValueOnce(of([absence({ id: 'p1' })]));
      myAbsences.mockReturnValueOnce(of([absence({ id: 'p1', status: 'applied' })]));
      withdrawAbsence.mockReturnValue(throwError(() => ({
        status: 409,
        error: { message: 'Der Antrag ist schon entschieden und kann nicht mehr zurückgezogen werden.' },
      })));
      create();
      press('Meine Abwesenheiten');
      press('Zurückziehen');
      press('Ja');

      expect(root().querySelector('.flow-error')?.textContent).toContain('schon entschieden');
      expect(text()).toContain('Eingetragen');
      expect(button('Zurückziehen')).toBeUndefined();
    });

    it('frees the days in calendar and account when the boss rejected meanwhile (409, review #142)', () => {
      const pending = absence({ id: 'p1', days: ['2026-10-20'] });
      const freed = {
        ...ABSENCE_OVERVIEW,
        balances: [{ ...ABSENCE_OVERVIEW.balances[0], planned: 0, remaining: 12 }, ABSENCE_OVERVIEW.balances[1]],
        days: ABSENCE_OVERVIEW.days.filter(day => day.day !== '2026-10-20'),
      };
      // Start and list still see the open request; the rejection lands before "Ja".
      absenceOverview.mockReturnValueOnce(of(ABSENCE_OVERVIEW)).mockReturnValueOnce(of(ABSENCE_OVERVIEW)).mockReturnValue(of(freed));
      myAbsences.mockReturnValueOnce(of([pending]));
      myAbsences.mockReturnValueOnce(of([{ ...pending, status: 'rejected' as const, decisionNote: 'Personal fehlt' }]));
      withdrawAbsence.mockReturnValue(throwError(() => ({ status: 409, error: { message: 'Der Antrag ist schon entschieden.' } })));
      create();
      press('Meine Abwesenheiten');
      press('Zurückziehen');
      press('Ja');
      expect(text()).toContain('Abgelehnt');

      press('Zurück');
      expect(words(root().querySelector('.absence-balance > .rest')!)).toBe('Rest 2026 12');
      press('Urlaub');
      expect(day('2026-10-20').disabled).toBe(false);
      expect(day('2026-10-20').getAttribute('aria-label')).not.toContain('beantragt');
    });

    it('loads the account again with the list - it may show decisions the account does not know yet', () => {
      create();
      absenceOverview.mockReturnValue(of({
        ...ABSENCE_OVERVIEW,
        days: ABSENCE_OVERVIEW.days.filter(day => day.day !== '2026-10-20'),
      }));
      press('Meine Abwesenheiten');
      press('Zurück');
      press('Urlaub');

      expect(absenceOverview).toHaveBeenCalledTimes(2);
      expect(day('2026-10-20').disabled).toBe(false);
    });

    it('says so when there are none', () => {
      create();
      press('Meine Abwesenheiten');

      expect(text()).toContain('Keine Abwesenheiten in den letzten 12 Monaten.');
    });
  });

  describe('verspätete Antworten', () => {
    it('drops an account that arrives after the identity changed', () => {
      const late = new Subject<KioskAbsenceOverview>();
      absenceOverview.mockReturnValue(late);
      create();

      fixture.componentRef.setInput('auth', { employeeId: 'anna', pin: '4321', nfcCardId: null });
      fixture.detectChanges();
      late.next(ABSENCE_OVERVIEW);
      fixture.detectChanges();

      expect(root().querySelector('.absence-balance')).toBeNull();
    });

    it('drops the list when the employee already left the step', () => {
      const late = new Subject<never[]>();
      myAbsences.mockReturnValue(late);
      create();
      press('Meine Abwesenheiten');
      press('Zurück');

      late.next([]);
      fixture.detectChanges();
      expect(title()).toBe('Abwesenheit');
    });

    it('stops its requests with the component', () => {
      const pending = new Subject<KioskAbsenceOverview>();
      absenceOverview.mockReturnValue(pending);
      create();

      fixture.destroy();
      expect(pending.observed).toBe(false);
    });
  });

  describe('Inaktivität', () => {
    it('hands over to the idle screen after 2 minutes without a tap', () => {
      create();
      vi.advanceTimersByTime(ABSENCE_IDLE_MS - 1);
      expect(timedOut).not.toHaveBeenCalled();

      vi.advanceTimersByTime(1);
      expect(timedOut).toHaveBeenCalledOnce();
    });

    it('every tap starts the 2 minutes anew', () => {
      create();
      vi.advanceTimersByTime(ABSENCE_IDLE_MS - 1000);
      press('Urlaub');
      vi.advanceTimersByTime(ABSENCE_IDLE_MS - 1000);

      expect(timedOut).not.toHaveBeenCalled();
    });
  });
});
