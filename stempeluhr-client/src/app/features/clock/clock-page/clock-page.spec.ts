import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import { of, Subject, throwError } from 'rxjs';

import { ClockStatus, HoursOverview, KioskEmployeeSession, NfcLatestEvent } from '../../../core/models/kiosk.models';
import { AudioFeedback } from '../../../core/services/audio-feedback';
import { KioskApi } from '../../../core/services/kiosk-api';
import { LocalNfcScanService } from '../../../core/services/local-nfc-scan.service';
import { ClockPage } from './clock-page';

describe('ClockPage', () => {
  let pinLogin: ReturnType<typeof vi.fn>;
  let pinLoginResult: Subject<KioskEmployeeSession>;
  let clockResult: Subject<ClockStatus>;
  let hoursOverview: ReturnType<typeof vi.fn>;
  /** Poll result for kioskApi.latestNfcEvent (only polled with a terminalId). */
  let latestNfcValue: NfcLatestEvent;
  /** terminalId served by the ActivatedRoute mock (null = /clock default route). */
  let terminalIdValue: string | null;

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
    pinLogin = vi.fn(() => pinLoginResult);
    latestNfcValue = { event: null };
    terminalIdValue = null;

    await TestBed.configureTestingModule({
      imports: [ClockPage],
      providers: [
        {
          provide: KioskApi,
          useValue: {
            pinLogin,
            clock: vi.fn(() => clockResult),
            hoursOverview,
            latestNfcEvent: vi.fn(() => of(latestNfcValue)),
          },
        },
        { provide: AudioFeedback, useValue: { playBeeps: vi.fn() } },
        // No HttpClient is provided in this spec - keep the local agent
        // poll fully mocked.
        {
          provide: LocalNfcScanService,
          useValue: { poll: vi.fn(() => of(null)), ack: vi.fn(() => of(null)) },
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
    expect(text).toContain('08:00:00');
    expect(text).toContain('+ 00:45:00 Pause');
    expect(text).toContain('20:00:00');
    expect(text).toContain('50:00:00');
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
    expect(card.textContent ?? '').toContain('08:00:00');
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
    expect(card.textContent ?? '').toContain('08:00:00');

    // Cancels the scheduled reset timer from the successful action.
    fixture.destroy();
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
      expect(component.selectedEmployee()?.id).toBe('max');
      // No pin was entered, so no hours reload may happen.
      expect(hoursOverview).not.toHaveBeenCalled();
      expect(component.hoursOverview()).toBeNull();

      fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('.hours-overview')).toBeNull();
    });

    it('hides the previous employee hours when an NFC card switches the identity', () => {
      const fixture = createPollingFixture();
      const component = fixture.componentInstance;

      // max logs in via PIN: his hours card is visible.
      unlock(fixture);
      fixture.detectChanges();
      expect(component.hoursOverview()).toEqual(overview);
      expect(fixture.nativeElement.querySelector('.hours-overview')).not.toBeNull();

      // berta taps her NFC card: the identity switches without any pin.
      const berta = {
        ...session.employee,
        id: 'berta',
        displayName: 'Berta Beispiel',
        initials: 'BB',
      };
      const bertaStatus: ClockStatus = {
        ...status,
        isRunning: true,
        activeTimesheetId: 9,
        startedAt: new Date().toISOString(),
        durationSeconds: 0,
        state: 'working',
        stateText: 'Eingestempelt',
      };
      latestNfcValue = {
        event: {
          eventId: 'ev-nfc-2',
          occurredAt: new Date().toISOString(),
          terminalId: 'term-1',
          cardId: 'BB01',
          employee: berta,
          status: bertaStatus,
          message: 'NFC-Karte erkannt.',
          success: true,
        },
      };
      vi.advanceTimersByTime(1_000);
      fixture.detectChanges();

      // Berta's identity and status are shown ...
      expect(component.selectedEmployee()?.id).toBe('berta');
      expect(component.isUnlocked()).toBe(true);
      expect(component.clockState.status()?.stateText).toBe('Eingestempelt');
      expect(fixture.nativeElement.textContent).toContain('Berta Beispiel');

      // ... but NOT max's hours: the stale card must disappear instead of
      // leaking the previous employee's data (privacy).
      expect(component.hoursOverview()).toBeNull();
      expect(fixture.nativeElement.querySelector('.hours-overview')).toBeNull();
    });
  });
});
