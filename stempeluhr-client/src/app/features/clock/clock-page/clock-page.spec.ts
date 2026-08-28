import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import { of, Subject, throwError } from 'rxjs';

import { ClockStatus, HoursOverview, KioskEmployeeSession } from '../../../core/models/kiosk.models';
import { AudioFeedback } from '../../../core/services/audio-feedback';
import { KioskApi } from '../../../core/services/kiosk-api';
import { LocalNfcScanService } from '../../../core/services/local-nfc-scan.service';
import { ClockPage } from './clock-page';

describe('ClockPage', () => {
  let pinLogin: ReturnType<typeof vi.fn>;
  let pinLoginResult: Subject<KioskEmployeeSession>;
  let clockResult: Subject<ClockStatus>;
  let hoursOverview: ReturnType<typeof vi.fn>;

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

    await TestBed.configureTestingModule({
      imports: [ClockPage],
      providers: [
        {
          provide: KioskApi,
          useValue: {
            pinLogin,
            clock: vi.fn(() => clockResult),
            hoursOverview,
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
          useValue: { snapshot: { queryParamMap: { get: () => null } } },
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
});
