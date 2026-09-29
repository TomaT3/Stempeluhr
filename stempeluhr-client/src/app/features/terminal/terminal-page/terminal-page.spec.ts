import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import { signal } from '@angular/core';
import { of, Subject, throwError } from 'rxjs';

import { ClockStatus, HoursOverview, KioskEmployeeSession, NfcClockEvent } from '../../../core/models/kiosk.models';
import { RejectedOfflineStamp } from '../../../core/models/offline.models';
import { AudioFeedback } from '../../../core/services/audio-feedback';
import { lastKnownStatus, rememberProjectedStatus } from '../../../core/services/offline-cache';
import { KioskApi } from '../../../core/services/kiosk-api';
import { LocalNfcScan, LocalNfcScanService } from '../../../core/services/local-nfc-scan.service';
import { OfflineQueueService } from '../../../core/services/offline-queue';
import { TerminalPage } from './terminal-page';

describe('TerminalPage', () => {
  let pinLogin: ReturnType<typeof vi.fn>;
  let pinLoginResult: Subject<KioskEmployeeSession>;
  let hoursOverview: ReturnType<typeof vi.fn>;
  let clockImpl: ReturnType<typeof vi.fn>;
  let failPolls: boolean;
  let localScanValue: LocalNfcScan | null;
  let enqueueKiosk: ReturnType<typeof vi.fn>;
  let acknowledgeRejected: ReturnType<typeof vi.fn>;
  let pendingQueue: ReturnType<typeof signal<unknown[]>>;
  let rejectedStamps: ReturnType<typeof signal<RejectedOfflineStamp[]>>;
  let recovered$: Subject<void>;

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
    pinLogin = vi.fn(() => pinLoginResult);
    clockImpl = vi.fn(() => throwError(() => ({ status: 0 })));
    failPolls = false;
    localScanValue = null;
    enqueueKiosk = vi.fn();
    acknowledgeRejected = vi.fn();
    pendingQueue = signal<unknown[]>([]);
    rejectedStamps = signal<RejectedOfflineStamp[]>([]);
    recovered$ = new Subject<void>();

    await TestBed.configureTestingModule({
      imports: [TerminalPage],
      providers: [
        {
          provide: KioskApi,
          useValue: {
            pinLogin,
            clock: clockImpl,
            hoursOverview,
            ping: vi.fn(() =>
              failPolls ? throwError(() => ({ status: 0 })) : of({ ok: true, version: null, configuredEmployees: 0, settingsConfigured: true }),
            ),
            identify: vi.fn(() => new Subject<NfcClockEvent>()),
            health: vi.fn(() => of({ ok: true, version: null, configuredEmployees: 0, settingsConfigured: true })),
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
        scanCachedCard();

        identify$.error({ status: 429 });

        expect(TestBed.inject(OfflineQueueService).authorizeEmployeeCard).not.toHaveBeenCalled();
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
});
