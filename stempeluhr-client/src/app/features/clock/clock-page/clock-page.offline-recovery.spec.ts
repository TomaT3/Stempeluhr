import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting, TestRequest } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';

import { ClockStatus, KioskEmployeeSession } from '../../../core/models/kiosk.models';
import { OfflineKioskClockEvent } from '../../../core/models/offline.models';
import { AudioFeedback } from '../../../core/services/audio-feedback';
import { KioskApi } from '../../../core/services/kiosk-api';
import { LocalNfcScanService } from '../../../core/services/local-nfc-scan.service';
import { OfflineQueueService } from '../../../core/services/offline-queue';
import { ClockPage } from './clock-page';

/**
 * End-to-end regression for the API-up/Kimai-down lockout: the REAL
 * OfflineQueueService (HttpTestingController) runs against the REAL
 * workflow. A buffered-only flush (server keeps the events in its outbox)
 * must NOT emit the recovered signal - so no component-level mock can hide
 * a broken flushQueue -> recoveredSubject -> back() coupling.
 */
describe('ClockPage recovery via the real OfflineQueueService', () => {
  let httpMock: HttpTestingController;
  let failPolls: boolean;
  let clockSubjects: Subject<ClockStatus>[];

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

  const kioskSyncEndpoint = '/api/kiosk/clock/sync';

  /** Lets the async flush continuation run so the next step happens. */
  async function drainMicrotasks(): Promise<void> {
    for (let i = 0; i < 20; i++) {
      await Promise.resolve();
    }
  }

  /** Answers an ALREADY-MATCHED sync request (expectOne removes it from the open list). */
  function respondSync(request: TestRequest, events: { eventId: string; status: string }[], kind: string): void {
    request.flush({
      accepted: kind === 'applied' ? events.length : 0,
      duplicates: 0,
      buffered: kind === 'buffered' ? events.length : 0,
      results: events,
    });
  }

  beforeEach(async () => {
    window.localStorage.clear();
    failPolls = false;
    clockSubjects = [];

    await TestBed.configureTestingModule({
      imports: [ClockPage],
      providers: [
        // NO override for OfflineQueueService: the real service runs.
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: KioskApi,
          useValue: {
            pinLogin: vi.fn(() => of(session)),
            clock: vi.fn(() => {
              const subject = new Subject<ClockStatus>();
              clockSubjects.push(subject);
              return subject;
            }),
            latestNfcEvent: vi.fn(() =>
              failPolls ? throwError(() => ({ status: 0 })) : of({ event: null }),
            ),
            hoursOverview: vi.fn(() => of(null)),
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
          useValue: { snapshot: { queryParamMap: { get: (key: string) => (key === 'terminalId' ? 'term-1' : null) } } },
        },
      ],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    vi.useFakeTimers();
  });

  afterEach(() => {
    httpMock.verify();
    vi.useRealTimers();
    window.localStorage.clear();
  });

  it('keeps the terminal unlocked while everything is buffered and releases it only after real processing', async () => {
    const fixture = TestBed.createComponent(ClockPage);
    const component = fixture.componentInstance;
    const queue = TestBed.inject(OfflineQueueService);

    // Unlock Max (backend still reachable).
    for (const digit of ['1', '2', '3', '4']) {
      component.pressDigit(digit);
    }
    await drainMicrotasks();
    expect(component.isUnlocked()).toBe(true);

    // Kimai goes down: the action fails transiently and lands in the REAL
    // localStorage queue.
    failPolls = true;
    component.start();
    clockSubjects[clockSubjects.length - 1].error({ status: 0 });
    await drainMicrotasks();
    expect(component.message()).toContain('Offline gespeichert');
    expect(queue.pendingCount().length).toBe(1);

    // Connection recovers: the NFC poll triggers a REAL syncNow() flush.
    failPolls = false;
    await vi.advanceTimersByTimeAsync(1_000);
    const firstRequest = httpMock.expectOne(r => r.url === kioskSyncEndpoint);
    const queued = firstRequest.request.body.events as OfflineKioskClockEvent[];
    expect(queued.length).toBe(1);

    // API up, Kimai STILL down: the server buffers everything. The real
    // flushQueue must NOT emit recovered - the terminal stays unlocked
    // (a PIN login is still impossible) and nothing resets.
    respondSync(firstRequest, [{ eventId: queued[0].eventId, status: 'buffered' }], 'buffered');
    await drainMicrotasks();
    expect(queue.pendingCount().length).toBe(1); // buffered stays queued
    // Buffered-only flush: the banner must STAY - the API answering is not
    // enough while Kimai is down (a PIN login is still impossible).
    expect(component.isOffline()).toBe(true);
    await vi.advanceTimersByTimeAsync(5_000);
    expect(component.isUnlocked()).toBe(true);
    expect(component.selectedEmployee()).not.toBeNull();

    // The next sync run (retry timer) replays the buffered event - now
    // Kimai is back: the SAME event is applied by the server. Only NOW
    // recovered fires through the real chain and releases the terminal.
    // (Short hops keep the fake clock behind the retry request's own fresh
    // deadline - a huge jump would time it out mid-flight.)
    await vi.advanceTimersByTimeAsync(15_000);
    const replayRequest = httpMock.expectOne(
      r => r.url === kioskSyncEndpoint && (r.body as { events: unknown[] }).events.length === 1,
    );
    respondSync(replayRequest, [{ eventId: queued[0].eventId, status: 'applied' }], 'applied');
    await drainMicrotasks();

    expect(queue.pendingCount().length).toBe(0);
    // Real processing -> recovered fired through the real chain: the banner
    // clears together with the terminal release.
    expect(component.isOffline()).toBe(false);
    expect(component.isUnlocked()).toBe(false);
    expect(component.selectedEmployee()).toBeNull();
  });
});