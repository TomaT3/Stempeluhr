import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import { signal } from '@angular/core';
import { of, Subject, throwError } from 'rxjs';

import {
  ClockStatus,
  HoursOverview,
  KioskEmployeeSession,
  NfcClockEvent,
  WorkTimeHint,
  WorkTimeHints,
} from '../../../core/models/kiosk.models';
import { RejectedOfflineStamp } from '../../../core/models/offline.models';
import { AudioFeedback } from '../../../core/services/audio-feedback';
import { lastKnownStatus, rememberProjectedStatus } from '../../../core/services/offline-cache';
import { KioskApi } from '../../../core/services/kiosk-api';
import { LocalNfcScan, LocalNfcScanService } from '../../../core/services/local-nfc-scan.service';
import { OfflineQueueService } from '../../../core/services/offline-queue';
import { CORRECTION_IDLE_MS } from '../../clock/correction-flow/correction-flow';
import { CORRECTION_NOW, CORRECTION_TIMESHEETS, correction } from '../../clock/correction-flow/correction-fixtures';
import { TerminalPage } from './terminal-page';

describe('TerminalPage', () => {
  let pinLogin: ReturnType<typeof vi.fn>;
  let pinLoginResult: Subject<KioskEmployeeSession>;
  let hoursOverview: ReturnType<typeof vi.fn>;
  let workTimeHints: ReturnType<typeof vi.fn>;
  let clockImpl: ReturnType<typeof vi.fn>;
  let failPolls: boolean;
  let localScanValue: LocalNfcScan | null;
  let enqueueKiosk: ReturnType<typeof vi.fn>;
  let acknowledgeRejected: ReturnType<typeof vi.fn>;
  let pendingQueue: ReturnType<typeof signal<unknown[]>>;
  let rejectedStamps: ReturnType<typeof signal<RejectedOfflineStamp[]>>;
  let recovered$: Subject<void>;
  let correctionTimesheets: ReturnType<typeof vi.fn>;
  let submitCorrection: ReturnType<typeof vi.fn>;
  let myCorrections: ReturnType<typeof vi.fn>;
  let withdrawCorrection: ReturnType<typeof vi.fn>;

  const status: ClockStatus = {
    isRunning: false,
    activeTimesheetId: null,
    startedAt: null,
    durationSeconds: 0,
    state: 'clockedOut',
    stateText: 'Nicht eingestempelt',
  };

  const session: KioskEmployeeSession = {
    employee: {
      id: 'max',
      displayName: 'Max Mustermann',
      initials: 'MM',
      color: '#123456',
      imageUrl: null,
      requiresPin: true,
    },
    status,
  };

  const overview: HoursOverview = {
    todaySeconds: 28800,
    todayPauseSeconds: 2700,
    weekSeconds: 72000,
    monthSeconds: 180000,
  };

  beforeEach(async () => {
    window.localStorage.clear();
    pinLoginResult = new Subject<KioskEmployeeSession>();
    hoursOverview = vi.fn(() => of(overview));
    workTimeHints = vi.fn(() => of({ timeZone: 'Europe/Berlin', hints: [] }));
    pinLogin = vi.fn(() => pinLoginResult);
    clockImpl = vi.fn(() => throwError(() => ({ status: 0 })));
    failPolls = false;
    localScanValue = null;
    enqueueKiosk = vi.fn();
    acknowledgeRejected = vi.fn();
    pendingQueue = signal<unknown[]>([]);
    rejectedStamps = signal<RejectedOfflineStamp[]>([]);
    recovered$ = new Subject<void>();
    correctionTimesheets = vi.fn(() => of(CORRECTION_TIMESHEETS));
    submitCorrection = vi.fn(() => of(correction()));
    myCorrections = vi.fn(() => of([]));
    withdrawCorrection = vi.fn(() => of(correction({ status: 'withdrawn' })));

    await TestBed.configureTestingModule({
      imports: [TerminalPage],
      providers: [
        {
          provide: KioskApi,
          useValue: {
            pinLogin,
            clock: clockImpl,
            hoursOverview,
            workTimeHints,
            ping: vi.fn(() =>
              failPolls ? throwError(() => ({ status: 0 })) : of({ ok: true, version: null, configuredEmployees: 0, settingsConfigured: true }),
            ),
            identify: vi.fn(() => new Subject<NfcClockEvent>()),
            health: vi.fn(() => of({ ok: true, version: null, configuredEmployees: 0, settingsConfigured: true })),
            correctionTimesheets,
            submitCorrection,
            myCorrections,
            withdrawCorrection,
          },
        },
        { provide: AudioFeedback, useValue: { playBeeps: vi.fn() } },
        {
          provide: LocalNfcScanService,
          useValue: {
            poll: vi.fn(() => of(localScanValue)),
            refreshCatalog: vi.fn(() => of(null)), ack: vi.fn(() => of(null)),
          },
        },
        {
          provide: OfflineQueueService,
          useValue: {
            authorizeEmployee: vi.fn(),
            authorizeEmployeeCard: vi.fn(),
            needsPin: signal(false),
            enqueueKiosk,
            syncNow: vi.fn(() => of([])),
            recovered: recovered$.asObservable(),
            rejected: rejectedStamps.asReadonly(),
            acknowledgeRejected,
            pendingCount: pendingQueue.asReadonly(),
          },
        },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: { get: () => 'term-1' } } },
        },
      ],
    }).compileComponents();

    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
    window.localStorage.clear();
    delete document.documentElement.dataset['theme'];
  });

  /** Types the full PIN and resolves the pending pinLogin with a session. */
  function unlock(fixture: ComponentFixture<TerminalPage>): void {
    const component = fixture.componentInstance;
    component.pressDigit('1');
    component.pressDigit('2');
    component.pressDigit('3');
    component.pressDigit('4');
    pinLoginResult.next(session);
  }

  it('shows no hours card before login', () => {
    const fixture = TestBed.createComponent(TerminalPage);
    fixture.detectChanges();

    expect(hoursOverview).not.toHaveBeenCalled();
    expect(fixture.nativeElement.querySelector('.hours-overview')).toBeNull();
  });

  it('shows the hours overview card after a successful pin login', () => {
    const fixture = TestBed.createComponent(TerminalPage);
    unlock(fixture);
    fixture.detectChanges();

    expect(hoursOverview).toHaveBeenCalledExactlyOnceWith('1234');

    const card = fixture.nativeElement.querySelector('.hours-overview') as HTMLElement;
    expect(card).not.toBeNull();
    const text = card.textContent ?? '';
    expect(text).toContain('Meine Stunden');
    expect(text).toContain('08:00');
    expect(text).toContain('+ 00:45 Pause');
    expect(text).toContain('20:00');
    expect(text).toContain('50:00');
  });

  it('keeps the card hidden when the hours overview request fails', () => {
    hoursOverview.mockReturnValue(throwError(() => ({ status: 500 })));

    const fixture = TestBed.createComponent(TerminalPage);
    unlock(fixture);
    fixture.detectChanges();

    expect(hoursOverview).toHaveBeenCalledWith('1234');
    expect(fixture.nativeElement.querySelector('.hours-overview')).toBeNull();
  });

  it('clears the card on back()', () => {
    const fixture = TestBed.createComponent(TerminalPage);
    const component = fixture.componentInstance;
    unlock(fixture);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.hours-overview')).not.toBeNull();

    component.back();
    fixture.detectChanges();

    expect(component.hoursOverview()).toBeNull();
    expect(fixture.nativeElement.querySelector('.hours-overview')).toBeNull();
  });

  it('does not refresh an unrelated employee when another backlog recovers', () => {
    const fixture = TestBed.createComponent(TerminalPage);
    unlock(fixture);
    fixture.componentInstance.message.set('Stempel gespeichert');
    recovered$.next();

    expect(pinLogin).toHaveBeenCalledTimes(1);
    expect(fixture.componentInstance.message()).toBe('Stempel gespeichert');
    expect(fixture.componentInstance.actionsBlocked()).toBe(false);
  });

  it('offers BOTH directions instead of a fake "Nicht eingestempelt" when the status is unknown', () => {
    // Offline card login of an employee this kiosk never saw a status for:
    // the real state is unknown, so it must not be guessed.
    window.localStorage.setItem(
      'stempeluhr.employee-card-cache.v1',
      JSON.stringify({ '04ABCD': session.employee }),
    );
    failPolls = true;
    localScanValue = { cardId: '04abcd', scannedAt: new Date().toISOString(), consumed: false };

    const fixture = TestBed.createComponent(TerminalPage);
    vi.advanceTimersByTime(1_000);
    fixture.detectChanges();

    expect(fixture.componentInstance.isOffline()).toBe(true);
    expect(fixture.componentInstance.isUnlocked()).toBe(true);
    expect(fixture.nativeElement.textContent).toContain('Status unbekannt');
    expect(fixture.nativeElement.textContent).not.toContain('Nicht eingestempelt');
    // Both ways out are on screen: the replay validates which one was right.
    expect(fixture.nativeElement.querySelector('.action-button.start')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('.action-button.stop')).not.toBeNull();
  });

  it('counts the waiting stamps in the kiosk offline banner - and stays quiet without any', () => {
    failPolls = true;
    const fixture = TestBed.createComponent(TerminalPage);
    vi.advanceTimersByTime(1_000);
    fixture.detectChanges();

    const banner = fixture.nativeElement.querySelector('.offline-banner') as HTMLElement;
    expect(banner).not.toBeNull();
    expect(banner.textContent).toContain('Offline –');
    // Ohne wartende Stempel bleibt der allgemeine Text stehen (kein "0 Stempel").
    expect(banner.textContent).not.toContain('warten auf Übertragung');

    pendingQueue.set([{}, {}]);
    fixture.detectChanges();
    expect(banner.textContent).toContain('2 Stempel warten auf Übertragung');

    // Einzahl klingt auch richtig - "1 Stempel warten" war ein Review-Hinweis.
    pendingQueue.set([{}]);
    fixture.detectChanges();
    expect(banner.textContent).toContain('1 Stempel wartet auf Übertragung');
  });

  it('shows waiting stamps on an ONLINE kiosk too (live stamp ok, buffered event left)', () => {
    // Der Kiosk ist erreichbar, in der Queue liegt aber noch etwas - genau der
    // Fall, in dem isOffline schon false ist und der Hinweis sonst verschwaende
    // (Review-Befund Runde 2).
    pendingQueue.set([{}]);
    const fixture = TestBed.createComponent(TerminalPage);
    vi.advanceTimersByTime(1_000);
    fixture.detectChanges();

    const banner = fixture.nativeElement.querySelector('.offline-banner') as HTMLElement;
    expect(banner).not.toBeNull();
    expect(banner.textContent).toContain('1 Stempel wartet auf Übertragung');
    // Online ist "Offline" die falsche Beschriftung.
    expect(banner.textContent).not.toContain('Offline');
  });

  it('offers Ausstempeln right after an OFFLINE Einstempeln', () => {
    window.localStorage.setItem(
      'stempeluhr.employee-card-cache.v1',
      JSON.stringify({ '04ABCD': session.employee }),
    );
    failPolls = true;
    localScanValue = { cardId: '04abcd', scannedAt: new Date().toISOString(), consumed: false };

    const fixture = TestBed.createComponent(TerminalPage);
    vi.advanceTimersByTime(1_000);
    fixture.detectChanges();

    fixture.componentInstance.start();
    fixture.detectChanges();

    expect(enqueueKiosk).toHaveBeenCalledTimes(1);
    // The queue event has to carry the display name: without it the refused-
    // stamp notice on the idle screen could only say "unbekannt".
    expect(enqueueKiosk).toHaveBeenCalledWith(
      expect.objectContaining({ employeeName: 'Max Mustermann', action: 'start' }),
    );
    // The queued start is shown as the current state - otherwise the employee
    // would only ever be offered "Einstempeln" again.
    expect(fixture.componentInstance.clockState.isWorking()).toBe(true);
    expect(fixture.nativeElement.querySelector('.action-button.stop')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('.action-button.pause')).not.toBeNull();
  });

  it('shows which offline stamp the server refused - with name, time and reason', () => {
    rejectedStamps.set([{
      eventId: 'r1',
      employeeId: 'max',
      employeeName: 'Max Mustermann',
      performedAt: '2026-09-18T05:55:00Z',
      rejectedAt: '2026-09-18T09:00:00Z',
      message: 'Mitarbeiter nicht gefunden oder PIN falsch.',
      action: 'start',
    }]);

    const fixture = TestBed.createComponent(TerminalPage);
    fixture.detectChanges();

    // Visible on the idle screen, so the NEXT person (or the admin) sees it.
    const notice = fixture.nativeElement.querySelector('.rejected-notice') as HTMLElement;
    expect(notice).not.toBeNull();
    expect(notice.textContent).toContain('1 Offline-Stempel nicht nachgetragen');
    expect(notice.textContent).toContain('Max Mustermann');
    expect(notice.textContent).toContain('Einstempeln');
    expect(notice.textContent).toContain('Mitarbeiter nicht gefunden oder PIN falsch.');

    // Die Klammer auf zwei Zeilen ist im jsdom nicht MESSBAR (kein Layout),
    // aber ihre Deklaration ist pinbar: ohne sie waechst die Leiste bei langen
    // Server-Meldungen (KimaiApiException traegt den ganzen Antwortrumpf mit)
    // auf 93 px und ueberdeckt die Loeschtaste des Tastenfelds.
    const style = getComputedStyle(notice.querySelector('.rejected-text') as HTMLElement);
    expect(style.display).toBe('-webkit-box');
    expect(style.webkitLineClamp).toBe('2');
    expect(style.overflow).toBe('hidden');
  });

  it('keeps quiet while no stamp was refused', () => {
    const fixture = TestBed.createComponent(TerminalPage);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.rejected-notice')).toBeNull();
  });

  it('clears the refused-stamp notice once somebody repaired the time', () => {
    rejectedStamps.set([{
      eventId: 'r1',
      employeeId: 'max',
      employeeName: 'Max Mustermann',
      performedAt: '2026-09-18T05:55:00Z',
      rejectedAt: '2026-09-18T09:00:00Z',
      message: 'Mitarbeiter nicht gefunden oder PIN falsch.',
      action: 'start',
    }]);
    const fixture = TestBed.createComponent(TerminalPage);
    fixture.detectChanges();

    (fixture.nativeElement.querySelector('.rejected-dismiss') as HTMLButtonElement).click();

    expect(acknowledgeRejected).toHaveBeenCalledExactlyOnceWith(['r1']);
  });

  it('names the effect of the button: it clears ALL refused stamps at once', () => {
    rejectedStamps.set([
      {
        eventId: 'r1',
        employeeId: 'max',
        employeeName: 'Max Mustermann',
        performedAt: '2026-09-18T05:55:00Z',
        rejectedAt: '2026-09-18T09:00:00Z',
        message: 'Mitarbeiter nicht gefunden oder PIN falsch.',
        action: 'start',
      },
      {
        eventId: 'r2',
        employeeId: 'erika',
        employeeName: 'Erika Musterfrau',
        performedAt: '2026-09-18T06:10:00Z',
        rejectedAt: '2026-09-18T09:00:00Z',
        message: 'Karte ist keinem Mitarbeiter zugeordnet.',
        action: 'stop',
      },
    ]);
    const fixture = TestBed.createComponent(TerminalPage);
    fixture.detectChanges();

    const notice = fixture.nativeElement.querySelector('.rejected-notice') as HTMLElement;
    expect(notice.textContent).toContain('2 Offline-Stempel nicht nachgetragen');
    const button = notice.querySelector('.rejected-dismiss') as HTMLButtonElement;
    // One detail line is shown, but the click discards every entry - the label
    // has to say so, otherwise two unread cases disappear with one press.
    expect(button.textContent?.trim()).toBe('Alle erledigt');
    rejectedStamps.update(entries => [...entries, { ...entries[0], eventId: 'r3' }]);
    // The new signal value has not reached the rendered button yet.
    button.click();
    expect(acknowledgeRejected).toHaveBeenCalledExactlyOnceWith(['r1', 'r2']);
  });

  it('hides the refused-stamp notice while a session is open - and shows it again after back()', () => {
    rejectedStamps.set([{
      eventId: 'r1',
      employeeId: 'max',
      employeeName: 'Max Mustermann',
      performedAt: '2026-09-18T05:55:00Z',
      rejectedAt: '2026-09-18T09:00:00Z',
      message: 'Mitarbeiter nicht gefunden oder PIN falsch.',
      action: 'start',
    }]);
    const fixture = TestBed.createComponent(TerminalPage);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.rejected-notice')).not.toBeNull();

    // During a session the bar would cover the hours card, so it stays hidden.
    unlock(fixture);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.rejected-notice')).toBeNull();

    fixture.componentInstance.back();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.rejected-notice')).not.toBeNull();
  });

  it('schaltet den Kiosk auf das dunkle Design und beim Verlassen zurueck auf hell', () => {
    const fixture = TestBed.createComponent(TerminalPage);
    fixture.detectChanges();

    expect(document.documentElement.dataset['theme']).toBe('dark');

    fixture.destroy();

    expect(document.documentElement.dataset['theme']).toBe('light');
  });

  it('does not claim "offline" while the backend is answering', () => {
    // ONLINE cache hit: the employee is unlocked before the server's status
    // answer arrives - the kiosk may say "unbekannt", but not "offline".
    window.localStorage.setItem(
      'stempeluhr.employee-card-cache.v1',
      JSON.stringify({ '04ABCD': session.employee }),
    );
    localScanValue = { cardId: '04abcd', scannedAt: new Date().toISOString(), consumed: false };

    const fixture = TestBed.createComponent(TerminalPage);
    vi.advanceTimersByTime(1_000);
    fixture.detectChanges();

    expect(fixture.componentInstance.isOffline()).toBe(false);
    expect(fixture.componentInstance.isUnlocked()).toBe(true);
    expect(fixture.componentInstance.clockState.status()).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Status unbekannt');
    expect(fixture.nativeElement.textContent).not.toContain('(offline)');
  });

  describe('task switch', () => {
    const working: ClockStatus = {
      isRunning: true,
      activeTimesheetId: 5,
      startedAt: '2026-09-27T06:00:00Z',
      durationSeconds: 0,
      state: 'working',
      stateText: 'Eingestempelt',
      activeTaskId: null,
      activeTaskLabel: null,
    };

    function unlockWorking(fixture: ComponentFixture<TerminalPage>, tasks = [{ id: 'kx', label: 'Kunde X' }]): void {
      const component = fixture.componentInstance;
      ['1', '2', '3', '4'].forEach(digit => component.pressDigit(digit));
      pinLoginResult.next({ employee: { ...session.employee, tasks }, status: working });
      fixture.detectChanges();
    }

    it('offers no switch button to employees without further tasks', () => {
      const fixture = TestBed.createComponent(TerminalPage);
      unlockWorking(fixture, []);

      expect(fixture.nativeElement.querySelector('.action-button.pause')).not.toBeNull();
      expect(fixture.nativeElement.querySelector('.action-button.switch')).toBeNull();
    });

    it('opens the picker in place of the stamp buttons and switches live', () => {
      clockImpl.mockImplementation(() => of({ ...working, activeTaskId: 'kx', activeTaskLabel: 'Kunde X', stateText: 'Wechsel zu Kunde X' }));
      const fixture = TestBed.createComponent(TerminalPage);
      unlockWorking(fixture);

      (fixture.nativeElement.querySelector('.action-button.switch') as HTMLButtonElement).click();
      fixture.detectChanges();

      const options = [...fixture.nativeElement.querySelectorAll('.task-button')] as HTMLButtonElement[];
      expect(options.map(option => option.textContent?.trim())).toEqual(['Standard-Tätigkeit', 'Kunde X']);
      // The running task (default) cannot be chosen again.
      expect(options[0].disabled).toBe(true);
      expect(fixture.nativeElement.querySelector('.action-button.stop')).toBeNull();

      vi.advanceTimersByTime(400);
      options[1].click();
      fixture.detectChanges();

      expect(clockImpl).toHaveBeenCalledWith('max', '1234', 'switch', null, 'kx', expect.any(String));
      expect(fixture.componentInstance.taskPickerOpen()).toBe(false);
      expect(fixture.nativeElement.querySelector('.task-label')?.textContent).toContain('Kunde X');
    });

    it('ignores a second tap right after opening the picker (issue #59)', () => {
      const fixture = TestBed.createComponent(TerminalPage);
      unlockWorking(fixture);

      (fixture.nativeElement.querySelector('.action-button.switch') as HTMLButtonElement).click();
      fixture.detectChanges();
      const options = [...fixture.nativeElement.querySelectorAll('.task-button')] as HTMLButtonElement[];

      // A bouncing double tap lands on the option now under the finger.
      vi.advanceTimersByTime(150);
      options[1].click();
      fixture.detectChanges();

      expect(clockImpl).not.toHaveBeenCalled();
      expect(enqueueKiosk).not.toHaveBeenCalled();
      expect(fixture.componentInstance.taskPickerOpen()).toBe(true);

      vi.advanceTimersByTime(250);
      options[1].click();

      expect(clockImpl).toHaveBeenCalledWith('max', '1234', 'switch', null, 'kx', expect.any(String));
    });

    it('names the main task as configured and says since when the section runs', () => {
      const fixture = TestBed.createComponent(TerminalPage);
      const component = fixture.componentInstance;
      ['1', '2', '3', '4'].forEach(digit => component.pressDigit(digit));
      pinLoginResult.next({
        employee: { ...session.employee, tasks: [{ id: 'kx', label: 'Kunde X' }], defaultTaskLabel: 'Büro' },
        status: working,
      });
      fixture.detectChanges();

      const caption = fixture.nativeElement.querySelector('.task-label')?.textContent ?? '';
      expect(caption).toContain('Büro');
      expect(caption).toMatch(/seit \d{2}:\d{2}/);

      component.openTaskPicker();
      fixture.detectChanges();
      const first = fixture.nativeElement.querySelector('.task-button') as HTMLButtonElement;
      expect(first.textContent?.trim()).toBe('Büro');
    });

    it('marks no task as running for a sheet that matches none (e.g. deleted task)', () => {
      const fixture = TestBed.createComponent(TerminalPage);
      const component = fixture.componentInstance;
      ['1', '2', '3', '4'].forEach(digit => component.pressDigit(digit));
      pinLoginResult.next({
        employee: { ...session.employee, tasks: [{ id: 'kx', label: 'Kunde X' }], defaultTaskLabel: 'Büro' },
        status: { ...working, activeIsDefaultTask: false },
      });
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('.task-label')?.textContent).not.toContain('Büro');

      component.openTaskPicker();
      fixture.detectChanges();
      const options = [...fixture.nativeElement.querySelectorAll('.task-button')] as HTMLButtonElement[];
      // Switching back to the main task must stay possible.
      expect(options.map(option => option.disabled)).toEqual([false, false]);
    });

    it('shows since when the section runs also without further tasks', () => {
      const fixture = TestBed.createComponent(TerminalPage);
      unlockWorking(fixture, []);

      const caption = fixture.nativeElement.querySelector('.task-label')?.textContent ?? '';
      expect(caption).toMatch(/^\s*seit \d{2}:\d{2}\s*$/);
    });

    it('drops the "since" caption while offline to keep 800x480 free of scrolling', () => {
      const fixture = TestBed.createComponent(TerminalPage);
      unlockWorking(fixture, []);
      fixture.componentInstance.isOffline.set(true);
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('.task-label')).toBeNull();
    });

    it('keeps the task name but not the time while offline', () => {
      const fixture = TestBed.createComponent(TerminalPage);
      unlockWorking(fixture);
      fixture.componentInstance.isOffline.set(true);
      fixture.detectChanges();

      const caption = fixture.nativeElement.querySelector('.task-label')?.textContent ?? '';
      expect(caption).toContain('Standard-Tätigkeit');
      expect(caption).not.toContain('seit');
    });

    it('closes the picker when the session ends', () => {
      const fixture = TestBed.createComponent(TerminalPage);
      unlockWorking(fixture);
      fixture.componentInstance.openTaskPicker();

      fixture.componentInstance.back();

      expect(fixture.componentInstance.taskPickerOpen()).toBe(false);
    });

    it('queues an offline switch with its target task and shows it as current', () => {
      const fixture = TestBed.createComponent(TerminalPage);
      unlockWorking(fixture);

      fixture.componentInstance.switchTask('kx');
      fixture.detectChanges();

      expect(enqueueKiosk).toHaveBeenCalledWith(expect.objectContaining({ action: 'switch', taskId: 'kx' }));
      expect(fixture.componentInstance.clockState.status()?.activeTaskId).toBe('kx');
      expect(fixture.componentInstance.clockState.isWorking()).toBe(true);
    });
  });

  describe('clock in on a task', () => {
    const tasks = [{ id: 'kx', label: 'Kunde X' }];

    function unlockClockedOut(fixture: ComponentFixture<TerminalPage>, employeeTasks = tasks): void {
      const component = fixture.componentInstance;
      ['1', '2', '3', '4'].forEach(digit => component.pressDigit(digit));
      pinLoginResult.next({ employee: { ...session.employee, tasks: employeeTasks, defaultTaskLabel: 'Büro' }, status });
      fixture.detectChanges();
    }

    function startOptions(fixture: ComponentFixture<TerminalPage>): HTMLButtonElement[] {
      return [...fixture.nativeElement.querySelectorAll('.task-button.start-task')] as HTMLButtonElement[];
    }

    it('keeps the single Einstempeln button without further tasks', () => {
      const fixture = TestBed.createComponent(TerminalPage);
      unlockClockedOut(fixture, []);

      expect(fixture.nativeElement.querySelector('.action-button.start.single')).not.toBeNull();
      expect(startOptions(fixture)).toEqual([]);
    });

    it('shows the task choice right away and clocks in on the chosen task with one tap', () => {
      clockImpl.mockImplementation(() => of({
        ...status, isRunning: true, state: 'working', stateText: 'Eingestempelt',
        startedAt: '2026-09-27T06:00:00Z', activeTaskId: 'kx', activeTaskLabel: 'Kunde X',
      }));
      const fixture = TestBed.createComponent(TerminalPage);
      unlockClockedOut(fixture);

      expect(fixture.nativeElement.querySelector('.action-button.start')).toBeNull();
      expect(fixture.nativeElement.querySelector('.start-choice-title')?.textContent).toContain('Einstempeln auf');
      const options = startOptions(fixture);
      expect(options.map(option => option.textContent?.trim())).toEqual(['Büro', 'Kunde X']);
      // Clocked out there is nothing to go back to: no Abbrechen.
      expect(fixture.nativeElement.querySelector('.task-cancel')).toBeNull();

      options[1].click();
      fixture.detectChanges();

      expect(clockImpl).toHaveBeenCalledWith('max', '1234', 'start', null, 'kx', expect.any(String));
      expect(fixture.nativeElement.querySelector('.task-label')?.textContent).toContain('Kunde X');
    });

    it('clocks in on the main task without a task id', () => {
      clockImpl.mockImplementation(() => of({ ...status, isRunning: true, state: 'working', stateText: 'Eingestempelt' }));
      const fixture = TestBed.createComponent(TerminalPage);
      unlockClockedOut(fixture);

      startOptions(fixture)[0].click();

      expect(clockImpl).toHaveBeenCalledWith('max', '1234', 'start', null, null, expect.any(String));
    });

    it('queues an offline clock-in with its task and shows that task as running', () => {
      const fixture = TestBed.createComponent(TerminalPage);
      unlockClockedOut(fixture);

      fixture.componentInstance.start('kx');
      fixture.detectChanges();

      expect(enqueueKiosk).toHaveBeenCalledWith(expect.objectContaining({ action: 'start', taskId: 'kx' }));
      expect(fixture.componentInstance.clockState.status()?.activeTaskId).toBe('kx');
      expect(fixture.nativeElement.querySelector('.task-label')?.textContent).toContain('Kunde X');
    });

    it('opens the choice from Einstempeln while the status is unknown - and back to both directions', () => {
      window.localStorage.setItem(
        'stempeluhr.employee-card-cache.v1',
        JSON.stringify({ '04ABCD': { ...session.employee, tasks } }),
      );
      failPolls = true;
      localScanValue = { cardId: '04abcd', scannedAt: new Date().toISOString(), consumed: false };

      const fixture = TestBed.createComponent(TerminalPage);
      vi.advanceTimersByTime(1_000);
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('.action-button.stop')).not.toBeNull();
      (fixture.nativeElement.querySelector('.action-button.start') as HTMLButtonElement).click();
      fixture.detectChanges();

      expect(startOptions(fixture).length).toBe(2);
      expect(enqueueKiosk).not.toHaveBeenCalled();

      (fixture.nativeElement.querySelector('.task-cancel') as HTMLButtonElement).click();
      fixture.detectChanges();

      expect(startOptions(fixture)).toEqual([]);
      expect(fixture.nativeElement.querySelector('.action-button.start')).not.toBeNull();
      expect(fixture.nativeElement.querySelector('.action-button.stop')).not.toBeNull();
    });

    it('ignores a second tap right after Einstempeln opened the choice (issue #59)', () => {
      window.localStorage.setItem(
        'stempeluhr.employee-card-cache.v1',
        JSON.stringify({ '04ABCD': { ...session.employee, tasks } }),
      );
      failPolls = true;
      localScanValue = { cardId: '04abcd', scannedAt: new Date().toISOString(), consumed: false };
      const fixture = TestBed.createComponent(TerminalPage);
      vi.advanceTimersByTime(1_000);
      fixture.detectChanges();

      (fixture.nativeElement.querySelector('.action-button.start') as HTMLButtonElement).click();
      fixture.detectChanges();
      // The main task now lies under the finger.
      startOptions(fixture)[0].click();
      fixture.detectChanges();

      expect(enqueueKiosk).not.toHaveBeenCalled();
      expect(startOptions(fixture).length).toBe(2);

      vi.advanceTimersByTime(400);
      startOptions(fixture)[0].click();

      expect(enqueueKiosk).toHaveBeenCalledWith(expect.objectContaining({ action: 'start', taskId: null }));
    });

    describe('with a cached card while online', () => {
      let identify$: Subject<NfcClockEvent>;

      function identifyEvent(overrides: Partial<NfcClockEvent>): NfcClockEvent {
        return {
          eventId: 'e1',
          occurredAt: new Date().toISOString(),
          terminalId: 'term-1',
          cardId: '04ABCD',
          employee: { ...session.employee, tasks },
          status,
          message: 'NFC-Karte erkannt.',
          success: true,
          ...overrides,
        };
      }

      /** Scans the cached card; the server's identify answer stays pending in identify$. */
      function scanCachedCard(cachedTasks = tasks): ComponentFixture<TerminalPage> {
        window.localStorage.setItem(
          'stempeluhr.employee-card-cache.v1',
          JSON.stringify({ '04ABCD': { ...session.employee, tasks: cachedTasks } }),
        );
        identify$ = new Subject<NfcClockEvent>();
        vi.mocked(TestBed.inject(KioskApi).identify).mockImplementation(() => identify$);
        localScanValue = { cardId: '04abcd', scannedAt: new Date().toISOString(), consumed: false };

        const fixture = TestBed.createComponent(TerminalPage);
        vi.advanceTimersByTime(1_000);
        fixture.detectChanges();
        return fixture;
      }

      it('never turns the open start choice into the switch picker when the status arrives as working', () => {
        const fixture = scanCachedCard();
        (fixture.nativeElement.querySelector('.action-button.start') as HTMLButtonElement).click();
        fixture.detectChanges();
        expect(startOptions(fixture).length).toBe(2);

        identify$.next(identifyEvent({
          status: { ...status, isRunning: true, activeTimesheetId: 5, state: 'working', stateText: 'Eingestempelt', activeIsDefaultTask: true },
        }));
        fixture.detectChanges();

        // A tap on "Kunde X" would book a switch here - the working buttons come instead.
        expect(fixture.componentInstance.taskPickerOpen()).toBe(false);
        expect(fixture.nativeElement.querySelectorAll('.task-button').length).toBe(0);
        expect(fixture.nativeElement.querySelector('.action-button.switch')).not.toBeNull();
        expect(fixture.nativeElement.querySelector('.action-button.stop')).not.toBeNull();
      });

      it('keeps offering the start choice when the status arrives as clocked out', () => {
        const fixture = scanCachedCard();
        (fixture.nativeElement.querySelector('.action-button.start') as HTMLButtonElement).click();

        identify$.next(identifyEvent({}));
        fixture.detectChanges();

        expect(startOptions(fixture).length).toBe(2);
        // Known status: there is no Ein-/Ausstempeln to go back to.
        expect(fixture.nativeElement.querySelector('.task-cancel')).toBeNull();
      });

      it('keeps a projected status while queued card stamps replay', () => {
        pendingQueue.set([{ kind: 'kiosk', event: { employeeId: 'max' } }]);
        rememberProjectedStatus('max', {
          ...status, isRunning: true, state: 'working', stateText: 'Eingestempelt',
        });
        const fixture = scanCachedCard();
        const component = fixture.componentInstance;
        const queue = TestBed.inject(OfflineQueueService);
        expect(component.actionsBlocked()).toBe(true);
        // Only the cache vouches for the card so far (issue #75).
        expect(queue.authorizeEmployeeCard).not.toHaveBeenCalled();
        expect(queue.syncNow).toHaveBeenCalled();

        identify$.next(identifyEvent({}));
        expect(queue.authorizeEmployeeCard).toHaveBeenCalledExactlyOnceWith('max', '04ABCD');
        expect(component.clockState.status()).toBeNull();
        expect(lastKnownStatus('max')?.origin).toBe('projected');
        expect(component.actionsBlocked()).toBe(true);
      });

      it('never attaches a moved card to the stamps of its former owner (issue #75)', () => {
        pendingQueue.set([
          { kind: 'kiosk', event: { employeeId: 'max' } },
          { kind: 'kiosk', event: { employeeId: 'anna' } },
        ]);
        const fixture = scanCachedCard();
        const queue = TestBed.inject(OfflineQueueService);
        expect(queue.authorizeEmployeeCard).not.toHaveBeenCalled();

        identify$.next(identifyEvent({
          employee: { ...session.employee, id: 'anna', displayName: 'Anna', tasks },
          status: null,
        }));

        expect(queue.authorizeEmployeeCard).toHaveBeenCalledExactlyOnceWith('anna', '04ABCD');
        expect(fixture.componentInstance.selectedEmployee()?.id).toBe('anna');
      });

      it('keeps the stamps waiting for the PIN when identify fails', () => {
        pendingQueue.set([{ kind: 'kiosk', event: { employeeId: 'max' } }]);
        const fixture = scanCachedCard();
        const component = fixture.componentInstance;

        identify$.error({ status: 429 });

        expect(TestBed.inject(OfflineQueueService).authorizeEmployeeCard).not.toHaveBeenCalled();
        // Nothing replays without the card - never claim otherwise.
        expect(component.actionsBlocked()).toBe(true);
        expect(component.message()).toBe('Karte nicht bestätigt – bitte erneut anmelden.');
      });

      it('says so when the server no longer knows the cached card', () => {
        pendingQueue.set([{ kind: 'kiosk', event: { employeeId: 'max' } }]);
        const fixture = scanCachedCard();

        identify$.next(identifyEvent({ success: false, employee: null, status: null, message: 'Unbekannte Karte' }));

        expect(TestBed.inject(OfflineQueueService).authorizeEmployeeCard).not.toHaveBeenCalled();
        expect(fixture.componentInstance.message()).toBe('Karte nicht bestätigt – bitte erneut anmelden.');
      });

      it('keeps quiet about a failed identify when no stamps wait for the card', () => {
        const fixture = scanCachedCard();
        const before = fixture.componentInstance.message();

        identify$.error({ status: 429 });

        expect(fixture.componentInstance.message()).toBe(before);
        expect(fixture.componentInstance.actionsBlocked()).toBe(false);
      });

      it('offers the tasks the server knows now, not the ones cached with the card', () => {
        const fixture = scanCachedCard([{ id: 'gone', label: 'Gelöscht' }]);

        identify$.next(identifyEvent({ employee: { ...session.employee, tasks: [{ id: 'ky', label: 'Kunde Y' }] } }));
        fixture.detectChanges();

        expect(startOptions(fixture).map(option => option.textContent?.trim())).toEqual(['Standard-Tätigkeit', 'Kunde Y']);
        const cache = JSON.parse(window.localStorage.getItem('stempeluhr.employee-card-cache.v1') ?? '{}');
        expect(cache['04ABCD'].tasks).toEqual([{ id: 'ky', label: 'Kunde Y' }]);
      });

      it('still refreshes the card cache when the employee acts before identify answers', () => {
        const working: ClockStatus = {
          ...status, isRunning: true, activeTimesheetId: 7, state: 'working', stateText: 'Eingestempelt', activeIsDefaultTask: true,
        };
        clockImpl.mockImplementation(() => of(working));
        const fixture = scanCachedCard([{ id: 'gone', label: 'Gelöscht' }]);
        fixture.componentInstance.start(null);

        // The answer to the scan is older than the action's: only the card cache takes it.
        identify$.next(identifyEvent({ employee: { ...session.employee, tasks: [{ id: 'ky', label: 'Kunde Y' }] } }));
        fixture.detectChanges();

        const cache = JSON.parse(window.localStorage.getItem('stempeluhr.employee-card-cache.v1') ?? '{}');
        expect(cache['04ABCD'].tasks).toEqual([{ id: 'ky', label: 'Kunde Y' }]);
        expect(fixture.componentInstance.clockState.status()?.state).toBe('working');
        const statusCache = JSON.parse(window.localStorage.getItem('stempeluhr.employee-status-cache.v1') ?? '{}');
        expect(statusCache['max'].status.state).toBe('working');
      });

      it('treats a card that now belongs to someone else as a new session', () => {
        window.localStorage.setItem(
          'stempeluhr.employee-status-cache.v1',
          JSON.stringify({
            max: {
              status: { ...status, isRunning: true, activeTimesheetId: 5, state: 'working', stateText: 'Eingestempelt', activeIsDefaultTask: true },
              observedAt: new Date().toISOString(),
              origin: 'observed',
            },
          }),
        );
        const fixture = scanCachedCard();
        fixture.componentInstance.openTaskPicker();
        expect(fixture.componentInstance.taskPickerOpen()).toBe(true);

        identify$.next(identifyEvent({
          employee: { ...session.employee, id: 'anna', displayName: 'Anna', tasks },
          status: null,
        }));
        fixture.detectChanges();

        expect(fixture.componentInstance.selectedEmployee()?.id).toBe('anna');
        expect(fixture.componentInstance.taskPickerOpen()).toBe(false);
        // Nothing is known about Anna yet: never Max's status under her name.
        expect(fixture.componentInstance.clockState.status()).toBeNull();
        expect(fixture.componentInstance.message()).toContain('Anna');
      });

      it('drops an identify answer that arrives after the session ended', () => {
        const fixture = scanCachedCard();
        fixture.componentInstance.back();

        identify$.next(identifyEvent({}));
        fixture.detectChanges();

        expect(fixture.componentInstance.selectedEmployee()).toBeNull();
        expect(fixture.componentInstance.clockState.status()).toBeNull();
      });
    });
  });

  describe('Korrekturanträge', () => {
    // Seit 13 h eingestempelt (Beginn 01:00Z, jetzt 14:00Z), Abschnitt = Timesheet 12 der Testdaten.
    const working: ClockStatus = {
      isRunning: true,
      activeTimesheetId: 12,
      startedAt: '2026-10-05T01:00:00Z',
      durationSeconds: 46800,
      state: 'working',
      stateText: 'Eingestempelt',
      activeIsDefaultTask: true,
    };
    const clockedOut: ClockStatus = { ...status, stateText: 'Ausgestempelt' };

    beforeEach(() => {
      vi.setSystemTime(CORRECTION_NOW);
    });

    function login(sessionStatus: ClockStatus = status, tasks: { id: string; label: string }[] = []): ComponentFixture<TerminalPage> {
      const fixture = TestBed.createComponent(TerminalPage);
      ['1', '2', '3', '4'].forEach(digit => fixture.componentInstance.pressDigit(digit));
      pinLoginResult.next({ employee: { ...session.employee, tasks }, status: sessionStatus });
      fixture.detectChanges();
      return fixture;
    }

    function loginWithCard(): ComponentFixture<TerminalPage> {
      window.localStorage.setItem('stempeluhr.employee-card-cache.v1', JSON.stringify({ '04ABCD': session.employee }));
      localScanValue = { cardId: '04abcd', scannedAt: new Date().toISOString(), consumed: false };
      const fixture = TestBed.createComponent(TerminalPage);
      vi.advanceTimersByTime(1_000);
      fixture.detectChanges();
      return fixture;
    }

    const entryButton = (fixture: ComponentFixture<TerminalPage>) =>
      fixture.nativeElement.querySelector('app-correction-entry .correction-entry') as HTMLButtonElement | null;

    function openFlow(fixture: ComponentFixture<TerminalPage>): void {
      entryButton(fixture)!.click();
      fixture.detectChanges();
    }

    function tap(fixture: ComponentFixture<TerminalPage>, text: string): void {
      const button = ([...fixture.nativeElement.querySelectorAll('app-correction-flow button')] as HTMLButtonElement[])
        .find(candidate => candidate.textContent?.trim() === text);
      expect(button, `Knopf "${text}"`).toBeDefined();
      button!.click();
      fixture.detectChanges();
    }

    function tapEntry(fixture: ComponentFixture<TerminalPage>, range: string): void {
      const button = ([...fixture.nativeElement.querySelectorAll('app-correction-flow .flow-entry')] as HTMLButtonElement[])
        .find(candidate => candidate.textContent?.includes(range));
      expect(button, `Eintrag ${range}`).toBeDefined();
      button!.click();
      fixture.detectChanges();
    }

    const flowTitle = (fixture: ComponentFixture<TerminalPage>) =>
      fixture.nativeElement.querySelector('app-correction-flow .flow-title')?.textContent?.trim();

    describe('Einstieg', () => {
      it('offers Korrektur in the session, but not on the login screen', () => {
        const fixture = TestBed.createComponent(TerminalPage);
        fixture.detectChanges();
        expect(entryButton(fixture)).toBeNull();

        ['1', '2', '3', '4'].forEach(digit => fixture.componentInstance.pressDigit(digit));
        pinLoginResult.next(session);
        fixture.detectChanges();

        expect(entryButton(fixture)?.textContent?.trim()).toBe('Korrektur');
        expect(entryButton(fixture)?.disabled).toBe(false);
      });

      it('is locked offline and says why', () => {
        const fixture = login();
        fixture.componentInstance.isOffline.set(true);
        fixture.detectChanges();

        expect(entryButton(fixture)?.textContent?.trim()).toBe('Korrektur nur online');
        expect(entryButton(fixture)?.disabled).toBe(true);
        entryButton(fixture)!.click();
        fixture.componentInstance.openCorrection();
        fixture.detectChanges();
        expect(fixture.componentInstance.correctionOpen()).toBe(false);
        expect(correctionTimesheets).not.toHaveBeenCalled();
      });

      it('is locked while stamps of this employee still wait for transfer', () => {
        pendingQueue.set([{ kind: 'kiosk', event: { employeeId: 'max' } }]);
        const fixture = TestBed.createComponent(TerminalPage);
        ['1', '2', '3', '4'].forEach(digit => fixture.componentInstance.pressDigit(digit));
        pinLoginResult.next(session);
        fixture.detectChanges();

        expect(entryButton(fixture)?.disabled).toBe(true);
      });

      it('reports a lost connection inside the open flow instead of queuing anything', () => {
        const fixture = login();
        openFlow(fixture);
        fixture.componentInstance.isOffline.set(true);
        correctionTimesheets.mockReturnValue(throwError(() => ({ status: 0 })));
        tap(fixture, 'Pause nachtragen');

        expect(fixture.nativeElement.querySelector('app-correction-flow .flow-error')?.textContent).toContain('Keine Verbindung');
        expect(fixture.componentInstance.correctionOpen()).toBe(true);
        expect(enqueueKiosk).not.toHaveBeenCalled();
      });
    });

    describe('Ablauf bis Absenden (PIN-Session)', () => {
      it('addPause', () => {
        const fixture = login();
        openFlow(fixture);
        tap(fixture, 'Pause nachtragen');
        tapEntry(fixture, '07:00–11:00');
        tap(fixture, 'Weiter');
        tap(fixture, 'Weiter');
        tap(fixture, 'Absenden');

        expect(correctionTimesheets).toHaveBeenCalledWith({ employeeId: 'max', pin: '1234', nfcCardId: null });
        expect(submitCorrection).toHaveBeenCalledExactlyOnceWith({
          employeeId: 'max', pin: '1234', nfcCardId: null,
          kind: 'addPause', timesheetId: 21, begin: null, end: null,
          pauseBegin: '2026-10-05T08:45', pauseEnd: '2026-10-05T09:15',
          taskId: null, comment: null, source: 'term-1',
        });
        expect(fixture.nativeElement.querySelector('app-correction-flow .flow-sent')?.textContent)
          .toBe('Antrag gesendet – wartet auf Freigabe');
      });

      it('setEnd', () => {
        const fixture = login();
        openFlow(fixture);
        tap(fixture, 'Ausstempeln nachtragen');
        tapEntry(fixture, '22:00 – So 04.10. 02:00');
        (fixture.nativeElement.querySelector('app-time-stepper .hour-later') as HTMLButtonElement).click();
        fixture.detectChanges();
        tap(fixture, 'Weiter');
        tap(fixture, 'Absenden');

        expect(submitCorrection).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({
          kind: 'setEnd', timesheetId: 12, end: '2026-10-04T01:00', begin: null, comment: null, source: 'term-1',
        }));
      });

      it('changeTimes', () => {
        const fixture = login();
        openFlow(fixture);
        tap(fixture, 'Zeiten ändern');
        tapEntry(fixture, '07:00–11:00');
        (fixture.nativeElement.querySelector('app-time-stepper .minute-later-5') as HTMLButtonElement).click();
        fixture.detectChanges();
        tap(fixture, 'Weiter');
        (fixture.nativeElement.querySelector('app-time-stepper .minute-earlier-1') as HTMLButtonElement).click();
        fixture.detectChanges();
        tap(fixture, 'Weiter');
        tap(fixture, 'Absenden');

        expect(submitCorrection).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({
          kind: 'changeTimes', timesheetId: 21, begin: '2026-10-05T07:05', end: '2026-10-05T10:59',
        }));
      });

      it('addShift with task and pause', () => {
        const fixture = login(status, [{ id: 'kx', label: 'Kunde X' }]);
        openFlow(fixture);
        tap(fixture, 'Schicht nachtragen');
        tap(fixture, 'Kunde X');
        tap(fixture, 'Weiter');
        tap(fixture, 'Weiter');
        tap(fixture, 'Mit Pause');
        tap(fixture, 'Weiter');
        tap(fixture, 'Weiter');
        tap(fixture, 'Absenden');

        expect(submitCorrection).toHaveBeenCalledExactlyOnceWith({
          employeeId: 'max', pin: '1234', nfcCardId: null,
          kind: 'addShift', timesheetId: null,
          begin: '2026-10-05T08:00', end: '2026-10-05T16:00',
          pauseBegin: '2026-10-05T11:45', pauseEnd: '2026-10-05T12:15',
          taskId: 'kx', comment: null, source: 'term-1',
        });
      });

      it('has no comment field - the terminal has no keyboard', () => {
        const fixture = login();
        openFlow(fixture);
        tap(fixture, 'Ausstempeln nachtragen');
        tapEntry(fixture, '22:00 – So 04.10. 02:00');
        tap(fixture, 'Weiter');

        expect(flowTitle(fixture)).toBe('Zusammenfassung');
        expect(fixture.nativeElement.querySelector('app-correction-flow textarea')).toBeNull();
      });

      it('shows the server message on 400 and the employee stays in the summary', () => {
        submitCorrection.mockReturnValue(throwError(() => ({ status: 400, error: { message: 'Eine Pause darf höchstens 4 Stunden dauern.' } })));
        const fixture = login();
        openFlow(fixture);
        tap(fixture, 'Pause nachtragen');
        tapEntry(fixture, '07:00–11:00');
        tap(fixture, 'Weiter');
        tap(fixture, 'Weiter');
        tap(fixture, 'Absenden');

        expect(fixture.nativeElement.querySelector('app-correction-flow .flow-error')?.textContent).toContain('höchstens 4 Stunden');
        expect(flowTitle(fixture)).toBe('Zusammenfassung');
      });

      it('Fertig returns to the session, the employee stays logged in', () => {
        const fixture = login();
        openFlow(fixture);
        tap(fixture, 'Ausstempeln nachtragen');
        tapEntry(fixture, '22:00 – So 04.10. 02:00');
        tap(fixture, 'Weiter');
        tap(fixture, 'Absenden');
        tap(fixture, 'Fertig');

        expect(fixture.componentInstance.correctionOpen()).toBe(false);
        expect(fixture.componentInstance.selectedEmployee()?.id).toBe('max');
        expect(fixture.nativeElement.querySelector('.action-panel')).not.toBeNull();
      });
    });

    describe('Karten-Session ohne PIN', () => {
      it('sends employeeId plus cardId and an empty PIN with every request', () => {
        const fixture = loginWithCard();
        expect(fixture.componentInstance.selectedEmployee()?.id).toBe('max');

        openFlow(fixture);
        tap(fixture, 'Ausstempeln nachtragen');
        tapEntry(fixture, '22:00 – So 04.10. 02:00');
        tap(fixture, 'Weiter');
        tap(fixture, 'Absenden');

        const card = { employeeId: 'max', pin: '', nfcCardId: '04ABCD' };
        expect(correctionTimesheets).toHaveBeenCalledWith(card);
        expect(submitCorrection).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ ...card, kind: 'setEnd', timesheetId: 12 }));
      });

      it('reads and withdraws "Meine Anträge" with the card', () => {
        myCorrections.mockReturnValue(of([correction({ id: 'p1' })]));
        withdrawCorrection.mockReturnValue(of(correction({ id: 'p1', status: 'withdrawn' })));
        const fixture = loginWithCard();
        openFlow(fixture);
        tap(fixture, 'Meine Anträge');
        tap(fixture, 'Zurückziehen');

        const card = { employeeId: 'max', pin: '', nfcCardId: '04ABCD' };
        expect(myCorrections).toHaveBeenCalledWith(card);
        expect(withdrawCorrection).toHaveBeenCalledExactlyOnceWith(card, 'p1');
      });
    });

    describe('Meine Anträge', () => {
      it('lists the requests with status, before and after, and withdraws an open one', () => {
        myCorrections.mockReturnValue(of([
          correction({ id: 'p1' }),
          correction({ id: 'r1', status: 'rejected', decisionNote: 'Bitte mit dem Chef sprechen' }),
        ]));
        withdrawCorrection.mockReturnValue(of(correction({ id: 'p1', status: 'withdrawn' })));
        const fixture = login();
        openFlow(fixture);
        tap(fixture, 'Meine Anträge');

        const text = fixture.nativeElement.querySelector('app-correction-flow')?.textContent ?? '';
        expect(text).toContain('Wartet auf Freigabe');
        expect(text).toContain('Abgelehnt');
        expect(text).toContain('Grund: Bitte mit dem Chef sprechen');
        expect(text).toContain('Arbeit Mo 05.10. 07:00–11:00');
        expect(text).toContain('Arbeit Mo 05.10. 07:00–10:00');

        tap(fixture, 'Zurückziehen');
        expect(withdrawCorrection).toHaveBeenCalledExactlyOnceWith({ employeeId: 'max', pin: '1234', nfcCardId: null }, 'p1');
        expect(fixture.nativeElement.querySelector('app-correction-flow')?.textContent).toContain('Zurückgezogen');
        expect(([...fixture.nativeElement.querySelectorAll('app-correction-flow button')] as HTMLButtonElement[])
          .some(button => button.textContent?.trim() === 'Zurückziehen')).toBe(false);
      });
    });

    describe('Sitzung und verspätete Antworten', () => {
      it('discards a late answer after the identity changed - nothing of Max under Anna', () => {
        const lateList = new Subject<typeof CORRECTION_TIMESHEETS>();
        correctionTimesheets.mockReturnValue(lateList);
        const fixture = login();
        openFlow(fixture);
        tap(fixture, 'Pause nachtragen');

        // X: Max is gone, Anna logs in and opens her own flow.
        fixture.nativeElement.querySelector('.back-button').click();
        fixture.detectChanges();
        expect(fixture.componentInstance.correctionOpen()).toBe(false);
        pinLogin.mockImplementation(() => of({ employee: { ...session.employee, id: 'anna', displayName: 'Anna Beispiel' }, status }));
        ['4', '3', '2', '1'].forEach(digit => fixture.componentInstance.pressDigit(digit));
        fixture.detectChanges();
        correctionTimesheets.mockReturnValue(of({ timeZone: 'Europe/Berlin', shifts: [] }));
        openFlow(fixture);
        tap(fixture, 'Pause nachtragen');

        // Max's answer finally arrives.
        lateList.next(CORRECTION_TIMESHEETS);
        fixture.detectChanges();

        expect(correctionTimesheets).toHaveBeenLastCalledWith({ employeeId: 'anna', pin: '4321', nfcCardId: null });
        expect(fixture.nativeElement.querySelector('app-correction-flow .flow-entry')).toBeNull();
        expect(fixture.nativeElement.querySelector('app-correction-flow .flow-note')?.textContent).toContain('Keine passenden Einträge');
        expect(fixture.nativeElement.textContent).not.toContain('Nachtdienst');
        expect(lateList.observed).toBe(false);
      });

      it('X ends the flow and logs out', () => {
        const fixture = login();
        openFlow(fixture);
        expect(fixture.componentInstance.correctionOpen()).toBe(true);

        fixture.nativeElement.querySelector('.back-button').click();
        fixture.detectChanges();

        expect(fixture.componentInstance.correctionOpen()).toBe(false);
        expect(fixture.nativeElement.querySelector('app-correction-flow')).toBeNull();
        expect(fixture.nativeElement.querySelector('.login-panel')).not.toBeNull();
      });

      it('a new card scan during the flow does not switch the employee, the flow stays', () => {
        const fixture = login();
        openFlow(fixture);
        localScanValue = { cardId: '04ffff', scannedAt: new Date().toISOString(), consumed: false };
        vi.advanceTimersByTime(1_000);
        fixture.detectChanges();

        expect(fixture.componentInstance.selectedEmployee()?.id).toBe('max');
        expect(fixture.componentInstance.correctionOpen()).toBe(true);
      });

      it('goes back to the idle screen after 2 minutes without a tap in the flow', () => {
        const fixture = login();
        openFlow(fixture);

        vi.advanceTimersByTime(CORRECTION_IDLE_MS - 1_000);
        fixture.detectChanges();
        expect(fixture.componentInstance.selectedEmployee()).not.toBeNull();

        tap(fixture, 'Pause nachtragen');
        vi.advanceTimersByTime(CORRECTION_IDLE_MS - 1_000);
        fixture.detectChanges();
        // The tap started the 2 minutes anew.
        expect(fixture.componentInstance.selectedEmployee()).not.toBeNull();

        vi.advanceTimersByTime(1_000);
        fixture.detectChanges();
        expect(fixture.componentInstance.selectedEmployee()).toBeNull();
        expect(fixture.componentInstance.correctionOpen()).toBe(false);
        expect(fixture.nativeElement.querySelector('.login-panel')).not.toBeNull();
      });

      it('a running reset after a stamp does not close the flow opened right afterwards', () => {
        clockImpl.mockImplementation(() => of({ ...working, stateText: 'Eingestempelt' }));
        const fixture = login();
        fixture.componentInstance.start();
        fixture.detectChanges();
        // Within the 2.2 s of the reset the employee taps Korrektur.
        openFlow(fixture);

        vi.advanceTimersByTime(5_000);
        fixture.detectChanges();

        expect(fixture.componentInstance.correctionOpen()).toBe(true);
        expect(fixture.componentInstance.selectedEmployee()?.id).toBe('max');
      });
    });

    describe('Vergessen auszustempeln?', () => {
      const hint = (fixture: ComponentFixture<TerminalPage>) =>
        fixture.nativeElement.querySelector('.terminal-clock .forgot-hint') as HTMLButtonElement | null;

      it('shows the hint from 12 hours on', () => {
        const fixture = login({ ...working, startedAt: '2026-10-05T02:00:00Z' });

        expect(hint(fixture)?.textContent?.trim()).toBe('Vergessen auszustempeln?');
      });

      it('shows no hint for a shorter section', () => {
        const fixture = login({ ...working, startedAt: '2026-10-05T02:00:01Z' });

        expect(hint(fixture)).toBeNull();
      });

      it('has no hint when clocked out or paused', () => {
        const fixture = login({ ...status });
        expect(hint(fixture)).toBeNull();
      });

      it('clocks out the normal way and then opens "Ausstempeln nachtragen" for exactly that timesheet', () => {
        clockImpl.mockImplementation(() => of(clockedOut));
        const fixture = login(working);

        hint(fixture)!.click();
        fixture.detectChanges();

        expect(clockImpl).toHaveBeenCalledExactlyOnceWith('max', '1234', 'stop', null, null, expect.any(String));
        expect(fixture.componentInstance.correctionOpen()).toBe(true);
        expect(flowTitle(fixture)).toBe('Ende');
        expect(fixture.nativeElement.querySelector('app-correction-flow .flow-context')?.textContent).toContain('Nachtdienst');
        // No reset to the idle screen: the employee is still in the flow.
        vi.advanceTimersByTime(10_000);
        fixture.detectChanges();
        expect(fixture.componentInstance.selectedEmployee()?.id).toBe('max');

        tap(fixture, 'Weiter');
        tap(fixture, 'Absenden');
        expect(submitCorrection).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({
          kind: 'setEnd', timesheetId: 12, end: '2026-10-04T00:00', pin: '1234',
        }));
      });

      it('says "bitte Ende eintragen" in the session and drops that hint when the flow is closed', () => {
        clockImpl.mockImplementation(() => of(clockedOut));
        const fixture = login(working);
        hint(fixture)!.click();
        fixture.detectChanges();
        expect(fixture.componentInstance.message()).toBe('Ausgestempelt – bitte das tatsächliche Ende eintragen.');

        tap(fixture, 'Zurück');
        tap(fixture, 'Zurück');

        expect(fixture.componentInstance.correctionOpen()).toBe(false);
        expect(fixture.componentInstance.message()).toBe('');
        expect(fixture.componentInstance.selectedEmployee()?.id).toBe('max');
      });

      it('queues the stop offline and opens no correction (needs the server)', () => {
        const fixture = login(working);
        fixture.componentInstance.isOffline.set(true);
        fixture.detectChanges();

        hint(fixture)!.click();
        fixture.detectChanges();

        expect(clockImpl).not.toHaveBeenCalled();
        expect(enqueueKiosk).toHaveBeenCalledWith(expect.objectContaining({ action: 'stop' }));
        expect(fixture.componentInstance.correctionOpen()).toBe(false);
      });

      it('queues the stop when the live stop fails and opens no correction', () => {
        clockImpl.mockImplementation(() => throwError(() => ({ status: 0 })));
        const fixture = login(working);

        hint(fixture)!.click();
        fixture.detectChanges();

        expect(enqueueKiosk).toHaveBeenCalledWith(expect.objectContaining({ action: 'stop' }));
        expect(fixture.componentInstance.correctionOpen()).toBe(false);
      });

      it('opens no correction when the stop was refused for good', () => {
        clockImpl.mockImplementation(() => throwError(() => ({ status: 400 })));
        const fixture = login(working);

        hint(fixture)!.click();
        fixture.detectChanges();

        expect(fixture.componentInstance.correctionOpen()).toBe(false);
        expect(fixture.componentInstance.message()).toBe('Kimai konnte nicht speichern');
      });

      it('drops the result of the stop when the session ended meanwhile', () => {
        const stop = new Subject<ClockStatus>();
        clockImpl.mockImplementation(() => stop);
        const fixture = login(working);
        hint(fixture)!.click();

        fixture.componentInstance.back();
        stop.next(clockedOut);
        fixture.detectChanges();

        expect(fixture.componentInstance.correctionOpen()).toBe(false);
        expect(fixture.componentInstance.selectedEmployee()).toBeNull();
      });
    });

    describe('Arbeitszeit-Hinweise', () => {
      // Läuft seit 11:30 (Timesheet 23 der Testdaten), also unter 12 h.
      const running: ClockStatus = { ...working, activeTimesheetId: 23, startedAt: '2026-10-05T09:30:00Z' };
      const paused: ClockStatus = { ...status, isRunning: true, activeTimesheetId: 22, state: 'paused', stateText: 'Pause' };
      const pauseCase: WorkTimeHint = {
        kind: 'continuous', begin: '2026-10-05T07:58', end: null, workedSeconds: 22_000, timesheetId: 23,
      };
      const lastPauseCase: WorkTimeHint = {
        kind: 'continuous', begin: '2026-10-03T22:00', end: '2026-10-04T06:00', workedSeconds: 25_800, timesheetId: 12,
      };
      const lastShiftCase: WorkTimeHint = {
        kind: 'shift', begin: '2026-10-03T22:00', end: '2026-10-04T09:00', workedSeconds: 38_400, timesheetId: null,
      };
      const hints = (...list: WorkTimeHint[]) => of({ timeZone: 'Europe/Berlin', hints: list });
      const hintButton = (fixture: ComponentFixture<TerminalPage>) =>
        fixture.nativeElement.querySelector('.terminal-clock .forgot-hint') as HTMLButtonElement | null;
      const hintText = (fixture: ComponentFixture<TerminalPage>) =>
        hintButton(fixture)?.textContent?.replace(/\s+/g, ' ').trim();

      it('offers "Pause vergessen?" after a PIN login and opens "Pause nachtragen" for the running entry without stamping', () => {
        workTimeHints.mockReturnValue(hints(pauseCase));
        const fixture = login(running);

        expect(workTimeHints).toHaveBeenCalledExactlyOnceWith({ employeeId: 'max', pin: '1234', nfcCardId: null });
        expect(hintText(fixture)).toBe('Pause vergessen? seit 07:58 ohne Pause');
        expect(hintButton(fixture)!.querySelector('.detail')?.textContent).toBe('seit 07:58 ohne Pause');

        hintButton(fixture)!.click();
        fixture.detectChanges();

        expect(clockImpl).not.toHaveBeenCalled();
        expect(fixture.componentInstance.correctionStart()).toEqual({ kind: 'addPause', timesheetId: 23 });
        expect(flowTitle(fixture)).toBe('Pause beginnt');
        expect(document.activeElement?.classList.contains('flow-title')).toBe(true);
        tap(fixture, 'Weiter');
        tap(fixture, 'Weiter');
        tap(fixture, 'Absenden');
        expect(submitCorrection).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ kind: 'addPause', timesheetId: 23 }));
        expect(clockImpl).not.toHaveBeenCalled();
        expect(fixture.componentInstance.clockState.status()?.state).toBe('working');
      });

      it('shows no pause hint for a case that belongs to another entry', () => {
        workTimeHints.mockReturnValue(hints({ ...pauseCase, timesheetId: 21 }));
        const fixture = login(running);

        expect(hintButton(fixture)).toBeNull();
      });

      it('gives "Vergessen auszustempeln?" precedence', () => {
        workTimeHints.mockReturnValue(hints({ ...pauseCase, timesheetId: 12 }));
        const fixture = login({ ...working, startedAt: '2026-10-05T02:00:00Z' });

        expect(fixture.nativeElement.querySelectorAll('.terminal-clock .forgot-hint').length).toBe(1);
        expect(hintText(fixture)).toBe('Vergessen auszustempeln?');
      });

      it('names the last shift when clocked out and opens "Pause nachtragen" for the entry of the hint', () => {
        workTimeHints.mockReturnValue(hints(lastPauseCase));
        const fixture = login();

        expect(hintText(fixture)).toBe('Letzte Schicht: 7:10 Std. ohne Pause Prüfen');

        hintButton(fixture)!.click();
        fixture.detectChanges();

        expect(fixture.componentInstance.correctionStart()).toEqual({ kind: 'addPause', timesheetId: 12 });
        expect(flowTitle(fixture)).toBe('Pause beginnt');
        expect(fixture.nativeElement.querySelector('app-correction-flow .flow-context')?.textContent).toContain('Nachtdienst');
      });

      it('opens the choice of kinds for a shift over 10 hours', () => {
        workTimeHints.mockReturnValue(hints(lastShiftCase));
        const fixture = login();

        expect(hintText(fixture)).toBe('Letzte Schicht: 10:40 Std. Prüfen');

        hintButton(fixture)!.click();
        fixture.detectChanges();

        expect(fixture.componentInstance.correctionStart()).toBeNull();
        expect(flowTitle(fixture)).toBe('Korrektur');
      });

      it('shows only the pause hint when both cases of the last shift apply', () => {
        workTimeHints.mockReturnValue(hints(lastShiftCase, lastPauseCase));
        const fixture = login();

        expect(hintText(fixture)).toBe('Letzte Schicht: 7:10 Std. ohne Pause Prüfen');
      });

      it('has no last-shift hint once clocked in, and no hint during the pause', () => {
        workTimeHints.mockReturnValue(hints(lastPauseCase, lastShiftCase));
        expect(hintButton(login(running))).toBeNull();

        workTimeHints.mockReturnValue(hints({ ...pauseCase, end: '2026-10-05T15:00', timesheetId: 21 }, lastShiftCase));
        expect(hintButton(login(paused))).toBeNull();
      });

      it('hides the hint offline', () => {
        workTimeHints.mockReturnValue(hints(pauseCase));
        const fixture = login(running);
        expect(hintButton(fixture)).not.toBeNull();

        fixture.componentInstance.isOffline.set(true);
        fixture.detectChanges();

        expect(hintButton(fixture)).toBeNull();
      });

      it('asks nothing and shows nothing while stamps of the employee wait for transfer', () => {
        pendingQueue.set([{ kind: 'kiosk', event: { employeeId: 'max' } }]);
        workTimeHints.mockReturnValue(hints(lastPauseCase));
        const fixture = login();

        expect(workTimeHints).not.toHaveBeenCalled();
        fixture.componentInstance.workTimeHints.set({ timeZone: 'Europe/Berlin', hints: [lastPauseCase] });
        fixture.detectChanges();
        expect(hintButton(fixture)).toBeNull();
      });

      it('stays quiet when the hints cannot be read', () => {
        workTimeHints.mockReturnValue(throwError(() => ({ status: 503 })));
        const fixture = login();

        expect(hintButton(fixture)).toBeNull();
        expect(fixture.componentInstance.message()).toBe('');
      });

      it('loads the hints of a cached card only after the server confirmed it', () => {
        workTimeHints.mockReturnValue(hints(lastPauseCase));
        const identify$ = new Subject<NfcClockEvent>();
        vi.mocked(TestBed.inject(KioskApi).identify).mockImplementation(() => identify$);
        const fixture = loginWithCard();
        expect(workTimeHints).not.toHaveBeenCalled();

        identify$.next({
          eventId: 'e1', occurredAt: new Date().toISOString(), terminalId: 'term-1', cardId: '04ABCD',
          employee: session.employee, status, message: 'NFC-Karte erkannt.', success: true,
        });
        fixture.detectChanges();

        expect(workTimeHints).toHaveBeenCalledExactlyOnceWith({ employeeId: 'max', pin: '', nfcCardId: '04ABCD' });
        expect(hintText(fixture)).toBe('Letzte Schicht: 7:10 Std. ohne Pause Prüfen');
      });

      it('loads the hints after an uncached card was identified', () => {
        workTimeHints.mockReturnValue(hints(lastShiftCase));
        vi.mocked(TestBed.inject(KioskApi).identify).mockImplementation(() => of({
          eventId: 'e1', occurredAt: new Date().toISOString(), terminalId: 'term-1', cardId: '04ABCD',
          employee: session.employee, status, message: 'NFC-Karte erkannt.', success: true,
        }));
        localScanValue = { cardId: '04abcd', scannedAt: new Date().toISOString(), consumed: false };
        const fixture = TestBed.createComponent(TerminalPage);
        vi.advanceTimersByTime(1_000);
        fixture.detectChanges();

        expect(workTimeHints).toHaveBeenCalledExactlyOnceWith({ employeeId: 'max', pin: '', nfcCardId: '04ABCD' });
        expect(hintText(fixture)).toBe('Letzte Schicht: 10:40 Std. Prüfen');
      });

      it('discards a late answer after the identity changed - no hint of Max for Anna', () => {
        const late = new Subject<WorkTimeHints>();
        workTimeHints.mockReturnValue(late);
        const fixture = login();

        fixture.componentInstance.back();
        workTimeHints.mockReturnValue(hints());
        pinLogin.mockImplementation(() => of({ employee: { ...session.employee, id: 'anna', displayName: 'Anna Beispiel' }, status }));
        ['4', '3', '2', '1'].forEach(digit => fixture.componentInstance.pressDigit(digit));
        fixture.detectChanges();
        late.next({ timeZone: 'Europe/Berlin', hints: [lastPauseCase] });
        fixture.detectChanges();

        expect(fixture.componentInstance.selectedEmployee()?.id).toBe('anna');
        expect(fixture.componentInstance.workTimeHints()?.hints).toEqual([]);
        expect(hintButton(fixture)).toBeNull();
        expect(late.observed).toBe(false);
      });

      it('discards an answer that arrives after a stamp of the same session', () => {
        const late = new Subject<WorkTimeHints>();
        workTimeHints.mockReturnValue(late);
        clockImpl.mockImplementation(() => new Subject<ClockStatus>());
        const fixture = login(running);

        fixture.componentInstance.startPause();
        late.next({ timeZone: 'Europe/Berlin', hints: [pauseCase] });

        expect(fixture.componentInstance.workTimeHints()).toBeNull();
      });

      it('clears the hints on back()', () => {
        workTimeHints.mockReturnValue(hints(lastPauseCase));
        const fixture = login();
        expect(fixture.componentInstance.workTimeHints()).not.toBeNull();

        fixture.componentInstance.back();

        expect(fixture.componentInstance.workTimeHints()).toBeNull();
      });

      it('reloads the hints when the flow is closed - the old state cannot be tapped meanwhile', () => {
        workTimeHints.mockReturnValue(hints(lastPauseCase));
        const fixture = login();
        hintButton(fixture)!.click();
        fixture.detectChanges();

        const reload = new Subject<WorkTimeHints>();
        workTimeHints.mockReturnValue(reload);
        tap(fixture, 'Zurück');
        tap(fixture, 'Zurück');

        expect(fixture.componentInstance.correctionOpen()).toBe(false);
        expect(workTimeHints).toHaveBeenCalledTimes(2);
        expect(hintButton(fixture)).toBeNull();

        // An open request now suppresses the case on the server.
        reload.next({ timeZone: 'Europe/Berlin', hints: [] });
        fixture.detectChanges();
        expect(hintButton(fixture)).toBeNull();
      });
    });

    describe('Layout 800x480', () => {
      it('replaces the stamp buttons and the hours card in their column and keeps name, X and status', () => {
        const fixture = login(working);
        expect(fixture.nativeElement.querySelector('.action-panel')).not.toBeNull();
        expect(fixture.nativeElement.querySelector('.hours-overview')).not.toBeNull();
        // Korrektur sits in the employee column, never among the big stamp buttons.
        expect(fixture.nativeElement.querySelector('.action-panel app-correction-entry')).toBeNull();
        expect(fixture.nativeElement.querySelector('.session-panel app-correction-entry')).not.toBeNull();

        openFlow(fixture);

        const terminal = fixture.nativeElement.querySelector('.terminal') as HTMLElement;
        const flow = fixture.nativeElement.querySelector('app-correction-flow') as HTMLElement;
        expect(flow.parentElement).toBe(terminal);
        expect(flow.classList.contains('layout-terminal')).toBe(true);
        expect(fixture.nativeElement.querySelector('.action-panel')).toBeNull();
        expect(fixture.nativeElement.querySelector('.hours-overview')).toBeNull();
        expect(fixture.nativeElement.querySelector('.session-panel .back-button')).not.toBeNull();
        expect(fixture.nativeElement.querySelector('.session-panel h1')?.textContent).toContain('Max Mustermann');
        expect(fixture.nativeElement.querySelector('.session-panel .status-line')).not.toBeNull();
        // The entry button itself gives way to the flow.
        expect(entryButton(fixture)).toBeNull();
      });

      it('puts the work time hints into the clock column, never into the employee column, and hides them in the flow', () => {
        workTimeHints.mockReturnValue(of({
          timeZone: 'Europe/Berlin',
          hints: [{ kind: 'shift', begin: '2026-10-03T22:00', end: '2026-10-04T09:00', workedSeconds: 38_400, timesheetId: null }],
        }));
        const fixture = login();

        const hint = fixture.nativeElement.querySelector('.forgot-hint') as HTMLElement;
        expect(hint.closest('.terminal-clock')).not.toBeNull();
        expect(fixture.nativeElement.querySelector('.session-panel .forgot-hint')).toBeNull();

        openFlow(fixture);
        expect(fixture.nativeElement.querySelector('.forgot-hint')).toBeNull();
        // The time stays where it was.
        expect(fixture.nativeElement.querySelector('.terminal-clock .clock-time')).not.toBeNull();
      });

      it('scrolls only inside the flow: fixed head and footer around ONE scroll body', () => {
        const fixture = login(working);
        openFlow(fixture);
        tap(fixture, 'Pause nachtragen');

        const flow = fixture.nativeElement.querySelector('app-correction-flow') as HTMLElement;
        expect(flow.querySelectorAll('.flow-body').length).toBe(1);
        expect(flow.querySelector('.flow-body .flow-list')).not.toBeNull();
        expect(flow.querySelector('.flow-head')).not.toBeNull();
        expect(flow.querySelector('.flow-footer')).not.toBeNull();
        expect(flow.querySelector('.flow-body .flow-footer')).toBeNull();
        expect(flow.querySelector('.flow-body .flow-head')).toBeNull();
      });

      it('is not covered by the offline banner: the banner stays above the flow in the same grid', () => {
        const fixture = login(working);
        openFlow(fixture);
        fixture.componentInstance.isOffline.set(true);
        fixture.detectChanges();

        const terminal = fixture.nativeElement.querySelector('.terminal') as HTMLElement;
        const banner = terminal.querySelector('.offline-banner') as HTMLElement;
        const flow = terminal.querySelector('app-correction-flow') as HTMLElement;
        expect(banner.parentElement).toBe(terminal);
        expect(flow.parentElement).toBe(terminal);
        // In the flow of the document, not fixed over it: the banner is the first grid item.
        expect(terminal.firstElementChild).toBe(banner);
        expect(banner.compareDocumentPosition(flow) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
      });

      it('shows no notice about refused stamps during the session (it would cover the flow)', () => {
        rejectedStamps.set([{
          eventId: 'e1', action: 'stop', performedAt: '2026-10-05T10:00:00Z', employeeName: 'Max', message: 'abgelehnt',
        } as unknown as RejectedOfflineStamp]);
        const fixture = login(working);
        openFlow(fixture);

        expect(fixture.nativeElement.querySelector('.rejected-notice')).toBeNull();
      });

      it('keeps the focus visible: after every step the heading of the flow holds it', () => {
        const fixture = login();
        openFlow(fixture);
        expect(document.activeElement?.classList.contains('flow-title')).toBe(true);

        tap(fixture, 'Pause nachtragen');
        expect(document.activeElement?.classList.contains('flow-title')).toBe(true);
        expect(document.activeElement?.textContent?.trim()).toBe('Eintrag wählen');
      });
    });
  });
});
