import { TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';

import { ClockStatus, KioskEmployeeSession, NfcLatestEvent } from '../../../core/models/kiosk.models';
import { AudioFeedback } from '../../../core/services/audio-feedback';
import { KioskApi } from '../../../core/services/kiosk-api';
import { OfflineQueueService } from '../../../core/services/offline-queue';
import { ClockPage } from './clock-page';

describe('ClockPage offline behaviour', () => {
  let pinLoginResult: Subject<KioskEmployeeSession>;
  let clockResult: Subject<ClockStatus>;
  let latestNfcValue: NfcLatestEvent;
  let failPolls: boolean;
  let recovered$: Subject<void>;
  let terminalIdValue: string | null;
  let enqueueKiosk: ReturnType<typeof vi.fn>;
  let playBeeps: ReturnType<typeof vi.fn>;

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

  beforeEach(async () => {
    pinLoginResult = new Subject<KioskEmployeeSession>();
    clockResult = new Subject<ClockStatus>();
    latestNfcValue = { event: null };
    failPolls = false;
    recovered$ = new Subject<void>();
    terminalIdValue = 'term-1';
    enqueueKiosk = vi.fn();
    playBeeps = vi.fn();

    await TestBed.configureTestingModule({
      imports: [ClockPage],
      providers: [
        {
          provide: KioskApi,
          useValue: {
            pinLogin: vi.fn(() => pinLoginResult),
            clock: vi.fn(() => clockResult),
            latestNfcEvent: vi.fn(() =>
              failPolls ? throwError(() => ({ status: 0 })) : of(latestNfcValue),
            ),
          },
        },
        { provide: AudioFeedback, useValue: { playBeeps } },
        {
          provide: OfflineQueueService,
          useValue: { enqueueKiosk, syncNow: vi.fn(() => of([])), recovered: recovered$.asObservable(), pendingCount: vi.fn(() => []) },
        },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: { get: (key: string) => (key === 'terminalId' ? terminalIdValue : null) } } },
        },
      ],
    }).compileComponents();

    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  function createComponent() {
    return TestBed.createComponent(ClockPage);
  }

  it('keeps the terminal unlocked after an offline stamp and resets only once events are processed', () => {
    const fixture = createComponent();
    const component = fixture.componentInstance;
    component.pressDigit('1');
    component.pressDigit('2');
    component.pressDigit('3');
    component.pressDigit('4');
    pinLoginResult.next(session);
    expect(component.isUnlocked()).toBe(true);

    // Kimai/backend unreachable: the action is queued instead.
    failPolls = true; // polls keep failing while offline
    component.start();
    clockResult.error({ status: 0 });

    expect(enqueueKiosk).toHaveBeenCalledTimes(1);
    expect(component.message()).toContain('Offline gespeichert');
    expect(component.isBusy()).toBe(false);

    // The terminal must NOT fall back to the locked idle screen while
    // offline: unlocking again needs a PIN login, which cannot work now.
    vi.advanceTimersByTime(5_000);
    expect(component.selectedEmployee()).not.toBeNull();
    expect(component.isUnlocked()).toBe(true);

    // Polls succeed again (API reachable) - the flush is triggered, but the
    // server only BUFFERS the events (Kimai still down). The recovered
    // signal does NOT fire, so the terminal must stay unlocked: a PIN login
    // is still impossible and resetting would lock the employee out.
    failPolls = false;
    vi.advanceTimersByTime(1_000);
    expect(TestBed.inject(OfflineQueueService).syncNow).toHaveBeenCalled();
    expect(component.isUnlocked()).toBe(true);
    expect(component.selectedEmployee()).not.toBeNull();

    // Now the server actually processed the events -> recovered fires and
    // the deferred reset runs.
    recovered$.next();
    expect(component.isUnlocked()).toBe(false);
    expect(component.selectedEmployee()).toBeNull();
  });

  it('reports an unreachable backend instead of a wrong PIN when the login fails offline', () => {
    const fixture = createComponent();
    const component = fixture.componentInstance;
    failPolls = true;
    component.pressDigit('1');
    component.pressDigit('2');
    component.pressDigit('3');
    component.pressDigit('4');
    pinLoginResult.error({ status: 0 });

    expect(component.message()).not.toContain('PIN nicht gefunden');
    expect(component.message()).toContain('Offline');
    expect(playBeeps).toHaveBeenCalledWith(2);
  });

  it('still reports a wrong PIN for permanent (4xx) login failures', () => {
    const fixture = createComponent();
    const component = fixture.componentInstance;
    component.pressDigit('1');
    component.pressDigit('2');
    component.pressDigit('3');
    component.pressDigit('4');
    pinLoginResult.error({ status: 401 });

    expect(component.message()).toBe('PIN nicht gefunden');
  });

  it('releases the terminal via the offline queue recovery signal even without NFC polling', () => {
    // /clock default route: no terminalId -> no NFC poll, so the queue's own
    // recovered signal is the only connectivity indicator.
    terminalIdValue = null;
    const fixture = createComponent();
    const component = fixture.componentInstance;

    failPolls = true; // irrelevant here, but mirrors the offline situation
    component.pressDigit('1');
    component.pressDigit('2');
    component.pressDigit('3');
    component.pressDigit('4');
    pinLoginResult.next(session);
    expect(component.isUnlocked()).toBe(true);

    component.stop();
    clockResult.error({ status: 0 });
    expect(enqueueKiosk).toHaveBeenCalledTimes(1);
    expect(component.isOffline()).toBe(true);

    // No auto-reset while offline, and no poll to recover from.
    vi.advanceTimersByTime(5_000);
    expect(component.isUnlocked()).toBe(true);

    // The offline queue reports a successful sync: release the terminal.
    recovered$.next();
    expect(component.isOffline()).toBe(false);
    expect(component.isUnlocked()).toBe(false);
    expect(component.selectedEmployee()).toBeNull();
  });

  it('shows the offline banner while offline but keeps all stamp actions available', () => {
    const fixture = createComponent();
    fixture.detectChanges();
    const component = fixture.componentInstance;

    // No banner while online.
    expect(fixture.nativeElement.querySelector('.offline-banner')).toBeNull();

    // Login, then go offline while working.
    component.pressDigit('1');
    component.pressDigit('2');
    component.pressDigit('3');
    component.pressDigit('4');
    const workingStatus: ClockStatus = { ...status, isRunning: true, state: 'working', stateText: 'Eingestempelt' };
    pinLoginResult.next({ employee: session.employee, status: workingStatus });
    fixture.detectChanges();

    failPolls = true;
    component.startPause();
    clockResult.error({ status: 0 });
    fixture.detectChanges();

    expect(component.isOffline()).toBe(true);

    // Banner is visible and says stamps are queued (no false "no pause"
    // limitation: the explicit kiosk path supports pause catch-up).
    const banner = fixture.nativeElement.querySelector('.offline-banner');
    expect(banner).not.toBeNull();
    expect(banner.textContent).toContain('OFFLINE-BETRIEB');
    expect(banner.textContent).toContain('nachgetragen');
    expect(banner.textContent).not.toContain('keine Pause');

    // All stamp actions stay available - the kiosk queue supports pause.
    const pauseButton = fixture.nativeElement.querySelector('.stamp-button.pause');
    expect(pauseButton).not.toBeNull();
    const stopButton = fixture.nativeElement.querySelector('.stamp-button.stop');
    expect(stopButton).not.toBeNull();
  });

  it('shows the pending queue count in the offline banner', () => {
    const fixture = createComponent();
    const component = fixture.componentInstance;
    const queue = TestBed.inject(OfflineQueueService) as unknown as {
      pendingCount: ReturnType<typeof vi.fn>;
    };
    // Two stamps are waiting to be synced.
    queue.pendingCount.mockReturnValue([{}, {}]);

    component.pressDigit('1');
    component.pressDigit('2');
    component.pressDigit('3');
    component.pressDigit('4');
    const workingStatus: ClockStatus = { ...status, isRunning: true, state: 'working', stateText: 'Eingestempelt' };
    pinLoginResult.next({ employee: session.employee, status: workingStatus });
    fixture.detectChanges();

    failPolls = true;
    component.startPause();
    clockResult.error({ status: 0 });
    fixture.detectChanges();

    expect(component.isOffline()).toBe(true);
    const count = fixture.nativeElement.querySelector('.offline-banner .offline-count');
    expect(count).not.toBeNull();
    expect(count.textContent.trim()).toBe('2');
  });
});