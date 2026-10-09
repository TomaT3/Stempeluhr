import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import { SwUpdate } from '@angular/service-worker';
import { of, Subject, throwError } from 'rxjs';

import { ClockStatus, HoursOverview, KioskEmployeeSession, NfcClockEvent, WorkTimeHint } from '../../../core/models/kiosk.models';
import { AudioFeedback } from '../../../core/services/audio-feedback';
import { KioskApi } from '../../../core/services/kiosk-api';
import { LocalNfcScan, LocalNfcScanService } from '../../../core/services/local-nfc-scan.service';
import { CORRECTION_IDLE_MS } from '../correction-flow/correction-flow';
import { CORRECTION_NOW, CORRECTION_TIMESHEETS, correction } from '../correction-flow/correction-fixtures';
import { ClockPage } from './clock-page';

describe('ClockPage', () => {
  let pinLogin: ReturnType<typeof vi.fn>;
  let pinLoginResult: Subject<KioskEmployeeSession>;
  let clockResult: Subject<ClockStatus>;
  let hoursOverview: ReturnType<typeof vi.fn>;
  let workTimeHints: ReturnType<typeof vi.fn>;
  /** Card published by the local agent (only polled with a terminalId). */
  let localScanValue: LocalNfcScan | null;
  /** Online card identification (kioskApi.identify). */
  let identifyResult: Subject<NfcClockEvent>;
  /** terminalId served by the ActivatedRoute mock (null = /clock default route). */
  let terminalIdValue: string | null;
  /** Health-Poll für AppVersionService (Badge + Auto-Reload). */
  let healthMock: ReturnType<typeof vi.fn>;
  let correctionTimesheets: ReturnType<typeof vi.fn>;
  let submitCorrection: ReturnType<typeof vi.fn>;
  let myCorrections: ReturnType<typeof vi.fn>;
  let withdrawCorrection: ReturnType<typeof vi.fn>;
  /** Service-Worker-Mock; null = kein Service Worker (Dev/Test-Standard). */
  let swUpdateMock: {
    isEnabled: boolean;
    checkForUpdate: ReturnType<typeof vi.fn>;
    activateUpdate: ReturnType<typeof vi.fn>;
    unrecoverable: Subject<unknown>;
  } | null;

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
    pinLoginResult = new Subject<KioskEmployeeSession>();
    clockResult = new Subject<ClockStatus>();
    hoursOverview = vi.fn(() => of(overview));
    workTimeHints = vi.fn(() => of({ timeZone: 'Europe/Berlin', hints: [] }));
    pinLogin = vi.fn(() => pinLoginResult);
    localScanValue = null;
    identifyResult = new Subject<NfcClockEvent>();
    terminalIdValue = null;
    healthMock = vi.fn(() => of({ ok: true, version: null, configuredEmployees: 0, settingsConfigured: true }));
    swUpdateMock = null;
    correctionTimesheets = vi.fn(() => of(CORRECTION_TIMESHEETS));
    submitCorrection = vi.fn(() => of(correction()));
    myCorrections = vi.fn(() => of([]));
    withdrawCorrection = vi.fn(() => of(correction({ status: 'withdrawn' })));

    await TestBed.configureTestingModule({
      imports: [ClockPage],
      providers: [
        {
          provide: KioskApi,
          useValue: {
            pinLogin,
            clock: vi.fn(() => clockResult),
            hoursOverview,
            workTimeHints,
            ping: vi.fn(() => of({ ok: true, version: null, configuredEmployees: 0, settingsConfigured: true })),
            identify: vi.fn(() => identifyResult),
            health: healthMock,
            correctionTimesheets,
            submitCorrection,
            myCorrections,
            withdrawCorrection,
          },
        },
        { provide: AudioFeedback, useValue: { playBeeps: vi.fn() } },
        { provide: SwUpdate, useFactory: () => swUpdateMock },
        // No HttpClient is provided in this spec - keep the local agent
        // poll fully mocked.
        {
          provide: LocalNfcScanService,
          useValue: { poll: vi.fn(() => of(localScanValue)), refreshCatalog: vi.fn(() => of(null)), ack: vi.fn(() => of(null)) },
        },
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: {
              queryParamMap: { get: (key: string) => (key === 'terminalId' ? terminalIdValue : null) },
            },
          },
        },
      ],
    }).compileComponents();
  });

  /** Types the full PIN and resolves the pending pinLogin with a session. */
  function unlock(fixture: ComponentFixture<ClockPage>): void {
    const component = fixture.componentInstance;
    component.pressDigit('1');
    component.pressDigit('2');
    component.pressDigit('3');
    component.pressDigit('4');
    pinLoginResult.next(session);
  }

  afterEach(() => {
    vi.useRealTimers();
  });

  it('confirms the pin automatically after the fourth digit', () => {
    const fixture = TestBed.createComponent(ClockPage);
    const component = fixture.componentInstance;

    component.pressDigit('1');
    component.pressDigit('2');
    component.pressDigit('3');

    expect(pinLogin).not.toHaveBeenCalled();

    component.pressDigit('4');

    expect(component.pin()).toBe('1234');
    expect(pinLogin).toHaveBeenCalledExactlyOnceWith('1234');
    expect(component.isBusy()).toBe(true);
  });

  it('ignores further digits while the pin login is in progress', () => {
    const fixture = TestBed.createComponent(ClockPage);
    const component = fixture.componentInstance;

    component.pressDigit('1');
    component.pressDigit('2');
    component.pressDigit('3');
    component.pressDigit('4');
    component.pressDigit('5');

    expect(component.pin()).toBe('1234');
    expect(pinLogin).toHaveBeenCalledOnce();
  });

  it('loads the hours overview after a successful pin login and renders the card', () => {
    const fixture = TestBed.createComponent(ClockPage);
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

    const fixture = TestBed.createComponent(ClockPage);
    unlock(fixture);
    fixture.detectChanges();

    expect(hoursOverview).toHaveBeenCalledWith('1234');
    expect(fixture.nativeElement.querySelector('.hours-overview')).toBeNull();
  });

  it('omits the pause line in the today block when todayPauseSeconds is zero', () => {
    hoursOverview.mockReturnValue(of({ ...overview, todayPauseSeconds: 0 }));

    const fixture = TestBed.createComponent(ClockPage);
    unlock(fixture);
    fixture.detectChanges();

    const card = fixture.nativeElement.querySelector('.hours-overview') as HTMLElement;
    expect(card).not.toBeNull();
    expect(card.querySelector('.hours-pause')).toBeNull();
    expect(card.textContent ?? '').toContain('08:00');
  });

  it('reloads the hours overview after a successful clock action', () => {
    const fixture = TestBed.createComponent(ClockPage);
    const component = fixture.componentInstance;
    unlock(fixture);
    fixture.detectChanges();

    expect(hoursOverview).toHaveBeenCalledTimes(1);

    component.stop();
    clockResult.next({
      isRunning: true,
      activeTimesheetId: 7,
      startedAt: new Date().toISOString(),
      durationSeconds: 0,
      state: 'working',
      stateText: 'Eingestempelt',
    });
    fixture.detectChanges();

    expect(hoursOverview).toHaveBeenCalledTimes(2);

    // Cancels the scheduled reset timer from the successful action.
    fixture.destroy();
  });

  it('offers the task choice when clocking in and books the chosen task', () => {
    const fixture = TestBed.createComponent(ClockPage);
    const component = fixture.componentInstance;
    ['1', '2', '3', '4'].forEach(digit => component.pressDigit(digit));
    pinLoginResult.next({ ...session, employee: { ...session.employee, tasks: [{ id: 'kx', label: 'Kunde X' }] } });
    fixture.detectChanges();

    // The plain Einstempeln button gives way to the choice (one tap per task).
    expect(fixture.nativeElement.querySelector('.stamp-button.start')).toBeNull();
    const options = [...fixture.nativeElement.querySelectorAll('.task-button.start-task')] as HTMLButtonElement[];
    expect(options.map(option => option.textContent?.trim())).toEqual(['Standard-Tätigkeit', 'Kunde X']);

    options[1].click();

    const kioskApi = TestBed.inject(KioskApi) as unknown as { clock: ReturnType<typeof vi.fn> };
    expect(kioskApi.clock).toHaveBeenCalledWith('max', '1234', 'start', null, 'kx', expect.any(String));
  });

  it('ignores a second tap right after opening the switch picker (issue #59)', () => {
    vi.useFakeTimers();
    const fixture = TestBed.createComponent(ClockPage);
    const component = fixture.componentInstance;
    ['1', '2', '3', '4'].forEach(digit => component.pressDigit(digit));
    pinLoginResult.next({
      employee: { ...session.employee, tasks: [{ id: 'kx', label: 'Kunde X' }] },
      status: { ...status, isRunning: true, activeTimesheetId: 5, state: 'working', stateText: 'Eingestempelt' },
    });
    fixture.detectChanges();

    (fixture.nativeElement.querySelector('.task-switch-button') as HTMLButtonElement).click();
    fixture.detectChanges();
    const kundeX = [...fixture.nativeElement.querySelectorAll('.task-button')]
      .find(option => option.textContent?.trim() === 'Kunde X') as HTMLButtonElement;
    kundeX.click();

    const kioskApi = TestBed.inject(KioskApi) as unknown as { clock: ReturnType<typeof vi.fn> };
    expect(kioskApi.clock).not.toHaveBeenCalled();
    expect(component.taskPickerOpen()).toBe(true);

    vi.advanceTimersByTime(400);
    kundeX.click();

    expect(kioskApi.clock).toHaveBeenCalledWith('max', '1234', 'switch', null, 'kx', expect.any(String));
    fixture.destroy();
  });

  it('reports a switch Kimai refused like an error and keeps the message longer (issue #56)', () => {
    vi.useFakeTimers();
    const fixture = TestBed.createComponent(ClockPage);
    const component = fixture.componentInstance;
    unlock(fixture);

    component.switchTask('kx');
    clockResult.next({
      ...status,
      isRunning: true,
      activeTimesheetId: 8,
      state: 'working',
      stateText: 'Eingestempelt',
      warning: 'Kunde X nicht moeglich - weiter auf Standard-Taetigkeit',
    });

    const audio = TestBed.inject(AudioFeedback) as unknown as { playBeeps: ReturnType<typeof vi.fn> };
    expect(audio.playBeeps).toHaveBeenLastCalledWith(2);
    expect(component.message()).toBe('Kunde X nicht moeglich - weiter auf Standard-Taetigkeit');

    vi.advanceTimersByTime(2200);
    expect(component.isUnlocked()).toBe(true);

    vi.advanceTimersByTime(3800);
    expect(component.isUnlocked()).toBe(false);
    expect(component.message()).toBe('');
    fixture.destroy();
  });

  it('keeps the hours card hidden before login and clears it on back()', () => {
    const fixture = TestBed.createComponent(ClockPage);
    const component = fixture.componentInstance;
    fixture.detectChanges();

    expect(component.hoursOverview()).toBeNull();
    expect(fixture.nativeElement.querySelector('.hours-overview')).toBeNull();

    unlock(fixture);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.hours-overview')).not.toBeNull();

    component.back();
    fixture.detectChanges();
    expect(component.hoursOverview()).toBeNull();
    expect(fixture.nativeElement.querySelector('.hours-overview')).toBeNull();
  });

  it('keeps the last hours values visible when a reload after a clock action fails', () => {
    const fixture = TestBed.createComponent(ClockPage);
    const component = fixture.componentInstance;
    unlock(fixture);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.hours-overview')).not.toBeNull();

    // The reload triggered by the successful clock action now fails.
    hoursOverview.mockReturnValue(throwError(() => ({ status: 500 })));
    component.stop();
    clockResult.next({
      isRunning: true,
      activeTimesheetId: 7,
      startedAt: new Date().toISOString(),
      durationSeconds: 0,
      state: 'working',
      stateText: 'Eingestempelt',
    });
    fixture.detectChanges();

    expect(hoursOverview).toHaveBeenCalledTimes(2);
    // Old values stay visible - the failed reload must neither clear the
    // signal nor throw.
    expect(component.hoursOverview()).toEqual(overview);
    const card = fixture.nativeElement.querySelector('.hours-overview') as HTMLElement;
    expect(card).not.toBeNull();
    expect(card.textContent ?? '').toContain('08:00');

    // Cancels the scheduled reset timer from the successful action.
    fixture.destroy();
  });

  it('never shows previous employee hours after back() with a stale in-flight response', () => {
    // Login A: Request A bleibt offen (in-flight), Login B: Request B schlägt fehl.
    const hoursA = new Subject<HoursOverview>();
    const hoursB = new Subject<HoursOverview>();
    hoursOverview
      .mockReturnValueOnce(hoursA)
      .mockReturnValueOnce(hoursB);
    // Login B bekommt ein EIGENES Subject: das geteilte pinLoginResult würde
    // beim zweiten next() auch den alten Login-A-Callback feuern lassen.
    const pinLoginB = new Subject<KioskEmployeeSession>();
    pinLogin
      .mockReturnValueOnce(pinLoginResult)
      .mockReturnValueOnce(pinLoginB);

    const fixture = TestBed.createComponent(ClockPage);
    const component = fixture.componentInstance;

    // Login A (PIN 1234) - Request A läuft noch (pending, noch keine Daten).
    unlock(fixture);
    fixture.detectChanges();
    expect(hoursOverview).toHaveBeenCalledWith('1234');
    expect(component.hoursOverview()).toBeNull();

    // Zurück, während Request A noch läuft.
    component.back();
    fixture.detectChanges();
    expect(component.hoursOverview()).toBeNull();

    // A's Response kommt zu spät - der PIN-Guard muss sie verwerfen.
    hoursA.next(overview);
    fixture.detectChanges();
    expect(component.hoursOverview()).toBeNull();

    // Login B (PIN 5678, andere Person) - Request B schlägt fehl.
    component.pressDigit('5');
    component.pressDigit('6');
    component.pressDigit('7');
    component.pressDigit('8');
    pinLoginB.next({
      employee: { ...session.employee, id: 'berta', displayName: 'Berta Beispiel', initials: 'BB' },
      status,
    });
    hoursB.error({ status: 500 });
    fixture.detectChanges();

    // Die Stunden von A dürfen unter B nie erscheinen.
    expect(component.hoursOverview()).toBeNull();
    expect(fixture.nativeElement.querySelector('.hours-overview')).toBeNull();
  });

  describe('NFC identity switch', () => {
    /** Creates the component on a polled terminal (terminalId = 'term-1'). */
    function createPollingFixture(): ComponentFixture<ClockPage> {
      terminalIdValue = 'term-1';
      return TestBed.createComponent(ClockPage);
    }

    beforeEach(() => {
      vi.useFakeTimers();
      window.localStorage.clear();
    });

    afterEach(() => {
      vi.useRealTimers();
      window.localStorage.clear();
    });

    it('renders no hours card after an NFC login without pin and never calls the hours API', () => {
      const fixture = createPollingFixture();
      const component = fixture.componentInstance;

      // Card touch: the local agent publishes it, identify resolves it online.
      localScanValue = { cardId: '04AB', scannedAt: new Date().toISOString(), consumed: false };
      vi.advanceTimersByTime(1_000);
      identifyResult.next({
        eventId: 'ev-nfc-1',
        occurredAt: new Date().toISOString(),
        terminalId: 'term-1',
        cardId: '04AB',
        employee: session.employee,
        status: status,
        message: 'NFC-Karte erkannt.',
        success: true,
      });

      expect(component.isUnlocked()).toBe(true);
      expect(component.selectedEmployee()?.id).toBe('max');
      // No pin was entered, so no hours reload may happen.
      expect(hoursOverview).not.toHaveBeenCalled();
      expect(component.hoursOverview()).toBeNull();

      fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('.hours-overview')).toBeNull();
    });

  });

  describe('Auto-Reload bei Server-Update', () => {
    afterEach(() => {
      vi.restoreAllMocks();
    });

    function createPageWithVersion(version: string | null) {
      healthMock.mockReturnValue(
        of({ ok: true, version, configuredEmployees: 0, settingsConfigured: true }),
      );
      // Prototype-Spies MÜSSEN vor createComponent aktiv sein: der health-Poll
      // feuert synchron im Konstruktor (startWith), die Instanz existiert dann
      // noch nicht zum Überschreiben.
      const reloadSpy = vi
        .spyOn(ClockPage.prototype as unknown as { performReload: () => void }, 'performReload')
        .mockImplementation(() => {});
      vi.spyOn(
        ClockPage.prototype as unknown as { isReleaseBuild: () => boolean },
        'isReleaseBuild',
      ).mockReturnValue(true);
      const fixture = TestBed.createComponent(ClockPage);
      const component = fixture.componentInstance as unknown as {
        pin: { set: (v: string) => void };
        message: () => string;
      };
      fixture.detectChanges();
      return { fixture, component, reloadSpy };
    }

    it('lädt bei Server-Versions-Mismatch automatisch neu (Idle)', () => {
      vi.useFakeTimers();
      try {
        const { component, reloadSpy } = createPageWithVersion('9.9.9');
        expect(component.message()).toContain('Neue Version verfügbar');

        vi.advanceTimersByTime(3_000);
        expect(reloadSpy).toHaveBeenCalledTimes(1);
      } finally {
        vi.useRealTimers();
      }
    });

    it('lädt nicht neu, solange eine PIN-Eingabe läuft', () => {
      vi.useFakeTimers();
      try {
        const { component, reloadSpy } = createPageWithVersion('9.9.9');
        component.pin.set('1'); // Mitarbeiter tippt gerade
        vi.advanceTimersByTime(3_000);
        expect(reloadSpy).not.toHaveBeenCalled();
        // Abort entfernt den Hinweis wieder (kein toter Text auf dem Bildschirm)
        expect(component.message()).toBe('');
      } finally {
        vi.useRealTimers();
      }
    });

    it('lädt nicht neu, solange ein Korrekturablauf offen ist (er zählt nicht als Ruhezustand)', () => {
      vi.useFakeTimers();
      try {
        const { fixture, reloadSpy } = createPageWithVersion('9.9.9');
        const page = fixture.componentInstance;
        ['1', '2', '3', '4'].forEach(digit => page.pressDigit(digit));
        pinLoginResult.next(session);
        fixture.detectChanges();
        page.openCorrection();
        expect(page.correctionOpen()).toBe(true);

        vi.advanceTimersByTime(3_000);
        expect(reloadSpy).not.toHaveBeenCalled();

        // Ablauf zu: der nächste Versuch lädt neu, sobald niemand mehr da ist.
        page.back();
        vi.advanceTimersByTime(60_000 + 3_000);
        expect(reloadSpy).toHaveBeenCalledTimes(1);
      } finally {
        vi.useRealTimers();
      }
    });

    describe('mit Service Worker', () => {
      function enableServiceWorker(activateResults: boolean[]) {
        swUpdateMock = {
          isEnabled: true,
          checkForUpdate: vi.fn(async () => true),
          activateUpdate: vi.fn(async () => activateResults.shift() ?? false),
          unrecoverable: new Subject<unknown>(),
        };
        return swUpdateMock;
      }

      it('aktiviert die neue Version vor dem Reload', async () => {
        vi.useFakeTimers();
        try {
          const sw = enableServiceWorker([true]);
          const { reloadSpy } = createPageWithVersion('9.9.9');

          await vi.advanceTimersByTimeAsync(3_000);

          expect(sw.checkForUpdate).toHaveBeenCalledTimes(1);
          expect(sw.activateUpdate).toHaveBeenCalledTimes(1);
          expect(reloadSpy).toHaveBeenCalledTimes(1);
        } finally {
          vi.useRealTimers();
        }
      });

      it('lädt nicht neu, solange der Service Worker die neue Version nicht hat, und versucht es später erneut', async () => {
        vi.useFakeTimers();
        try {
          // Erst liegt die neue Version noch nicht vor, beim zweiten Versuch schon.
          const sw = enableServiceWorker([false, true]);
          const { component, reloadSpy } = createPageWithVersion('9.9.9');

          await vi.advanceTimersByTimeAsync(3_000);
          // Ein Reload würde die alte App aus dem Cache laden - Schleife.
          expect(reloadSpy).not.toHaveBeenCalled();
          expect(component.message()).toBe('');

          await vi.advanceTimersByTimeAsync(60_000 + 3_000);
          expect(sw.activateUpdate).toHaveBeenCalledTimes(2);
          expect(reloadSpy).toHaveBeenCalledTimes(1);
        } finally {
          vi.useRealTimers();
        }
      });

      it('aktiviert nichts, wenn während des Update-Checks jemand aktiv wird', async () => {
        vi.useFakeTimers();
        try {
          const sw = enableServiceWorker([true]);
          let finishCheck: (value: boolean) => void = () => {};
          sw.checkForUpdate.mockImplementation(() => new Promise<boolean>(resolve => (finishCheck = resolve)));
          const { component, reloadSpy } = createPageWithVersion('9.9.9');

          await vi.advanceTimersByTimeAsync(3_000);
          component.pin.set('1'); // Mitarbeiter beginnt zu tippen
          finishCheck(true);
          await vi.advanceTimersByTimeAsync(0);

          expect(sw.activateUpdate).not.toHaveBeenCalled();
          expect(reloadSpy).not.toHaveBeenCalled();
        } finally {
          vi.useRealTimers();
        }
      });

      it('lädt neu, wenn der Service Worker nicht mehr weiterweiß', () => {
        const sw = enableServiceWorker([]);
        const { reloadSpy } = createPageWithVersion(null);

        sw.unrecoverable.next({ reason: 'test' });

        expect(reloadSpy).toHaveBeenCalledTimes(1);
      });
    });

    it('lädt im Dev-Build (0.0.0-local) nie automatisch neu', () => {
      vi.useFakeTimers();
      try {
        healthMock.mockReturnValue(
          of({ ok: true, version: '9.9.9', configuredEmployees: 0, settingsConfigured: true }),
        );
        const reloadSpy = vi
          .spyOn(ClockPage.prototype as unknown as { performReload: () => void }, 'performReload')
          .mockImplementation(() => {});
        // KEIN isReleaseBuild-Override: APP_VERSION='0.0.0-local' im Test -> false.
        TestBed.createComponent(ClockPage);
        vi.advanceTimersByTime(3_000);
        expect(reloadSpy).not.toHaveBeenCalled();
      } finally {
        vi.useRealTimers();
      }
    });
  });

  describe('Korrekturanträge', () => {
    const working: ClockStatus = {
      isRunning: true,
      activeTimesheetId: 12,
      startedAt: '2026-10-05T01:00:00Z',
      durationSeconds: 46800,
      state: 'working',
      stateText: 'Eingestempelt',
      activeIsDefaultTask: true,
    };

    beforeEach(() => {
      vi.useFakeTimers();
      vi.setSystemTime(CORRECTION_NOW);
      window.localStorage.clear();
    });

    afterEach(() => {
      window.localStorage.clear();
    });

    function login(sessionStatus: ClockStatus = status, tasks: { id: string; label: string }[] = []): ComponentFixture<ClockPage> {
      const fixture = TestBed.createComponent(ClockPage);
      ['1', '2', '3', '4'].forEach(digit => fixture.componentInstance.pressDigit(digit));
      pinLoginResult.next({ employee: { ...session.employee, tasks }, status: sessionStatus });
      fixture.detectChanges();
      return fixture;
    }

    const entryButton = (fixture: ComponentFixture<ClockPage>) =>
      fixture.nativeElement.querySelector('app-correction-entry .correction-entry') as HTMLButtonElement | null;

    function openFlow(fixture: ComponentFixture<ClockPage>): void {
      entryButton(fixture)!.click();
      fixture.detectChanges();
    }

    function tap(fixture: ComponentFixture<ClockPage>, text: string): void {
      const button = ([...fixture.nativeElement.querySelectorAll('app-correction-flow button')] as HTMLButtonElement[])
        .find(candidate => candidate.textContent?.trim() === text);
      expect(button, `Knopf "${text}"`).toBeDefined();
      button!.click();
      fixture.detectChanges();
    }

    function tapEntry(fixture: ComponentFixture<ClockPage>, range: string): void {
      const button = ([...fixture.nativeElement.querySelectorAll('app-correction-flow .flow-entry')] as HTMLButtonElement[])
        .find(candidate => candidate.textContent?.includes(range));
      expect(button, `Eintrag ${range}`).toBeDefined();
      button!.click();
      fixture.detectChanges();
    }

    const flowTitle = (fixture: ComponentFixture<ClockPage>) =>
      fixture.nativeElement.querySelector('app-correction-flow .flow-title')?.textContent?.trim();

    function typeComment(fixture: ComponentFixture<ClockPage>, text: string): void {
      const area = fixture.nativeElement.querySelector('app-correction-flow textarea') as HTMLTextAreaElement;
      area.value = text;
      area.dispatchEvent(new Event('input'));
      fixture.detectChanges();
    }

    describe('Einstieg', () => {
      it('offers Korrektur after login, not before', () => {
        const fixture = TestBed.createComponent(ClockPage);
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
        fixture.componentInstance.openCorrection();
        expect(fixture.componentInstance.correctionOpen()).toBe(false);
        expect(correctionTimesheets).not.toHaveBeenCalled();
      });

      it('replaces status and stamp buttons by the flow, name and back stay', () => {
        const fixture = login(working);
        expect(fixture.nativeElement.querySelector('.stamp-actions')).not.toBeNull();

        openFlow(fixture);

        expect(fixture.nativeElement.querySelector('.stamp-actions')).toBeNull();
        expect(fixture.nativeElement.querySelector('app-correction-flow.layout-clock')).not.toBeNull();
        expect(fixture.nativeElement.querySelector('.person h2')?.textContent).toContain('Max Mustermann');
        expect(fixture.nativeElement.querySelector('.back-button')).not.toBeNull();
        expect(entryButton(fixture)).toBeNull();
      });
    });

    describe('Ablauf bis Absenden (PIN-Session)', () => {
      it('addPause with a comment: source clock', () => {
        const fixture = login();
        openFlow(fixture);
        tap(fixture, 'Pause nachtragen');
        tapEntry(fixture, '07:00–11:00');
        tap(fixture, 'Weiter');
        tap(fixture, 'Weiter');
        typeComment(fixture, '  Pause vergessen  ');
        tap(fixture, 'Absenden');

        expect(correctionTimesheets).toHaveBeenCalledWith({ employeeId: 'max', pin: '1234', nfcCardId: null });
        expect(submitCorrection).toHaveBeenCalledExactlyOnceWith({
          employeeId: 'max', pin: '1234', nfcCardId: null,
          kind: 'addPause', timesheetId: 21, begin: null, end: null,
          pauseBegin: '2026-10-05T08:45', pauseEnd: '2026-10-05T09:15',
          taskId: null, comment: 'Pause vergessen', source: 'clock',
        });
        expect(fixture.nativeElement.querySelector('app-correction-flow .flow-sent')?.textContent)
          .toBe('Antrag gesendet – wartet auf Freigabe');
      });

      it('setEnd without a comment sends none', () => {
        const fixture = login();
        openFlow(fixture);
        tap(fixture, 'Ausstempeln nachtragen');
        tapEntry(fixture, '22:00 – So 04.10. 02:00');
        tap(fixture, 'Weiter');
        tap(fixture, 'Absenden');

        expect(submitCorrection).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({
          kind: 'setEnd', timesheetId: 12, end: '2026-10-04T00:00', comment: null, source: 'clock',
        }));
      });

      it('changeTimes', () => {
        const fixture = login();
        openFlow(fixture);
        tap(fixture, 'Zeiten ändern');
        tapEntry(fixture, '07:00–11:00');
        (fixture.nativeElement.querySelector('app-time-stepper .hour-earlier') as HTMLButtonElement).click();
        fixture.detectChanges();
        tap(fixture, 'Weiter');
        tap(fixture, 'Weiter');
        tap(fixture, 'Absenden');

        expect(submitCorrection).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({
          kind: 'changeTimes', timesheetId: 21, begin: '2026-10-05T06:00', end: null,
        }));
      });

      it('addShift on the main task without a pause', () => {
        const fixture = login(status, [{ id: 'kx', label: 'Kunde X' }]);
        openFlow(fixture);
        tap(fixture, 'Schicht nachtragen');
        tap(fixture, 'Standard-Tätigkeit');
        tap(fixture, 'Weiter');
        tap(fixture, 'Weiter');
        tap(fixture, 'Ohne Pause');
        tap(fixture, 'Absenden');

        expect(submitCorrection).toHaveBeenCalledExactlyOnceWith({
          employeeId: 'max', pin: '1234', nfcCardId: null,
          kind: 'addShift', timesheetId: null, begin: '2026-10-05T08:00', end: '2026-10-05T16:00',
          pauseBegin: null, pauseEnd: null, taskId: null, comment: null, source: 'clock',
        });
      });

      it('keeps the employee in the summary with the server message on 400', () => {
        submitCorrection.mockReturnValue(throwError(() => ({ status: 400, error: { message: 'Zeiten in der Zukunft sind nicht erlaubt.' } })));
        const fixture = login();
        openFlow(fixture);
        tap(fixture, 'Ausstempeln nachtragen');
        tapEntry(fixture, '22:00 – So 04.10. 02:00');
        tap(fixture, 'Weiter');
        tap(fixture, 'Absenden');

        expect(fixture.nativeElement.querySelector('app-correction-flow .flow-error')?.textContent).toContain('Zukunft');
        expect(flowTitle(fixture)).toBe('Zusammenfassung');
      });
    });

    describe('Karten-Session ohne PIN', () => {
      it('sends employeeId plus cardId and an empty PIN', () => {
        terminalIdValue = 'term-1';
        const fixture = TestBed.createComponent(ClockPage);
        localScanValue = { cardId: '04AB', scannedAt: new Date().toISOString(), consumed: false };
        vi.advanceTimersByTime(1_000);
        identifyResult.next({
          eventId: 'ev-nfc-1', occurredAt: new Date().toISOString(), terminalId: 'term-1', cardId: '04AB',
          employee: session.employee, status, message: 'NFC-Karte erkannt.', success: true,
        });
        fixture.detectChanges();
        expect(fixture.componentInstance.pin()).toBe('');

        openFlow(fixture);
        tap(fixture, 'Ausstempeln nachtragen');
        tapEntry(fixture, '22:00 – So 04.10. 02:00');
        tap(fixture, 'Weiter');
        tap(fixture, 'Absenden');

        const card = { employeeId: 'max', pin: '', nfcCardId: '04AB' };
        expect(correctionTimesheets).toHaveBeenCalledWith(card);
        expect(submitCorrection).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({
          ...card, kind: 'setEnd', timesheetId: 12, source: 'term-1',
        }));
      });
    });

    describe('Meine Anträge', () => {
      it('shows status and reason and withdraws an open request', () => {
        myCorrections.mockReturnValue(of([
          correction({ id: 'p1' }),
          correction({ id: 'r1', status: 'rejected', decisionNote: 'Schon im Dienstplan' }),
        ]));
        withdrawCorrection.mockReturnValue(of(correction({ id: 'p1', status: 'withdrawn' })));
        const fixture = login();
        openFlow(fixture);
        tap(fixture, 'Meine Anträge');

        const text = fixture.nativeElement.querySelector('app-correction-flow')?.textContent ?? '';
        expect(text).toContain('Wartet auf Freigabe');
        expect(text).toContain('Abgelehnt');
        expect(text).toContain('Grund: Schon im Dienstplan');

        tap(fixture, 'Zurückziehen');
        expect(withdrawCorrection).toHaveBeenCalledExactlyOnceWith({ employeeId: 'max', pin: '1234', nfcCardId: null }, 'p1');
        expect(fixture.nativeElement.querySelector('app-correction-flow')?.textContent).toContain('Zurückgezogen');
      });
    });

    describe('Sitzung und verspätete Antworten', () => {
      it('discards a late answer after the identity changed', () => {
        const lateList = new Subject<typeof CORRECTION_TIMESHEETS>();
        correctionTimesheets.mockReturnValue(lateList);
        const fixture = login();
        openFlow(fixture);
        tap(fixture, 'Pause nachtragen');

        fixture.nativeElement.querySelector('.back-button').click();
        fixture.detectChanges();
        expect(fixture.componentInstance.correctionOpen()).toBe(false);
        pinLogin.mockImplementation(() => of({ employee: { ...session.employee, id: 'anna', displayName: 'Anna Beispiel' }, status }));
        ['4', '3', '2', '1'].forEach(digit => fixture.componentInstance.pressDigit(digit));
        fixture.detectChanges();
        correctionTimesheets.mockReturnValue(of({ timeZone: 'Europe/Berlin', shifts: [] }));
        openFlow(fixture);
        tap(fixture, 'Pause nachtragen');

        lateList.next(CORRECTION_TIMESHEETS);
        fixture.detectChanges();

        expect(correctionTimesheets).toHaveBeenLastCalledWith({ employeeId: 'anna', pin: '4321', nfcCardId: null });
        expect(fixture.nativeElement.querySelector('app-correction-flow .flow-entry')).toBeNull();
        expect(fixture.nativeElement.textContent).not.toContain('Nachtdienst');
        expect(lateList.observed).toBe(false);
      });

      it('goes back to the idle screen after 2 minutes without a tap', () => {
        const fixture = login();
        openFlow(fixture);

        vi.advanceTimersByTime(CORRECTION_IDLE_MS - 1_000);
        fixture.detectChanges();
        expect(fixture.componentInstance.selectedEmployee()).not.toBeNull();

        vi.advanceTimersByTime(1_000);
        fixture.detectChanges();
        expect(fixture.componentInstance.selectedEmployee()).toBeNull();
        expect(fixture.nativeElement.querySelector('.kiosk-login')).not.toBeNull();
        expect(fixture.nativeElement.querySelector('app-correction-flow')).toBeNull();
      });
    });

    describe('Arbeitszeit-Hinweise', () => {
      const running: ClockStatus = { ...working, activeTimesheetId: 23, startedAt: '2026-10-05T09:30:00Z' };
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
      const hintButton = (fixture: ComponentFixture<ClockPage>) =>
        fixture.nativeElement.querySelector('app-correction-entry .forgot-hint') as HTMLButtonElement | null;
      const hintText = (fixture: ComponentFixture<ClockPage>) =>
        hintButton(fixture)?.textContent?.replace(/\s+/g, ' ').trim();

      it('offers "Pause vergessen?" and opens "Pause nachtragen" for the running entry without stamping', () => {
        workTimeHints.mockReturnValue(hints(pauseCase));
        const fixture = login(running);

        expect(workTimeHints).toHaveBeenCalledExactlyOnceWith({ employeeId: 'max', pin: '1234', nfcCardId: null });
        expect(hintText(fixture)).toBe('Pause vergessen? seit 07:58 ohne Pause');

        hintButton(fixture)!.click();
        fixture.detectChanges();

        const kioskApi = TestBed.inject(KioskApi) as unknown as { clock: ReturnType<typeof vi.fn> };
        expect(kioskApi.clock).not.toHaveBeenCalled();
        expect(fixture.componentInstance.correctionStart()).toEqual({ kind: 'addPause', timesheetId: 23 });
        expect(flowTitle(fixture)).toBe('Pause beginnt');
      });

      it('names the last shift and opens "Pause nachtragen" for the entry of the hint', () => {
        workTimeHints.mockReturnValue(hints(lastShiftCase, lastPauseCase));
        const fixture = login();

        expect(hintText(fixture)).toBe('Letzte Schicht: 7:10 Std. ohne Pause Prüfen');
        hintButton(fixture)!.click();
        fixture.detectChanges();

        expect(fixture.componentInstance.correctionStart()).toEqual({ kind: 'addPause', timesheetId: 12 });
      });

      it('opens the choice of kinds for a shift over 10 hours', () => {
        workTimeHints.mockReturnValue(hints(lastShiftCase));
        const fixture = login();

        expect(hintText(fixture)).toBe('Letzte Schicht: 10:40 Std. Prüfen');
        hintButton(fixture)!.click();
        fixture.detectChanges();

        expect(flowTitle(fixture)).toBe('Korrektur');
      });

      it('shows no hint during the pause or offline', () => {
        workTimeHints.mockReturnValue(hints({ ...pauseCase, end: '2026-10-05T15:00', timesheetId: 21 }, lastShiftCase));
        expect(hintButton(login({ ...status, isRunning: true, activeTimesheetId: 22, state: 'paused', stateText: 'Pause' }))).toBeNull();

        workTimeHints.mockReturnValue(hints(lastShiftCase));
        const fixture = login();
        fixture.componentInstance.isOffline.set(true);
        fixture.detectChanges();
        expect(hintButton(fixture)).toBeNull();
      });

      it('loads the hints of a card session with the card', () => {
        workTimeHints.mockReturnValue(hints(lastShiftCase));
        terminalIdValue = 'term-1';
        const fixture = TestBed.createComponent(ClockPage);
        localScanValue = { cardId: '04AB', scannedAt: new Date().toISOString(), consumed: false };
        vi.advanceTimersByTime(1_000);
        expect(workTimeHints).not.toHaveBeenCalled();
        identifyResult.next({
          eventId: 'ev-nfc-1', occurredAt: new Date().toISOString(), terminalId: 'term-1', cardId: '04AB',
          employee: session.employee, status, message: 'NFC-Karte erkannt.', success: true,
        });
        fixture.detectChanges();

        expect(workTimeHints).toHaveBeenCalledExactlyOnceWith({ employeeId: 'max', pin: '', nfcCardId: '04AB' });
        expect(hintText(fixture)).toBe('Letzte Schicht: 10:40 Std. Prüfen');
      });
    });

    describe('Vergessen auszustempeln?', () => {
      const hint = (fixture: ComponentFixture<ClockPage>) =>
        fixture.nativeElement.querySelector('app-correction-entry .forgot-hint') as HTMLButtonElement | null;

      it('shows the hint from 12 hours on, not for a shorter section', () => {
        expect(hint(login({ ...working, startedAt: '2026-10-05T02:00:01Z' }))).toBeNull();
      });

      it('offers the hint after 12 hours', () => {
        const fixture = login({ ...working, startedAt: '2026-10-05T02:00:00Z' });

        expect(hint(fixture)?.textContent?.trim()).toBe('Vergessen auszustempeln?');
      });

      it('clocks out the normal way and then opens "Ausstempeln nachtragen" for that timesheet', () => {
        const fixture = login(working);

        hint(fixture)!.click();
        const kioskApi = TestBed.inject(KioskApi) as unknown as { clock: ReturnType<typeof vi.fn> };
        expect(kioskApi.clock).toHaveBeenCalledExactlyOnceWith('max', '1234', 'stop', null, null, expect.any(String));
        expect(fixture.componentInstance.correctionOpen()).toBe(false);

        clockResult.next({ ...status, stateText: 'Ausgestempelt' });
        fixture.detectChanges();

        expect(fixture.componentInstance.correctionOpen()).toBe(true);
        expect(flowTitle(fixture)).toBe('Ende');
        expect(fixture.nativeElement.querySelector('app-correction-flow .flow-context')?.textContent).toContain('Nachtdienst');
        vi.advanceTimersByTime(10_000);
        fixture.detectChanges();
        expect(fixture.componentInstance.selectedEmployee()?.id).toBe('max');

        tap(fixture, 'Weiter');
        tap(fixture, 'Absenden');
        expect(submitCorrection).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ kind: 'setEnd', timesheetId: 12 }));
      });

      it('opens no correction when the stop did not go through online', () => {
        const fixture = login(working);
        fixture.componentInstance.isOffline.set(true);
        fixture.detectChanges();

        hint(fixture)!.click();
        fixture.detectChanges();

        const kioskApi = TestBed.inject(KioskApi) as unknown as { clock: ReturnType<typeof vi.fn> };
        expect(kioskApi.clock).not.toHaveBeenCalled();
        expect(fixture.componentInstance.correctionOpen()).toBe(false);
      });

      it('drops the result of the stop when the employee left meanwhile', () => {
        const fixture = login(working);
        hint(fixture)!.click();

        fixture.componentInstance.back();
        clockResult.next({ ...status, stateText: 'Ausgestempelt' });
        fixture.detectChanges();

        expect(fixture.componentInstance.correctionOpen()).toBe(false);
        expect(fixture.componentInstance.selectedEmployee()).toBeNull();
      });
    });
  });
});
