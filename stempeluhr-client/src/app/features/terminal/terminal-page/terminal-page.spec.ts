import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import { signal } from '@angular/core';
import { of, Subject, throwError } from 'rxjs';

import { ClockStatus, HoursOverview, KioskEmployeeSession, NfcClockEvent, NfcLatestEvent } from '../../../core/models/kiosk.models';
import { RejectedOfflineStamp } from '../../../core/models/offline.models';
import { AudioFeedback } from '../../../core/services/audio-feedback';
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
            latestNfcEvent: vi.fn(() =>
              failPolls ? throwError(() => ({ status: 0 })) : of<NfcLatestEvent>({ event: null }),
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
            ack: vi.fn(() => of(null)),
          },
        },
        {
          provide: OfflineQueueService,
          useValue: {
            enqueueKiosk,
            syncNow: vi.fn(() => of([])),
            recovered: recovered$.asObservable(),
            rejected: rejectedStamps.asReadonly(),
            acknowledgeRejected,
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

    expect(acknowledgeRejected).toHaveBeenCalledTimes(1);
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
});
