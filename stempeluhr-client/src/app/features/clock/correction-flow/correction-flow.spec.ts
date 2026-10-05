import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, Subject, throwError } from 'rxjs';
import type { Mock } from 'vitest';

import { CorrectionAuth, CorrectionTimesheets, EmployeeTask, KioskCorrection } from '../../../core/models/kiosk.models';
import { KioskApi } from '../../../core/services/kiosk-api';
import { CORRECTION_IDLE_MS, CorrectionFlow, CorrectionStart } from './correction-flow';
import { CORRECTION_NOW, CORRECTION_TIMESHEETS, correction } from './correction-fixtures';

describe('CorrectionFlow', () => {
  const auth: CorrectionAuth = { employeeId: 'max', pin: '1234', nfcCardId: null };

  let correctionTimesheets: ReturnType<typeof vi.fn>;
  let submitCorrection: ReturnType<typeof vi.fn>;
  let myCorrections: ReturnType<typeof vi.fn>;
  let withdrawCorrection: ReturnType<typeof vi.fn>;
  let fixture: ComponentFixture<CorrectionFlow>;
  let closed: Mock<() => void>;
  let timedOut: Mock<() => void>;

  beforeEach(async () => {
    correctionTimesheets = vi.fn(() => of(CORRECTION_TIMESHEETS));
    submitCorrection = vi.fn(() => of(correction()));
    myCorrections = vi.fn(() => of([]));
    withdrawCorrection = vi.fn(() => of(correction({ status: 'withdrawn' })));
    closed = vi.fn<() => void>();
    timedOut = vi.fn<() => void>();

    await TestBed.configureTestingModule({
      imports: [CorrectionFlow],
      providers: [{ provide: KioskApi, useValue: { correctionTimesheets, submitCorrection, myCorrections, withdrawCorrection } }],
    }).compileComponents();

    vi.useFakeTimers();
    vi.setSystemTime(CORRECTION_NOW);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  function create(options: {
    layout?: 'terminal' | 'clock';
    tasks?: EmployeeTask[];
    start?: CorrectionStart | null;
    auth?: CorrectionAuth;
  } = {}): void {
    fixture = TestBed.createComponent(CorrectionFlow);
    fixture.componentRef.setInput('auth', options.auth ?? auth);
    fixture.componentRef.setInput('layout', options.layout ?? 'terminal');
    fixture.componentRef.setInput('source', options.layout === 'clock' ? 'clock' : 'term-1');
    fixture.componentRef.setInput('tasks', options.tasks ?? []);
    fixture.componentRef.setInput('defaultTaskLabel', 'Büro');
    fixture.componentRef.setInput('start', options.start ?? null);
    fixture.componentInstance.closed.subscribe(() => closed());
    fixture.componentInstance.timedOut.subscribe(() => timedOut());
    fixture.detectChanges();
  }

  const root = () => fixture.nativeElement as HTMLElement;
  const title = () => root().querySelector('.flow-title')?.textContent?.trim();

  function press(text: string): void {
    const button = ([...root().querySelectorAll('button')] as HTMLButtonElement[]).find(candidate => candidate.textContent?.trim() === text);
    expect(button, `Knopf "${text}"`).toBeDefined();
    button!.click();
    fixture.detectChanges();
  }

  function pressSelector(selector: string): void {
    (root().querySelector(selector) as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  function pressEntry(range: string): void {
    const entry = ([...root().querySelectorAll('.flow-entry')] as HTMLButtonElement[]).find(candidate => candidate.textContent?.includes(range));
    expect(entry, `Eintrag ${range}`).toBeDefined();
    entry!.click();
    fixture.detectChanges();
  }

  function stepper(): string[] {
    return [...root().querySelectorAll('app-time-stepper .value')].map(element => element.textContent?.trim() ?? '');
  }

  const lines = () => [...root().querySelectorAll('.flow-line')].map(element => element.textContent?.trim());

  describe('Art und Eintrag wählen', () => {
    it('offers the four kinds and Meine Anträge, and Zurück leaves the flow', () => {
      create();

      expect(title()).toBe('Korrektur');
      expect([...root().querySelectorAll('.flow-choice')].map(button => button.textContent?.trim())).toEqual([
        'Pause nachtragen',
        'Ausstempeln nachtragen',
        'Schicht nachtragen',
        'Zeiten ändern',
        'Meine Anträge',
      ]);

      press('Zurück');
      expect(closed).toHaveBeenCalledOnce();
    });

    it('lists shifts with their entries, newest first, a night shift as ONE shift over midnight', () => {
      create();
      press('Zeiten ändern');

      expect(correctionTimesheets).toHaveBeenCalledWith(auth);
      const headings = [...root().querySelectorAll('.flow-shift h3')].map(heading => heading.textContent?.trim());
      expect(headings).toEqual([
        'Schicht Mo 05.10. 07:00 – läuft',
        'Schicht Sa 03.10. 22:00 – So 04.10. 06:00',
        'Schicht Mi 30.09. 08:00–16:30',
      ]);
    });

    it('offers only stopped work for a pause - and never an entry with an open request', () => {
      create();
      press('Pause nachtragen');

      const entries = [...root().querySelectorAll('.flow-entry')].map(entry => [...entry.children].map(child => child.textContent?.trim()).join(' '));
      // Not the pause (22), not the running entry (23), not the one with an open request (13).
      expect(entries).toEqual([
        'Arbeit Mo 05.10. 07:00–11:00',
        'Nachtdienst Sa 03.10. 22:00 – So 04.10. 02:00',
        'Kunde X Mi 30.09. 08:00–16:30',
      ]);
    });

    it('offers stopped pauses as well for the end and times, but still no running or requested entry', () => {
      create();
      press('Ausstempeln nachtragen');

      const entries = [...root().querySelectorAll('.flow-entry')].map(entry => [...entry.children].map(child => child.textContent?.trim()).join(' '));
      expect(entries).toContain('Pause Mo 05.10. 11:00–11:30');
      expect(entries.some(entry => entry?.includes('läuft'))).toBe(false);
      expect(entries.some(entry => entry?.includes('02:00–06:00'))).toBe(false);
    });

    it('says so when nothing fits', () => {
      correctionTimesheets.mockReturnValue(of({ timeZone: 'Europe/Berlin', shifts: [] } satisfies CorrectionTimesheets));
      create();
      press('Pause nachtragen');

      expect(root().querySelector('.flow-note')?.textContent).toContain('Keine passenden Einträge');
    });

    it('stays on the kinds with a message when the list cannot be loaded', () => {
      correctionTimesheets.mockReturnValue(throwError(() => ({ status: 503, error: { message: 'Kimai ist gerade nicht erreichbar. Bitte später erneut versuchen.' } })));
      create();
      press('Pause nachtragen');

      expect(title()).toBe('Korrektur');
      expect(root().querySelector('.flow-error')?.textContent).toContain('Kimai ist gerade nicht erreichbar');
    });

    it('explains a timeout instead of a raw error', () => {
      correctionTimesheets.mockReturnValue(throwError(() => new Error('Timeout has occurred')));
      create();
      press('Pause nachtragen');

      expect(root().querySelector('.flow-error')?.textContent).toContain('Keine Verbindung zum Server');
    });
  });

  describe('Ablauf je Art bis Absenden', () => {
    it('addPause: pause of 30 minutes in the middle of the entry, request body with local times', () => {
      create();
      press('Pause nachtragen');
      pressEntry('07:00–11:00');

      // Eintrag 07:00-11:00: Mitte 09:00, Pause 08:45-09:15.
      expect(title()).toBe('Pause beginnt');
      expect(stepper()).toEqual(['Mo 05.10.', '08', '45']);
      press('Weiter');
      expect(title()).toBe('Pause endet');
      expect(stepper()).toEqual(['Mo 05.10.', '09', '15']);
      press('Weiter');

      expect(title()).toBe('Zusammenfassung');
      expect(lines()).toEqual([
        'Arbeit Mo 05.10. 07:00–11:00',
        'Arbeit Mo 05.10. 07:00–08:45',
        'Pause Mo 05.10. 08:45–09:15',
        'Arbeit Mo 05.10. 09:15–11:00',
      ]);
      press('Absenden');

      expect(submitCorrection).toHaveBeenCalledExactlyOnceWith({
        employeeId: 'max', pin: '1234', nfcCardId: null,
        kind: 'addPause', timesheetId: 21,
        begin: null, end: null, pauseBegin: '2026-10-05T08:45', pauseEnd: '2026-10-05T09:15',
        taskId: null, comment: null, source: 'term-1',
      });
      expect(root().querySelector('.flow-sent')?.textContent).toBe('Antrag gesendet – wartet auf Freigabe');
    });

    it('addPause: moving the pause start keeps its length and never leaves the entry', () => {
      create();
      press('Pause nachtragen');
      pressEntry('07:00–11:00');

      pressSelector('.hour-later');
      pressSelector('.hour-later');
      pressSelector('.hour-later');
      // 08:45 + 3 h = 11:45 would leave the entry (ends 11:00): the button locks at 10:45.
      expect(stepper()).toEqual(['Mo 05.10.', '10', '45']);
      press('Weiter');
      // The pause end is clamped to the end of the entry.
      expect(stepper()).toEqual(['Mo 05.10.', '11', '00']);
    });

    it('setEnd across midnight: a night shift end on the next day', () => {
      create();
      press('Ausstempeln nachtragen');
      pressEntry('22:00 – So 04.10. 02:00');

      // Kein 8-h-Eintrag: die Mitte (00:00 am Folgetag) ist der Vorschlag.
      expect(title()).toBe('Ende');
      expect(stepper()).toEqual(['So 04.10.', '00', '00']);
      pressSelector('.hour-later');
      pressSelector('.minute-later-5');
      press('Weiter');

      expect(lines()).toEqual([
        'Nachtdienst Sa 03.10. 22:00 – So 04.10. 02:00',
        'Nachtdienst Sa 03.10. 22:00 – So 04.10. 01:05',
      ]);
      press('Absenden');

      expect(submitCorrection).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({
        kind: 'setEnd', timesheetId: 12, begin: null, end: '2026-10-04T01:05', pauseBegin: null, pauseEnd: null,
      }));
    });

    it('setEnd: the end cannot reach the old end (that would be no change)', () => {
      create();
      press('Ausstempeln nachtragen');
      pressEntry('22:00 – So 04.10. 02:00');

      pressSelector('.hour-later');
      pressSelector('.hour-later');

      // Alter Ende 02:00: spätestens 01:59.
      expect(stepper()).toEqual(['So 04.10.', '01', '00']);
      expect((root().querySelector('.hour-later') as HTMLButtonElement).disabled).toBe(true);
    });

    it('changeTimes: sends only the time that changed', () => {
      create();
      press('Zeiten ändern');
      pressEntry('07:00–11:00');

      expect(title()).toBe('Beginn');
      expect(stepper()).toEqual(['Mo 05.10.', '07', '00']);
      pressSelector('.minute-earlier-5');
      press('Weiter');
      expect(title()).toBe('Ende');
      press('Weiter');

      expect(lines()).toEqual(['Arbeit Mo 05.10. 07:00–11:00', 'Arbeit Mo 05.10. 06:55–11:00']);
      press('Absenden');

      expect(submitCorrection).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({
        kind: 'changeTimes', timesheetId: 21, begin: '2026-10-05T06:55', end: null,
      }));
    });

    it('changeTimes: without a change there is nothing to send', () => {
      create();
      press('Zeiten ändern');
      pressEntry('07:00–11:00');
      press('Weiter');

      expect(root().querySelector('.flow-note')?.textContent).toContain('Keine Änderung');
      const next = ([...root().querySelectorAll('button')] as HTMLButtonElement[]).find(button => button.textContent?.trim() === 'Weiter');
      expect(next?.disabled).toBe(true);
    });

    it('addShift on a further task with a pause: the last eight hours as a start', () => {
      create({ tasks: [{ id: 'kx', label: 'Kunde X' }] });
      press('Schicht nachtragen');

      expect(title()).toBe('Tätigkeit wählen');
      expect([...root().querySelectorAll('.flow-choice')].map(button => button.textContent?.trim())).toEqual(['Büro', 'Kunde X']);
      press('Kunde X');

      expect(title()).toBe('Beginn');
      expect(stepper()).toEqual(['Mo 05.10.', '08', '00']);
      press('Weiter');
      expect(title()).toBe('Ende');
      expect(stepper()).toEqual(['Mo 05.10.', '16', '00']);
      press('Weiter');

      expect(title()).toBe('Pause eintragen?');
      press('Mit Pause');
      expect(title()).toBe('Pause beginnt');
      expect(stepper()).toEqual(['Mo 05.10.', '11', '45']);
      press('Weiter');
      expect(stepper()).toEqual(['Mo 05.10.', '12', '15']);
      press('Weiter');

      expect(lines()).toEqual([
        'kein Eintrag',
        'Kunde X Mo 05.10. 08:00–11:45',
        'Pause Mo 05.10. 11:45–12:15',
        'Kunde X Mo 05.10. 12:15–16:00',
      ]);
      press('Absenden');

      expect(submitCorrection).toHaveBeenCalledExactlyOnceWith({
        employeeId: 'max', pin: '1234', nfcCardId: null,
        kind: 'addShift', timesheetId: null,
        begin: '2026-10-05T08:00', end: '2026-10-05T16:00',
        pauseBegin: '2026-10-05T11:45', pauseEnd: '2026-10-05T12:15',
        taskId: 'kx', comment: null, source: 'term-1',
      });
    });

    it('addShift without further tasks skips the task step and books the main task', () => {
      create();
      press('Schicht nachtragen');

      expect(title()).toBe('Beginn');
      press('Weiter');
      press('Weiter');
      press('Ohne Pause');
      expect(lines()).toEqual(['kein Eintrag', 'Büro Mo 05.10. 08:00–16:00']);
      press('Absenden');

      expect(submitCorrection).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({
        kind: 'addShift', timesheetId: null, begin: '2026-10-05T08:00', end: '2026-10-05T16:00',
        pauseBegin: null, pauseEnd: null, taskId: null,
      }));
    });

    it('addShift: the shift stays within 16 hours and cannot end in the future', () => {
      create();
      press('Schicht nachtragen');
      press('Weiter');

      // Ende 16:00 = jetzt: kein weiterer Schritt in die Zukunft.
      expect((root().querySelector('.hour-later') as HTMLButtonElement).disabled).toBe(true);
      expect((root().querySelector('.minute-later-1') as HTMLButtonElement).disabled).toBe(true);
    });

    describe('Zeiten ändern bei einem Eintrag über 16 Stunden (vergessenes Ausstempeln)', () => {
      const long: CorrectionTimesheets = {
        timeZone: 'Europe/Berlin',
        shifts: [{
          begin: '2026-10-04T08:00',
          end: '2026-10-05T04:00',
          entries: [{ id: 31, begin: '2026-10-04T08:00', end: '2026-10-05T04:00', kind: 'work', label: 'Arbeit', hasOpenRequest: false }],
        }],
      };

      const next = () => ([...root().querySelectorAll('button')] as HTMLButtonElement[]).find(button => button.textContent?.trim() === 'Weiter')!;
      const absenden = () => ([...root().querySelectorAll('button')] as HTMLButtonElement[]).find(button => button.textContent?.trim() === 'Absenden')!;
      const disabled = (selector: string) => (root().querySelector(selector) as HTMLButtonElement).disabled;

      beforeEach(() => {
        correctionTimesheets.mockReturnValue(of(long));
        create();
        press('Zeiten ändern');
        pressEntry('08:00 – Mo 05.10. 04:00');
      });

      it('says why and lets the begin move towards the range until it fits', () => {
        expect(title()).toBe('Beginn');
        expect(stepper()).toEqual(['So 04.10.', '08', '00']);
        expect(root().querySelector('.flow-warn')?.textContent).toContain('höchstens 16 Stunden');
        // Begin starts 4 h below its minimum (end - 16 h = 12:00): only later is possible.
        expect(disabled('.hour-earlier')).toBe(true);
        expect(disabled('.hour-later')).toBe(false);

        for (let i = 0; i < 4; i++) {
          pressSelector('.hour-later');
        }
        expect(stepper()).toEqual(['So 04.10.', '12', '00']);
        expect(root().querySelector('.flow-warn')).toBeNull();

        press('Weiter');
        expect(title()).toBe('Ende');
        expect(next().disabled).toBe(false);
        press('Weiter');
        expect(lines()).toEqual(['Arbeit So 04.10. 08:00 – Mo 05.10. 04:00', 'Arbeit So 04.10. 12:00 – Mo 05.10. 04:00']);
        expect(absenden().disabled).toBe(false);
        absenden().click();

        expect(submitCorrection).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({
          kind: 'changeTimes', timesheetId: 31, begin: '2026-10-04T12:00', end: null,
        }));
      });

      it('or the end is shortened instead - Weiter at the last field waits until it fits', () => {
        // The begin stays: Weiter is allowed on the first field.
        expect(next().disabled).toBe(false);
        press('Weiter');

        expect(title()).toBe('Ende');
        expect(stepper()).toEqual(['Mo 05.10.', '04', '00']);
        // End is 4 h above its maximum (begin + 16 h = 00:00): only earlier is possible.
        expect(disabled('.hour-later')).toBe(true);
        expect(disabled('.hour-earlier')).toBe(false);
        expect(next().disabled).toBe(true);
        expect(root().querySelector('.flow-warn')?.textContent).toContain('höchstens 16 Stunden');

        for (let i = 0; i < 4; i++) {
          pressSelector('.hour-earlier');
        }
        expect(stepper()).toEqual(['Mo 05.10.', '00', '00']);
        expect(next().disabled).toBe(false);
        press('Weiter');
        absenden().click();

        expect(submitCorrection).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({
          kind: 'changeTimes', timesheetId: 31, begin: null, end: '2026-10-05T00:00',
        }));
      });

      it('cannot be sent unchanged', () => {
        press('Weiter');
        expect(next().disabled).toBe(true);
        fixture.componentInstance.submit();
        expect(submitCorrection).not.toHaveBeenCalled();
      });
    });

    it('Zurück walks back through the steps and keeps the entered times', () => {
      create();
      press('Zeiten ändern');
      pressEntry('07:00–11:00');
      pressSelector('.minute-earlier-5');
      press('Weiter');
      expect(title()).toBe('Ende');

      press('Zurück');
      expect(title()).toBe('Beginn');
      expect(stepper()).toEqual(['Mo 05.10.', '06', '55']);
      press('Zurück');
      expect(title()).toBe('Eintrag wählen');
      press('Zurück');
      expect(title()).toBe('Korrektur');
    });
  });

  describe('Kommentar', () => {
    function toSummary(): void {
      press('Ausstempeln nachtragen');
      pressEntry('22:00 – So 04.10. 02:00');
      press('Weiter');
    }

    it('is only offered on /clock, trimmed and limited to 300 characters', () => {
      create({ layout: 'clock' });
      toSummary();

      const area = root().querySelector('textarea') as HTMLTextAreaElement;
      expect(area.getAttribute('maxlength')).toBe('300');
      area.value = `  ${'x'.repeat(320)}  `;
      area.dispatchEvent(new Event('input'));
      fixture.detectChanges();
      expect(root().querySelector('.flow-comment small')?.textContent).toBe('300/300');
      area.value = '  Bitte prüfen  ';
      area.dispatchEvent(new Event('input'));
      press('Absenden');

      expect(submitCorrection).toHaveBeenCalledWith(expect.objectContaining({ comment: 'Bitte prüfen', source: 'clock' }));
    });

    it('is not shown on the terminal - it has no keyboard - and never sent', () => {
      create({ layout: 'terminal' });
      toSummary();

      expect(root().querySelector('textarea')).toBeNull();
      press('Absenden');
      expect(submitCorrection).toHaveBeenCalledWith(expect.objectContaining({ comment: null, source: 'term-1' }));
    });
  });

  describe('Ergebnis', () => {
    function toSummary(): void {
      press('Ausstempeln nachtragen');
      pressEntry('22:00 – So 04.10. 02:00');
      press('Weiter');
    }

    it('shows the server message on 400 and stays in the summary to correct', () => {
      submitCorrection.mockReturnValue(throwError(() => ({
        status: 400, error: { message: 'Der Zeitraum überschneidet sich mit einem anderen Eintrag.' },
      })));
      create();
      toSummary();
      press('Absenden');

      expect(root().querySelector('.flow-error')?.textContent).toContain('Der Zeitraum überschneidet sich');
      expect(title()).toBe('Zusammenfassung');
      expect(root().querySelector('.flow-sent')).toBeNull();

      // Corrected: back to the time, then send again.
      press('Zurück');
      expect(root().querySelector('.flow-error')).toBeNull();
      press('Weiter');
      submitCorrection.mockReturnValue(of(correction()));
      press('Absenden');
      expect(root().querySelector('.flow-sent')).not.toBeNull();
    });

    it('sends only once while the request runs', () => {
      const pending = new Subject<KioskCorrection>();
      submitCorrection.mockReturnValue(pending);
      create();
      toSummary();

      press('Absenden');
      const send = ([...root().querySelectorAll('button')] as HTMLButtonElement[]).find(button => button.textContent?.includes('Sendet'));
      expect(send?.disabled).toBe(true);
      fixture.componentInstance.submit();

      expect(submitCorrection).toHaveBeenCalledTimes(1);
    });

    it('points to Meine Anträge when the answer is missing (the request may have arrived)', () => {
      submitCorrection.mockReturnValue(throwError(() => ({ status: 0 })));
      create();
      toSummary();
      press('Absenden');

      expect(root().querySelector('.flow-error')?.textContent).toContain('Meine Anträge');
    });

    it('Fertig after the result returns to the session', () => {
      create();
      toSummary();
      press('Absenden');
      press('Fertig');

      expect(closed).toHaveBeenCalledOnce();
    });
  });

  describe('verspätete Antworten', () => {
    it('drops the list when the employee already left the step', () => {
      const pending = new Subject<CorrectionTimesheets>();
      correctionTimesheets.mockReturnValue(pending);
      create();
      press('Pause nachtragen');
      expect(root().querySelector('.flow-note')?.textContent).toContain('Lädt');

      press('Zurück');
      pending.next(CORRECTION_TIMESHEETS);
      fixture.detectChanges();

      expect(closed).toHaveBeenCalledOnce();
      expect(fixture.componentInstance.timesheets()).toBeNull();
      expect(title()).toBe('Korrektur');
    });

    it('drops an answer that arrives after the identity changed', () => {
      const pending = new Subject<CorrectionTimesheets>();
      correctionTimesheets.mockReturnValue(pending);
      create();
      press('Pause nachtragen');

      fixture.componentRef.setInput('auth', { employeeId: 'anna', pin: '9999', nfcCardId: null });
      pending.next(CORRECTION_TIMESHEETS);
      fixture.detectChanges();

      expect(fixture.componentInstance.timesheets()).toBeNull();
      expect(root().querySelector('.flow-entry')).toBeNull();
    });

    it('stops its requests with the component', () => {
      const pending = new Subject<CorrectionTimesheets>();
      correctionTimesheets.mockReturnValue(pending);
      create();
      press('Pause nachtragen');
      expect(pending.observed).toBe(true);

      fixture.destroy();

      expect(pending.observed).toBe(false);
    });
  });

  describe('Meine Anträge', () => {
    const mine = [
      correction({ id: 'p1' }),
      correction({
        id: 'r1', kind: 'addShift', status: 'rejected', timesheetId: null, original: null, begin: '2026-10-02T08:00', end: '2026-10-02T16:00',
        decisionNote: 'Schicht war im Dienstplan nicht vorgesehen',
      }),
      correction({ id: 'f1', status: 'failed', error: 'Der Antrag konnte nicht in Kimai gebucht werden. Der Chef kümmert sich darum.' }),
      correction({ id: 'a1', status: 'applied' }),
      correction({ id: 'w1', status: 'withdrawn' }),
    ];

    it('shows the status, before and after, and the reason of a rejection', () => {
      myCorrections.mockReturnValue(of(mine));
      create();
      press('Meine Anträge');

      expect(myCorrections).toHaveBeenCalledWith(auth);
      expect(title()).toBe('Meine Anträge');
      const status = [...root().querySelectorAll('.flow-status')].map(element => element.textContent?.trim());
      expect(status).toEqual(['Wartet auf Freigabe', 'Abgelehnt', 'Fehlgeschlagen', 'Eingetragen', 'Zurückgezogen']);
      const first = root().querySelector('.flow-request') as HTMLElement;
      expect(first.textContent).toContain('Ausstempeln nachtragen');
      expect(first.textContent).toContain('Arbeit Mo 05.10. 07:00–11:00');
      expect(first.textContent).toContain('Arbeit Mo 05.10. 07:00–10:00');
      expect(root().textContent).toContain('Grund: Schicht war im Dienstplan nicht vorgesehen');
      expect(root().textContent).toContain('Der Chef kümmert sich darum');
    });

    it('offers Zurückziehen only for open requests and withdraws one', () => {
      myCorrections.mockReturnValue(of(mine));
      withdrawCorrection.mockReturnValue(of(correction({ id: 'p1', status: 'withdrawn' })));
      create();
      press('Meine Anträge');

      const buttons = [...root().querySelectorAll('.flow-request .flow-button')] as HTMLButtonElement[];
      expect(buttons.map(button => button.textContent?.trim())).toEqual(['Zurückziehen']);

      buttons[0].click();
      fixture.detectChanges();

      expect(withdrawCorrection).toHaveBeenCalledExactlyOnceWith(auth, 'p1');
      expect(root().querySelector('.flow-request .flow-button')).toBeNull();
      expect(root().textContent).toContain('Antrag zurückgezogen.');
      expect([...root().querySelectorAll('.flow-status')][0].textContent?.trim()).toBe('Zurückgezogen');
    });

    it('shows the new state when the request was decided meanwhile (409)', () => {
      myCorrections.mockReturnValueOnce(of(mine));
      withdrawCorrection.mockReturnValue(throwError(() => ({
        status: 409, error: { message: 'Der Antrag ist schon entschieden und kann nicht mehr zurückgezogen werden.' },
      })));
      create();
      press('Meine Anträge');
      myCorrections.mockReturnValue(of([correction({ id: 'p1', status: 'applied' })]));

      (root().querySelector('.flow-request .flow-button') as HTMLButtonElement).click();
      fixture.detectChanges();

      expect(root().querySelector('.flow-error')?.textContent).toContain('schon entschieden');
      expect(myCorrections).toHaveBeenCalledTimes(2);
      expect(root().querySelector('.flow-request .flow-button')).toBeNull();
      expect([...root().querySelectorAll('.flow-status')].map(element => element.textContent?.trim())).toEqual(['Eingetragen']);
    });

    it('says so when there are none', () => {
      create();
      press('Meine Anträge');

      expect(root().querySelector('.flow-note')?.textContent).toContain('Keine Anträge');
    });
  });

  describe('Vergessen auszustempeln', () => {
    it('opens "Ausstempeln nachtragen" for exactly the given timesheet, back leads to the kinds', () => {
      create({ start: { kind: 'setEnd', timesheetId: 12 } });

      expect(correctionTimesheets).toHaveBeenCalledOnce();
      expect(title()).toBe('Ende');
      expect(root().querySelector('.flow-context')?.textContent).toContain('Nachtdienst Sa 03.10. 22:00 – So 04.10. 02:00');
      press('Zurück');
      expect(title()).toBe('Korrektur');
    });

    it('reports a timesheet that is gone or already has a request', () => {
      create({ start: { kind: 'setEnd', timesheetId: 999 } });
      expect(root().querySelector('.flow-error')?.textContent).toContain('nicht gefunden');
      expect(title()).toBe('Korrektur');
    });

    it('reports a timesheet with an open request', () => {
      create({ start: { kind: 'setEnd', timesheetId: 13 } });
      expect(root().querySelector('.flow-error')?.textContent).toContain('offenen Antrag');
    });
  });

  describe('Inaktivität', () => {
    it('hands over to the idle screen after 2 minutes without a tap', () => {
      create();

      vi.advanceTimersByTime(CORRECTION_IDLE_MS - 1);
      expect(timedOut).not.toHaveBeenCalled();
      vi.advanceTimersByTime(1);
      expect(timedOut).toHaveBeenCalledOnce();
    });

    it('every tap starts the 2 minutes anew', () => {
      create();
      vi.advanceTimersByTime(CORRECTION_IDLE_MS - 1000);

      root().dispatchEvent(new Event('pointerdown'));
      vi.advanceTimersByTime(CORRECTION_IDLE_MS - 1000);
      expect(timedOut).not.toHaveBeenCalled();

      press('Pause nachtragen');
      vi.advanceTimersByTime(CORRECTION_IDLE_MS - 1);
      expect(timedOut).not.toHaveBeenCalled();
      vi.advanceTimersByTime(1);
      expect(timedOut).toHaveBeenCalledOnce();
    });

    it('stops the timer with the component', () => {
      create();
      fixture.destroy();

      vi.advanceTimersByTime(CORRECTION_IDLE_MS * 2);
      expect(timedOut).not.toHaveBeenCalled();
    });
  });
});
