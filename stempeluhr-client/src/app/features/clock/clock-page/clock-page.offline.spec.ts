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
          useValue: { enqueueKiosk, syncNow: vi.fn(() => of([])), recovered: recovered$.asObservable() },
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
  it('keeps pause actions usable offline and hides the banner only on recovery', async () => {
    const fixture = createComponent();
    const component = fixture.componentInstance;

    component.pressDigit('1');
    component.pressDigit('2');
    component.pressDigit('3');
    component.pressDigit('4');
    pinLoginResult.next(session);
    expect(component.isUnlocked()).toBe(true);

    // Working state: Pause + Ausstempeln are offered.
    component.clockState.setStatus({
      ...status,
      isRunning: true,
      activeTimesheetId: 42,
      startedAt: '2026-08-24T08:00:00Z',
      durationSeconds: 600,
      state: 'working',
      stateText: 'Eingestempelt',
    });
    fixture.detectChanges();

    // Offline transition via an ALLOWED action: stop()'s request hangs and
    // its failure arrives much later - the queued stamp must still carry
    // the ACTION's timestamp, not the late error time.
    failPolls = true;
    const stampIso = new Date().toISOString();
    component.stop();
    await vi.advanceTimersByTimeAsync(30_000);
    clockResult.error({ status: 0 });

    expect(enqueueKiosk).toHaveBeenCalledTimes(1);
    expect(enqueueKiosk.mock.calls[0][0].performedAt).toBe(stampIso);
    // PIN-opened session: no card id may leak into the queued event.
    expect(enqueueKiosk.mock.calls[0][0].nfcCardId ?? null).toBeNull();

    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.offline-banner')).not.toBeNull();

    // The pause button must remain visible AND usable while offline.
    const pauseButton = fixture.nativeElement.querySelector('.stamp-button.pause') as HTMLButtonElement;
    expect(pauseButton).not.toBeNull();
    expect(pauseButton.disabled).toBe(false);

    // Paused state: 'Pause beenden' must stay usable offline as well - an
    // employee in a break must be able to end it (Issue #11 browser queue).
    component.clockState.setStatus({
      ...status,
      isRunning: false,
      activeTimesheetId: 42,
      startedAt: '2026-08-24T08:05:00Z',
      durationSeconds: 300,
      state: 'paused',
      stateText: 'In Pause',
    });
    fixture.detectChanges();
    const resumeButton = fixture.nativeElement.querySelector('.stamp-button.start') as HTMLButtonElement;
    expect(resumeButton).not.toBeNull();
    expect(resumeButton.textContent).toContain('Pause beenden');
    expect(resumeButton.disabled).toBe(false);

    // Recovery: the banner disappears, the action buttons survive.
    component.isOffline.set(false);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.offline-banner')).toBeNull();
    expect(fixture.nativeElement.querySelector('.stamp-button.start')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('.stamp-button.stop')).not.toBeNull();
  });

  it('queues an offline stamp of an NFC-unlocked session with its card id', () => {
    const fixture = createComponent();
    const component = fixture.componentInstance;

    // Unlock via NFC touch on the polled terminal: no pin entry happened.
    latestNfcValue = {
      event: {
        eventId: 'ev-nfc-1',
        occurredAt: new Date().toISOString(),
        terminalId: 'term-1',
        cardId: '04AB',
        employee: session.employee,
        status,
        message: 'NFC-Karte erkannt.',
        success: true,
      },
    };
    vi.advanceTimersByTime(1_000);
    expect(component.isUnlocked()).toBe(true);

    // Backend unreachable: the stamp queues for replay ...
    failPolls = true;
    component.start();
    clockResult.error({ status: 0 });

    // ... and MUST carry the card id so the server replay can resolve the
    // employee without a pin (live-path parity) instead of rejecting it as
    // "pin wrong" forever.
    expect(enqueueKiosk).toHaveBeenCalledTimes(1);
    const queued = enqueueKiosk.mock.calls[0][0];
    expect(queued.employeeId).toBe('max');
    expect(queued.nfcCardId).toBe('04AB');
  });
});
