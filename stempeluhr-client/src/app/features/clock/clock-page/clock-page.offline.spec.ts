import { TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import { signal } from '@angular/core';
import { Observable, Subject, TimeoutError, of, throwError } from 'rxjs';

import { ClockStatus, KioskEmployeeSession, NfcClockEvent } from '../../../core/models/kiosk.models';
import { RejectedOfflineStamp } from '../../../core/models/offline.models';
import { AudioFeedback } from '../../../core/services/audio-feedback';
import { KioskApi } from '../../../core/services/kiosk-api';
import { LocalNfcScan, LocalNfcScanService } from '../../../core/services/local-nfc-scan.service';
import { lastKnownStatus, rememberEmployeePin, rememberObservedStatus, resolveEmployeeByPin } from '../../../core/services/offline-cache';
import { OfflineQueueService } from '../../../core/services/offline-queue';
import { ClockPage } from './clock-page';

describe('ClockPage offline behaviour', () => {
  let pinLoginResult: Subject<KioskEmployeeSession>;
  let clockResult: Subject<ClockStatus>;
  let failPolls: boolean;
  let recovered$: Subject<void>;
  let terminalIdValue: string | null;
  let enqueueKiosk: ReturnType<typeof vi.fn>;
  let acknowledgeRejected: ReturnType<typeof vi.fn>;
  let rejectedStamps: ReturnType<typeof signal<RejectedOfflineStamp[]>>;
  let syncNow: ReturnType<typeof vi.fn>;
  let pendingQueue: ReturnType<typeof signal<unknown[]>>;
  let healthResult: Observable<unknown>;
  let healthApi: ReturnType<typeof vi.fn>;
  /** Wenn gesetzt: jede Health-Anfrage bekommt ein eigenes Subject (Reihenfolge = Aufrufreihenfolge). */
  let healthSubjects: Subject<unknown>[] | null;
  let playBeeps: ReturnType<typeof vi.fn>;
  let localAck: ReturnType<typeof vi.fn>;
  let localScanValue: LocalNfcScan | null;
  /** Online card identification (kioskApi.identify); unknown by default. */
  let identifyValue: Subject<NfcClockEvent>;

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
    window.localStorage.clear();
    pinLoginResult = new Subject<KioskEmployeeSession>();
    clockResult = new Subject<ClockStatus>();
    failPolls = false;
    recovered$ = new Subject<void>();
    terminalIdValue = 'term-1';
    enqueueKiosk = vi.fn();
    acknowledgeRejected = vi.fn();
    rejectedStamps = signal<RejectedOfflineStamp[]>([]);
    syncNow = vi.fn(() => of([]));
    pendingQueue = signal<unknown[]>([]);
    healthResult = of({ ok: true, version: null, configuredEmployees: 0, settingsConfigured: true });
    // Der Health-Endpunkt wird auch vom Versions-Badge abgefragt; Tests, die
    // genau die Anfrage des Workflows brauchen, setzen healthSubjects = [].
    healthSubjects = null;
    healthApi = vi.fn(() => {
      if (healthSubjects) {
        const subject = new Subject<unknown>();
        healthSubjects.push(subject);
        return subject.asObservable();
      }
      return healthResult;
    });
    playBeeps = vi.fn();
    localAck = vi.fn(() => of(null));
    localScanValue = null;
    // Default: identify fails like an unreachable backend would - the card
    // stays unknown. Tests that need an online match replace this.
    identifyValue = new Subject<NfcClockEvent>();

    await TestBed.configureTestingModule({
      imports: [ClockPage],
      providers: [
        {
          provide: KioskApi,
          useValue: {
            pinLogin: vi.fn(() => pinLoginResult),
            clock: vi.fn(() => clockResult),
            ping: vi.fn(() =>
              failPolls ? throwError(() => ({ status: 0 })) : of({ ok: true, version: null, configuredEmployees: 0, settingsConfigured: true }),
            ),
            hoursOverview: vi.fn(() => of(null)),
            identify: vi.fn(() => identifyValue),
            health: healthApi,
          },
        },
        { provide: AudioFeedback, useValue: { playBeeps } },
        {
          provide: LocalNfcScanService,
          useValue: {
            poll: vi.fn(() => of(localScanValue)),
            ack: localAck,
          },
        },
        {
          provide: OfflineQueueService,
          useValue: {
            enqueueKiosk,
            syncNow,
            recovered: recovered$.asObservable(),
            rejected: rejectedStamps.asReadonly(),
            acknowledgeRejected,
            pendingCount: pendingQueue.asReadonly(),
          },
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
    window.localStorage.clear();
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

  it('reports an unreachable backend instead of a wrong PIN when the login fails offline', async () => {
    const fixture = createComponent();
    const component = fixture.componentInstance;
    failPolls = true;
    component.pressDigit('1');
    component.pressDigit('2');
    component.pressDigit('3');
    component.pressDigit('4');
    pinLoginResult.error({ status: 0 });

    // Nothing was ever cached for this PIN: the kiosk may only admit that it
    // cannot check the PIN. The fallback hashes the entered PIN against the
    // cached verifiers before it answers - hence the await.
    await vi.waitFor(() => expect(component.message()).toContain('Offline'));
    expect(component.message()).not.toContain('PIN nicht gefunden');
    expect(component.isUnlocked()).toBe(false);
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
    // /clock default route: no terminalId -> no NFC poll; connectivity comes
    // from the health poll and from the queue's own recovered signal.
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

    // Go offline: the banner appears, the running-state actions stay usable.
    failPolls = true;
    vi.advanceTimersByTime(1_000);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.offline-banner')).not.toBeNull();

    const pauseButton = fixture.nativeElement.querySelector('.stamp-button.pause') as HTMLButtonElement;
    expect(pauseButton).not.toBeNull();
    expect(pauseButton.disabled).toBe(false);

    // Offline transition via an ALLOWED action: stop()'s request hangs and
    // its failure arrives much later - the queued stamp must still carry
    // the ACTION's timestamp, not the late error time.
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

    // The queued Ausstempeln is reflected locally: the kiosk stops offering
    // Pause/Ausstempeln for a state it just queued away and offers the way
    // back in instead.
    expect(fixture.nativeElement.querySelector('.stamp-button.pause')).toBeNull();
    expect(fixture.nativeElement.querySelector('.stamp-button.start')?.textContent).toContain('Einstempeln');

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

    // Unlock via card touch while online: the kiosk resolves the card via
    // identify - no pin entry happened.
    localScanValue = { cardId: '04ab', scannedAt: new Date().toISOString(), consumed: false };
    vi.advanceTimersByTime(1_000);
    identifyValue.next({
      eventId: 'ev-1',
      occurredAt: new Date().toISOString(),
      terminalId: 'term-1',
      cardId: '04AB',
      employee: session.employee,
      status,
      message: 'NFC-Karte erkannt.',
      success: true,
    });
    expect(component.isUnlocked()).toBe(true);

    // Backend unreachable: the stamp queues for replay ...
    component.start();
    clockResult.error({ status: 0 });

    // ... and MUST carry the card id so the server replay can resolve the
    // employee without a pin (live-path parity) instead of rejecting it as
    // "pin wrong" forever.
    expect(enqueueKiosk).toHaveBeenCalledTimes(1);
    const queued = enqueueKiosk.mock.calls[0][0];
    expect(queued.employeeId).toBe('max');
    expect(queued.pin).toBe('');
    expect(queued.nfcCardId).toBe('04AB');
  });
  it('unlocks the employee from a local agent scan while offline and acks it', () => {
    failPolls = true; // backend unreachable -> offline mode
    const fixture = createComponent();
    const component = fixture.componentInstance;

    // Card catalog cached from an earlier ONLINE NFC event.
    window.localStorage.setItem(
      'stempeluhr.employee-card-cache.v1',
      JSON.stringify({ '04ABCD': session.employee }),
    );

    // The local agent reports a fresh, unconsumed scan.
    localScanValue = { cardId: '04abcd', scannedAt: new Date().toISOString(), consumed: false };
    vi.advanceTimersByTime(1_000);

    expect(component.isOffline()).toBe(true);
    expect(component.selectedEmployee()?.id).toBe('max');
    expect(component.isUnlocked()).toBe(true);
    expect(component.message()).toContain('Max Mustermann');
    expect(playBeeps).toHaveBeenCalledWith(1);
    expect(localAck).toHaveBeenCalledTimes(1);

    // Normal buttons keep working and go through the offline queue.
    component.start();
    clockResult.error({ status: 0 });
    const queued = enqueueKiosk.mock.calls[0][0];
    expect(queued.employeeId).toBe('max');
    expect(queued.nfcCardId).toBe('04ABCD');
  });

  it('refreshes the status after an ONLINE cache-hit scan', () => {
    // Backend reachable: the card is in the local cache, so the employee is
    // unlocked immediately - and the fresh status is loaded from the server
    // in the background (the cache itself stores no status).
    const fixture = createComponent();
    const component = fixture.componentInstance;

    window.localStorage.setItem(
      'stempeluhr.employee-card-cache.v1',
      JSON.stringify({ '04ABCD': session.employee }),
    );

    localScanValue = { cardId: '04abcd', scannedAt: new Date().toISOString(), consumed: false };
    vi.advanceTimersByTime(1_000);

    // Unlocked right away from the cache, no status applied yet.
    expect(component.selectedEmployee()?.id).toBe('max');
    expect(component.isUnlocked()).toBe(true);
    expect(component.clockState.status()).toBeNull();

    // The background identify refreshes the status.
    identifyValue.next({
      eventId: 'ev-ident-1',
      occurredAt: new Date().toISOString(),
      terminalId: 'term-1',
      cardId: '04ABCD',
      employee: session.employee,
      status: { ...status, isRunning: true, state: 'working', stateText: 'Eingestempelt' },
      message: 'NFC-Karte erkannt.',
      success: true,
    });

    expect(component.clockState.status()?.state).toBe('working');
  });

  it('resolves an uncached card ONLINE via identify, caches it and unlocks', () => {
    // Backend reachable: the cache miss goes to the server, which knows the
    // card even though this browser never saw it before.
    const fixture = createComponent();
    const component = fixture.componentInstance;

    localScanValue = { cardId: '7788AA', scannedAt: new Date().toISOString(), consumed: false };
    vi.advanceTimersByTime(1_000);

    expect(localAck).toHaveBeenCalledTimes(1);
    expect(component.isUnlocked()).toBe(false);

    identifyValue.next({
      eventId: 'ev-ident-1',
      occurredAt: new Date().toISOString(),
      terminalId: 'term-1',
      cardId: '7788AA',
      employee: session.employee,
      status,
      message: 'NFC-Karte erkannt.',
      success: true,
    });

    expect(component.selectedEmployee()?.id).toBe('max');
    expect(component.isUnlocked()).toBe(true);
    expect(component.message()).toContain('Max Mustermann');

    // The pair is cached so a later OFFLINE scan of the same card works.
    const cache = JSON.parse(window.localStorage.getItem('stempeluhr.employee-card-cache.v1') ?? '{}');
    expect(cache['7788AA'].id).toBe('max');

    // The fresh status from the server is applied (online identify).
    expect(component.clockState.status()).toEqual(status);
  });

  it('reports an unknown card for a local scan without a cached employee', () => {
    failPolls = true;
    const component = createComponent().componentInstance;

    localScanValue = { cardId: 'FFFF01', scannedAt: new Date().toISOString(), consumed: false };
    vi.advanceTimersByTime(1_000);

    expect(localAck).toHaveBeenCalledTimes(1);
    expect(component.isUnlocked()).toBe(false);

    // Offline the identify call is skipped entirely (it could only fail or
    // hang) - the cache miss answers immediately.
    expect(component.message()).toBe('Unbekannte Karte');
  });

  it('reports the server message for an uncached card that is unknown ONLINE', () => {
    // Backend reachable: the cache miss goes to identify, and an unknown
    // card (success: false) keeps the terminal locked with the server's
    // message.
    const component = createComponent().componentInstance;

    localScanValue = { cardId: 'FFFF01', scannedAt: new Date().toISOString(), consumed: false };
    vi.advanceTimersByTime(1_000);

    expect(localAck).toHaveBeenCalledTimes(1);
    expect(component.isUnlocked()).toBe(false);

    identifyValue.next({
      eventId: 'ev-ident-1',
      occurredAt: new Date().toISOString(),
      terminalId: 'term-1',
      cardId: 'FFFF01',
      employee: null,
      status: null,
      message: 'NFC-Karte ist keinem Mitarbeiter zugeordnet.',
      success: false,
    });

    expect(component.message()).toBe('NFC-Karte ist keinem Mitarbeiter zugeordnet.');
  });

  it('acks scans even while UNLOCKED so the agent fallback never fires on them', () => {
    // Unlock first (offline card login), then a second tap must be consumed
    // (ack) WITHOUT switching employees - otherwise every tap would block
    // the agent reader loop for the selection timeout and could fire a
    // phantom toggle from the stale status cache.
    window.localStorage.setItem(
      'stempeluhr.employee-card-cache.v1',
      JSON.stringify({ '04ABCD': session.employee }),
    );
    failPolls = true;
    const fixture = createComponent();
    const component = fixture.componentInstance;

    localScanValue = { cardId: '04abcd', scannedAt: new Date().toISOString(), consumed: false };
    vi.advanceTimersByTime(1_000);
    expect(component.isUnlocked()).toBe(true);
    expect(localAck).toHaveBeenCalledTimes(1);

    // Second tap while unlocked.
    localScanValue = { cardId: '04abcd', scannedAt: new Date(Date.now() + 5_000).toISOString(), consumed: false };
    vi.advanceTimersByTime(1_000);

    expect(localAck).toHaveBeenCalledTimes(2);
    // No employee switch happened (still the same session, still unlocked,
    // and no new stamp was queued).
    expect(enqueueKiosk).not.toHaveBeenCalled();
  });

  it('acks scans even while BUSY so a pending stamp request cannot starve the agent', () => {
    // Regression pin: a stamp request can run up to its timeout - while
    // isBusy stays true, polls must STILL consume scans (ack only, no
    // employee switch). Online, so the request is actually sent.
    window.localStorage.setItem(
      'stempeluhr.employee-card-cache.v1',
      JSON.stringify({ '04ABCD': session.employee }),
    );
    const fixture = createComponent();
    const component = fixture.componentInstance;

    localScanValue = { cardId: '04abcd', scannedAt: new Date().toISOString(), consumed: false };
    vi.advanceTimersByTime(1_000);
    expect(component.isUnlocked()).toBe(true);

    // Start an action whose clock request never settles -> isBusy stays true.
    component.start(); // clockResult never emits/errors

    localScanValue = { cardId: '04abcd', scannedAt: new Date(Date.now() + 5_000).toISOString(), consumed: false };
    vi.advanceTimersByTime(1_000);

    expect(component.isBusy()).toBe(true);
    expect(localAck).toHaveBeenCalledTimes(2);
    // The busy action must not be interrupted or double-queued.
    expect(enqueueKiosk).not.toHaveBeenCalled();
  });

  it('keeps the pressed employee in a queued event even if identity changes while the request hangs', () => {
    // The REAL regression pin for the press-time identity snapshot:
    // 1) max unlocks via PIN and presses START - the clock request hangs.
    // 2) back() locks the terminal and clears selectedEmployee/nfcCardId
    //    while the subscription is still alive.
    // 3) A scan then unlocks BERTA.
    // 4) Only NOW does the hung request fail offline.
    // The queued event must carry max (press-time snapshot), never berta's
    // id or an empty string - against 1e5f388 this test fails with ''.
    // Online, so the request is actually sent (and hangs).
    const fixture = createComponent();
    const component = fixture.componentInstance;

    window.localStorage.setItem(
      'stempeluhr.employee-card-cache.v1',
      JSON.stringify({
        '04ABCD': session.employee,
        '04BB': { ...session.employee, id: 'berta', displayName: 'Berta Beispiel' },
      }),
    );

    component.pressDigit('1');
    component.pressDigit('2');
    component.pressDigit('3');
    component.pressDigit('4');
    pinLoginResult.next(session);
    expect(component.isUnlocked()).toBe(true);

    component.start(); // hangs: clockResult never settles (yet)
    expect(component.isBusy()).toBe(true);

    component.back();
    expect(component.isUnlocked()).toBe(false);
    expect(component.selectedEmployee()).toBeNull();

    localScanValue = { cardId: '04bb', scannedAt: new Date().toISOString(), consumed: false };
    vi.advanceTimersByTime(1_000);
    expect(component.selectedEmployee()?.id).toBe('berta');

    // Only now does the hung request fail offline.
    clockResult.error({ status: 0 });

    expect(enqueueKiosk).toHaveBeenCalledTimes(1);
    const queued = enqueueKiosk.mock.calls[0][0];
    // Press-time snapshot wins: max started the action, not berta.
    expect(queued.employeeId).toBe('max');
    expect(queued.action).toBe('start');
  });

  it('does not stack connectivity polls while one is still pending', () => {
    const kioskApi = TestBed.inject(KioskApi) as unknown as { ping: ReturnType<typeof vi.fn> };
    const hanging = new Subject<unknown>();
    kioskApi.ping.mockReturnValue(hanging);
    createComponent();

    vi.advanceTimersByTime(5_000);
    expect(kioskApi.ping).toHaveBeenCalledTimes(1);

    // Once the pending poll settles, polling resumes.
    hanging.error({ status: 0 });
    vi.advanceTimersByTime(1_000);
    expect(kioskApi.ping).toHaveBeenCalledTimes(2);
  });

  it('queues immediately without a request while the outage is already known', () => {
    window.localStorage.setItem(
      'stempeluhr.employee-card-cache.v1',
      JSON.stringify({ '04ABCD': session.employee }),
    );
    failPolls = true;
    const fixture = createComponent();
    const component = fixture.componentInstance;
    const kioskApi = TestBed.inject(KioskApi) as unknown as { clock: ReturnType<typeof vi.fn> };

    localScanValue = { cardId: '04abcd', scannedAt: new Date().toISOString(), consumed: false };
    vi.advanceTimersByTime(1_000);
    expect(component.isOffline()).toBe(true);
    expect(component.isUnlocked()).toBe(true);

    component.start();

    // No request that could only time out - the stamp is queued right away.
    expect(kioskApi.clock).not.toHaveBeenCalled();
    expect(enqueueKiosk).toHaveBeenCalledTimes(1);
    expect(enqueueKiosk.mock.calls[0][0]).toMatchObject({ employeeId: 'max', action: 'start', nfcCardId: '04ABCD' });
    expect(component.isBusy()).toBe(false);
    expect(component.message()).toContain('Offline gespeichert');
    expect(component.clockState.status()?.state).toBe('working');
  });

  it('queues the stamp when the request times out', () => {
    const fixture = createComponent();
    const component = fixture.componentInstance;
    component.pressDigit('1');
    component.pressDigit('2');
    component.pressDigit('3');
    component.pressDigit('4');
    pinLoginResult.next(session);

    component.start();
    expect(component.isBusy()).toBe(true);
    // rxjs TimeoutError carries no HTTP status -> offline path.
    clockResult.error(new TimeoutError());

    expect(enqueueKiosk).toHaveBeenCalledTimes(1);
    expect(enqueueKiosk.mock.calls[0][0]).toMatchObject({ employeeId: 'max', pin: '1234', action: 'start' });
    expect(component.isOffline()).toBe(true);
    expect(component.isBusy()).toBe(false);
  });

  it('shows an honest unknown-status badge instead of "ausgestempelt" after an offline card login', () => {
    failPolls = true;
    window.localStorage.setItem(
      'stempeluhr.employee-card-cache.v1',
      JSON.stringify({ '04ABCD': session.employee }),
    );
    const fixture = createComponent();

    // No PIN login, no NFC event -> clockState.status stays null.
    localScanValue = { cardId: '04abcd', scannedAt: new Date().toISOString(), consumed: false };
    vi.advanceTimersByTime(1_000);
    fixture.detectChanges();

    expect(fixture.componentInstance.clockState.status()).toBeNull();
    const badge = fixture.nativeElement.querySelector('app-status-badge');
    expect(badge?.textContent).toContain('Status unbekannt');
    expect(fixture.nativeElement.textContent).not.toContain('Nicht eingestempelt');

    // The stamp buttons must stay available despite the unknown status.
    expect(fixture.nativeElement.querySelector('.stamp-button.start')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('.stamp-button.stop')).not.toBeNull();
  });

  it('does not claim "offline" while the backend is answering', () => {
    // ONLINE cache hit: the employee is unlocked before the server's status
    // answer arrives - "unbekannt" is fine then, "offline" is not.
    window.localStorage.setItem(
      'stempeluhr.employee-card-cache.v1',
      JSON.stringify({ '04ABCD': session.employee }),
    );
    const fixture = createComponent();

    localScanValue = { cardId: '04abcd', scannedAt: new Date().toISOString(), consumed: false };
    vi.advanceTimersByTime(1_000);
    fixture.detectChanges();

    expect(fixture.componentInstance.isOffline()).toBe(false);
    expect(fixture.componentInstance.clockState.status()).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Status unbekannt');
    expect(fixture.nativeElement.textContent).not.toContain('(offline)');
    expect(fixture.nativeElement.textContent).not.toContain('Offline – kein Status bekannt');
  });

  it('warns about refused offline stamps on the clock page WITHOUT naming colleagues', () => {
    // /clock also runs on personal phones: the notice says that something is
    // wrong, but not whose stamp it was (that stays on the kiosk, issue #34).
    rejectedStamps.set([{
      eventId: 'r1',
      employeeId: 'max',
      employeeName: 'Max Mustermann',
      performedAt: '2026-09-18T05:55:00Z',
      rejectedAt: '2026-09-18T09:00:00Z',
      message: 'Mitarbeiter nicht gefunden oder PIN falsch.',
      action: 'start',
    }]);
    const fixture = createComponent();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('1 Offline-Stempel nicht nachgetragen');
    expect(text).not.toContain('Max Mustermann');

    (fixture.nativeElement.querySelector('p[role="alert"] button') as HTMLButtonElement).click();
    // Auch hier muss die Beschriftung die Wirkung nennen (ein Druck quittiert alle).
    expect(
      (fixture.nativeElement.querySelector('p[role="alert"] button') as HTMLButtonElement).textContent?.trim(),
    ).toBe('Alle erledigt');
    expect(acknowledgeRejected).toHaveBeenCalledTimes(1);
  });

  it('shows waiting stamps in the offline banner right after loading (no terminalId)', () => {
    // Ohne NFC-Poll war der Ausfall bisher nur an einer fehlgeschlagenen
    // Aktion zu erkennen - der wartende Stempel blieb nach einem Reload
    // unsichtbar (Issue #6).
    healthResult = throwError(() => ({ status: 0 }));
    pendingQueue.set([{}, {}]);
    terminalIdValue = null;

    const fixture = TestBed.createComponent(ClockPage);
    fixture.detectChanges();

    const banner = fixture.nativeElement.querySelector('.offline-banner') as HTMLElement;
    expect(banner).not.toBeNull();
    expect(banner.textContent).toContain('2 Stempel warten auf Übertragung');
  });

  it('drops the banner and flushes the waiting stamps once the health poll answers again', () => {
    healthResult = throwError(() => ({ status: 0 }));
    pendingQueue.set([{}]);
    terminalIdValue = null;
    const fixture = TestBed.createComponent(ClockPage);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.offline-banner')).not.toBeNull();

    healthResult = of({ ok: true, version: null, configuredEmployees: 0, settingsConfigured: true });
    vi.advanceTimersByTime(15_000);
    fixture.detectChanges();

    // Zurueck im Netz: der wartende Nachtrag wird sofort angestossen ...
    expect(syncNow).toHaveBeenCalled();
    // ... der Hinweis bleibt aber stehen, solange die Queue nicht leer ist.
    expect(fixture.nativeElement.querySelector('.offline-banner')?.textContent).toContain(
      '1 Stempel wartet auf Übertragung',
    );

    pendingQueue.set([]);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.offline-banner')).toBeNull();
  });

  it('keeps the waiting notice while the API answers but the replay stays queued (W1)', () => {
    // API erreichbar, Kimai nimmt die Nachträge aber nicht an: die Events
    // bleiben in der Queue. Der Hinweis samt Zähler darf NICHT verschwinden -
    // vorher hing er allein an isOffline und wäre hier ausgeblendet worden.
    healthResult = of({ ok: true, version: null, configuredEmployees: 0, settingsConfigured: true });
    pendingQueue.set([{}, {}]);
    terminalIdValue = null;

    const fixture = TestBed.createComponent(ClockPage);
    fixture.detectChanges();

    const banner = fixture.nativeElement.querySelector('.offline-banner') as HTMLElement;
    expect(banner).not.toBeNull();
    expect(banner.textContent).toContain('2 Stempel warten auf Übertragung');
    // Kein "Offline" behaupten, wenn der Server antwortet.
    expect(banner.textContent).not.toContain('Offline');
  });

  it('treats a hanging health request as offline after the timeout (W2)', () => {
    healthResult = new Subject<unknown>().asObservable();
    pendingQueue.set([{}]);
    terminalIdValue = null;

    const fixture = TestBed.createComponent(ClockPage);
    fixture.detectChanges();
    // Vor dem Timeout ist nichts entschieden: der Hinweis nennt nur den
    // wartenden Stempel, ohne "Offline" zu behaupten.
    const before = (fixture.nativeElement.querySelector('.offline-banner') as HTMLElement | null)?.textContent ?? '';
    expect(before).not.toContain('Offline');

    vi.advanceTimersByTime(10_000);
    fixture.detectChanges();

    // Nach dem Timeout gilt der Server als nicht handlungsfähig.
    expect(fixture.nativeElement.querySelector('.offline-banner')?.textContent).toContain(
      'Offline – 1 Stempel wartet auf Übertragung',
    );
  });

  it('stops the health poll and the running request when the page is destroyed', () => {
    terminalIdValue = null;
    // Jede Anfrage bekommt ein eigenes Subject, damit sich die des Workflows
    // von der des Versions-Badges unterscheiden laesst.
    healthSubjects = [];

    const fixture = TestBed.createComponent(ClockPage);
    fixture.detectChanges();

    const pending = healthSubjects;
    expect(pending.every((s) => s.observed)).toBe(true);

    const component = fixture.componentInstance as unknown as { healthPollTimer: number | null };
    const timerId = component.healthPollTimer;
    expect(timerId).not.toBeNull();

    const clearSpy = vi.spyOn(window, 'clearInterval');
    fixture.destroy();

    // Der Takt endet ...
    expect(clearSpy).toHaveBeenCalledWith(timerId);
    clearSpy.mockRestore();
    // ... und die noch laufende Anfrage wird abgebrochen (Review-Befund Runde 2).
    // Genau eine Abmeldung: das Versions-Badge haengt am Root-Injector und
    // laeuft weiter, die Anfrage des Workflows darf es nicht.
    expect(pending.filter((s) => !s.observed)).toHaveLength(1);
  });

  it('signs in OFFLINE with a PIN remembered from an earlier ONLINE login', async () => {
    // A successful online login is what fills the verifier cache.
    await rememberEmployeePin('1234', session.employee);

    const fixture = createComponent();
    const component = fixture.componentInstance;
    component.pressDigit('1');
    component.pressDigit('2');
    component.pressDigit('3');
    component.pressDigit('4');
    failPolls = true;
    pinLoginResult.error({ status: 0 });

    await vi.waitFor(() => expect(component.isUnlocked()).toBe(true));
    expect(component.selectedEmployee()?.id).toBe('max');
    // The PIN stays in the session so the queued stamp can be re-validated.
    expect(component.pin()).toBe('1234');
    expect(component.message()).toContain('Offline');
    expect(playBeeps).toHaveBeenCalledWith(1);

    component.start();
    clockResult.error({ status: 0 });
    expect(enqueueKiosk).toHaveBeenCalledTimes(1);
    expect(enqueueKiosk.mock.calls[0][0].pin).toBe('1234');
  });

  it('refuses an OFFLINE login for a PIN that was never used online', async () => {
    await rememberEmployeePin('9999', session.employee);

    const fixture = createComponent();
    const component = fixture.componentInstance;
    component.pressDigit('1');
    component.pressDigit('2');
    component.pressDigit('3');
    component.pressDigit('4');
    pinLoginResult.error({ status: 0 });

    await vi.waitFor(() => expect(component.message()).toContain('Offline'));
    expect(component.isUnlocked()).toBe(false);
    expect(component.selectedEmployee()).toBeNull();
  });

  it('drops a remembered PIN the SERVER rejects (rotated PIN)', async () => {
    await rememberEmployeePin('1234', session.employee);

    const fixture = createComponent();
    const component = fixture.componentInstance;
    component.pressDigit('1');
    component.pressDigit('2');
    component.pressDigit('3');
    component.pressDigit('4');
    pinLoginResult.error({ status: 401 });

    expect(component.message()).toBe('PIN nicht gefunden');
    // Without this the rotated PIN would keep unlocking the kiosk offline
    // while every queued stamp is rejected during replay.
    await vi.waitFor(async () => expect(await resolveEmployeeByPin('1234')).toBeNull());
  });

  it('offers the action matching the last known status after an OFFLINE card login', () => {
    failPolls = true;
    window.localStorage.setItem(
      'stempeluhr.employee-card-cache.v1',
      JSON.stringify({ '04ABCD': session.employee }),
    );
    const fixture = createComponent();

    // Status this kiosk saw while it was still ONLINE: the employee is at work.
    rememberObservedStatus('max', {
      isRunning: true,
      activeTimesheetId: 7,
      startedAt: '2026-09-18T06:00:00Z',
      durationSeconds: 0,
      state: 'working',
      stateText: 'Eingestempelt',
    });

    localScanValue = { cardId: '04abcd', scannedAt: new Date().toISOString(), consumed: false };
    vi.advanceTimersByTime(1_000);
    fixture.detectChanges();

    expect(fixture.componentInstance.clockState.isWorking()).toBe(true);
    expect(fixture.componentInstance.clockState.status()?.stateText).toContain('zuletzt gesehen');
    // Already clocked in: the way OUT must be offered, never "Einstempeln" again.
    expect(fixture.nativeElement.querySelector('.stamp-button.pause')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('.stamp-button.stop')).not.toBeNull();
  });

  it('projects a queued OFFLINE action into the status the kiosk shows', () => {
    const fixture = createComponent();
    const component = fixture.componentInstance;
    component.pressDigit('1');
    component.pressDigit('2');
    component.pressDigit('3');
    component.pressDigit('4');
    pinLoginResult.next(session);
    expect(component.clockState.isWorking()).toBe(false);

    component.start();
    clockResult.error({ status: 0 });

    // Without the projection the kiosk would keep offering "Einstempeln" and
    // the employee would queue the same start over and over.
    expect(component.clockState.isWorking()).toBe(true);
    expect(component.clockState.status()?.stateText).toContain('offline vorgemerkt');
    // A kiosk reload during the outage must not lose that state either.
    expect(lastKnownStatus('max')?.origin).toBe('projected');
  });

  it('shows the remembered status with a neutral label while the server answer is pending', () => {
    // ONLINE cache hit: the employee is unlocked before the server's status
    // answer arrives - and a hung connection may never deliver it. The kiosk
    // then shows what it remembers, labelled as remembered rather than as
    // "offline" (the banner owns that claim) or as confirmed.
    window.localStorage.setItem(
      'stempeluhr.employee-card-cache.v1',
      JSON.stringify({ '04ABCD': session.employee }),
    );
    rememberObservedStatus('max', {
      isRunning: true,
      activeTimesheetId: 7,
      startedAt: '2026-09-18T06:00:00Z',
      durationSeconds: 0,
      state: 'working',
      stateText: 'Eingestempelt',
    });
    const fixture = createComponent();

    localScanValue = { cardId: '04abcd', scannedAt: new Date().toISOString(), consumed: false };
    vi.advanceTimersByTime(1_000);
    fixture.detectChanges();

    expect(fixture.componentInstance.isOffline()).toBe(false);
    expect(fixture.componentInstance.clockState.isWorking()).toBe(true);
    const stateText = fixture.componentInstance.clockState.status()?.stateText ?? '';
    expect(stateText).toContain('zuletzt gesehen');
    expect(stateText).not.toContain('offline');
    // Someone who is already clocked in gets the way out, not "Einstempeln".
    expect(fixture.nativeElement.querySelector('.stamp-button.stop')).not.toBeNull();
  });

  it('remembers PIN and status of a successful ONLINE login for the next outage', async () => {
    // This is the ONE production place that fills the two caches the offline
    // login depends on - without these writes the kiosk cannot sign anybody in
    // while the backend is down.
    const fixture = createComponent();
    const component = fixture.componentInstance;
    component.pressDigit('1');
    component.pressDigit('2');
    component.pressDigit('3');
    component.pressDigit('4');
    pinLoginResult.next(session);

    await vi.waitFor(async () => expect(await resolveEmployeeByPin('1234')).toEqual(session.employee));
    expect(lastKnownStatus('max')?.origin).toBe('observed');
    expect(lastKnownStatus('max')?.status).toEqual(session.status);
  });

  it('does not unlock when the OFFLINE login is abandoned while it is verified', async () => {
    await rememberEmployeePin('1234', session.employee);
    const fixture = createComponent();
    const component = fixture.componentInstance;
    component.pressDigit('1');
    component.pressDigit('2');
    component.pressDigit('3');
    component.pressDigit('4');
    pinLoginResult.error({ status: 0 }); // starts the asynchronous verifier lookup
    component.clearPin(); // employee cancels / the next person types a PIN

    await vi.waitFor(() => expect(component.isBusy()).toBe(false));
    expect(component.isUnlocked()).toBe(false);
    expect(component.selectedEmployee()).toBeNull();
  });

  it('drops a stale status when an OFFLINE card login knows nothing about the employee', () => {
    failPolls = true;
    window.localStorage.setItem(
      'stempeluhr.employee-card-cache.v1',
      JSON.stringify({ '04ABCD': session.employee }),
    );
    const fixture = createComponent();
    const component = fixture.componentInstance;
    // Whatever was on screen before: this kiosk may not attribute it to the
    // employee who just scanned - the status is simply unknown.
    component.clockState.setStatus({
      isRunning: true,
      activeTimesheetId: 7,
      startedAt: '2026-09-18T06:00:00Z',
      durationSeconds: 0,
      state: 'working',
      stateText: 'Eingestempelt',
    });

    localScanValue = { cardId: '04abcd', scannedAt: new Date().toISOString(), consumed: false };
    vi.advanceTimersByTime(1_000);

    expect(component.isUnlocked()).toBe(true);
    expect(component.clockState.status()).toBeNull();
  });
});
