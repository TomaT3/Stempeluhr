import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import { of, Subject, throwError } from 'rxjs';

import { ClockStatus, HoursOverview, KioskEmployeeSession, NfcLatestEvent } from '../../../core/models/kiosk.models';
import { AudioFeedback } from '../../../core/services/audio-feedback';
import { KioskApi } from '../../../core/services/kiosk-api';
import { LocalNfcScanService } from '../../../core/services/local-nfc-scan.service';
import { TerminalPage } from './terminal-page';

describe('TerminalPage', () => {
  let pinLogin: ReturnType<typeof vi.fn>;
  let pinLoginResult: Subject<KioskEmployeeSession>;
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
    hoursOverview = vi.fn(() => of(overview));
    pinLogin = vi.fn(() => pinLoginResult);

    await TestBed.configureTestingModule({
      imports: [TerminalPage],
      providers: [
        {
          provide: KioskApi,
          useValue: {
            pinLogin,
            clock: vi.fn(),
            hoursOverview,
            latestNfcEvent: vi.fn(() => of<NfcLatestEvent>({ event: null })),
            health: vi.fn(() => of({ ok: true, version: null, configuredEmployees: 0, settingsConfigured: true })),
          },
        },
        { provide: AudioFeedback, useValue: { playBeeps: vi.fn() } },
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
    expect(text).toContain('08:00:00');
    expect(text).toContain('+ 00:45:00 Pause');
    expect(text).toContain('20:00:00');
    expect(text).toContain('50:00:00');
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
});
