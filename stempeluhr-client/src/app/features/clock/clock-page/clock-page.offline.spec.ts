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
        { provide: OfflineQueueService, useValue: { enqueueKiosk, syncNow: vi.fn(() => of([])) } },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: { get: (key: string) => (key === 'terminalId' ? 'term-1' : null) } } },
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

  it('keeps the terminal unlocked after an offline stamp and resets once the connection recovers', () => {
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

    // Connection recovered (polls succeed again): flush + deferred reset.
    failPolls = false;
    vi.advanceTimersByTime(1_000);
    expect(TestBed.inject(OfflineQueueService).syncNow).toHaveBeenCalled();
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
});