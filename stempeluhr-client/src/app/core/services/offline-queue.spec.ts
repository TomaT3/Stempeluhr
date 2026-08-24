import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { OfflineKioskClockEvent, OfflineNfcClockEvent } from '../models/offline.models';
import { OfflineQueueService } from './offline-queue';

describe('OfflineQueueService sync batching', () => {
  let service: OfflineQueueService;
  let httpMock: HttpTestingController;

  const kioskEndpoint = '/api/kiosk/clock/sync';
  const nfcEndpoint = '/api/nfc/clock/sync';

  function kioskEvent(id: string): OfflineKioskClockEvent {
    return { eventId: id, employeeId: 'max', pin: '1234', action: 'start', performedAt: '2026-08-24T08:00:00Z' };
  }

  function nfcEvent(id: string): OfflineNfcClockEvent {
    return { eventId: id, cardId: '04AB', scannedAt: '2026-08-24T08:00:00Z' };
  }

  function resultFor(events: OfflineKioskClockEvent[], status: string) {
    return {
      accepted: status === 'applied' ? events.length : 0,
      duplicates: 0,
      buffered: status === 'buffered' ? events.length : 0,
      results: events.map(e => ({ eventId: e.eventId, status })),
    };
  }

  /** Lets the async flush continuation run so the next chunk goes out. */
  async function drainMicrotasks(): Promise<void> {
    for (let i = 0; i < 20; i++) {
      await Promise.resolve();
    }
  }

  beforeEach(() => {
    window.localStorage.clear();
    vi.useFakeTimers();

    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    service = TestBed.inject(OfflineQueueService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
    vi.useRealTimers();
    window.localStorage.clear();
  });

  it('splits a queue larger than the server batch limit into consecutive requests', async () => {
    for (let i = 0; i < 125; i++) {
      service.enqueueKiosk(kioskEvent(`k${i}`));
    }
    expect(service.pendingCount().length).toBe(125);

    let done = false;
    service.syncNow().subscribe(() => (done = true));

    // First batch: exactly the server cap of 100 events.
    const first = httpMock.expectOne(
      req => req.url === kioskEndpoint && req.body.events.length === 100,
    );
    first.flush(resultFor(first.request.body.events as OfflineKioskClockEvent[], 'applied'));
    await drainMicrotasks();

    // Second batch: the remaining 25 events.
    const second = httpMock.expectOne(
      req => req.url === kioskEndpoint && req.body.events.length === 25,
    );
    second.flush(resultFor(second.request.body.events as OfflineKioskClockEvent[], 'applied'));
    await drainMicrotasks();

    expect(service.pendingCount().length).toBe(0);
    expect(done).toBe(true);
  });

  it('stops sending while the server buffers and drains the unseen tail afterwards', async () => {
    for (let i = 0; i < 150; i++) {
      service.enqueueKiosk(kioskEvent(`k${i}`));
    }

    service.syncNow().subscribe();

    // Kimai is down: the whole first chunk comes back buffered.
    const first = httpMock.expectOne(
      req => req.url === kioskEndpoint && req.body.events.length === 100,
    );
    first.flush(resultFor(first.request.body.events as OfflineKioskClockEvent[], 'buffered'));
    await drainMicrotasks();

    // The remaining chunk must NOT be sent while the server only buffers -
    // it would just pile onto the same outbox backlog. Everything stays queued.
    expect(service.pendingCount().length).toBe(150);

    // Mixed queue (buffered head + unseen tail) -> normal retry cadence.
    // Kimai has recovered: the buffered head applies now, and the flush
    // continues straight into the unseen tail within the same run.
    await vi.advanceTimersByTimeAsync(15_000);
    const retry = httpMock.expectOne(
      req => req.url === kioskEndpoint && req.body.events.length === 100,
    );
    retry.flush(resultFor(retry.request.body.events as OfflineKioskClockEvent[], 'applied'));
    await drainMicrotasks();

    const tail = httpMock.expectOne(
      req => req.url === kioskEndpoint && req.body.events.length === 50,
    );
    tail.flush(resultFor(tail.request.body.events as OfflineKioskClockEvent[], 'applied'));
    await drainMicrotasks();

    expect(service.pendingCount().length).toBe(0);
  });

  it('keeps already-resolved events when a later chunk fails mid-run', async () => {
    for (let i = 0; i < 120; i++) {
      service.enqueueKiosk(kioskEvent(`k${i}`));
    }

    service.syncNow().subscribe();

    const first = httpMock.expectOne(
      req => req.url === kioskEndpoint && req.body.events.length === 100,
    );
    first.flush(resultFor(first.request.body.events as OfflineKioskClockEvent[], 'applied'));
    await drainMicrotasks();

    const second = httpMock.expectOne(
      req => req.url === kioskEndpoint && req.body.events.length === 20,
    );
    second.flush({ }, { status: 500, statusText: 'Server Error' });
    await drainMicrotasks();

    // Partial progress survives: only the resolved 100 leave the queue.
    expect(service.pendingCount().length).toBe(20);
    expect(service.pendingCount()[0].event.eventId).toBe('k100');
  });

  it('sends kiosk events to the kiosk endpoint only', async () => {
    service.enqueueKiosk(kioskEvent('kk1'));
    service.syncNow().subscribe();
    const req = httpMock.expectOne(r => r.url === kioskEndpoint);
    req.flush(resultFor(req.request.body.events as OfflineKioskClockEvent[], 'applied'));
    await drainMicrotasks();
    expect(service.pendingCount().length).toBe(0);
    httpMock.expectNone(nfcEndpoint);
  });

  it('does not emit recovered when the server only buffers (API up, Kimai down)', async () => {
    let recovered = 0;
    service.recovered.subscribe(() => recovered++);

    service.enqueueKiosk(kioskEvent('buf1'));
    service.syncNow().subscribe();

    const req = httpMock.expectOne(r => r.url === kioskEndpoint);
    req.flush(resultFor(req.request.body.events as OfflineKioskClockEvent[], 'buffered'));
    await drainMicrotasks();

    // All events buffered => PIN login still impossible, terminal must stay
    // unlocked. The host must NOT get a "recovered" signal.
    expect(recovered).toBe(0);
  });

  it('emits recovered when at least one event was actually processed', async () => {
    let recovered = 0;
    service.recovered.subscribe(() => recovered++);

    service.enqueueKiosk(kioskEvent('ok1'));
    service.syncNow().subscribe();

    const req = httpMock.expectOne(r => r.url === kioskEndpoint);
    req.flush(resultFor(req.request.body.events as OfflineKioskClockEvent[], 'applied'));
    await drainMicrotasks();

    expect(recovered).toBe(1);
    expect(service.pendingCount().length).toBe(0);
  });

  it('skips a second syncNow() while a flush is in flight (in-flight guard)', async () => {
    service.enqueueKiosk(kioskEvent('g1'));
    service.enqueueKiosk(kioskEvent('g2'));

    let firstDone = false;
    service.syncNow().subscribe(() => (firstDone = true));

    // Second overlapping call (constructor/timer/NFC poll) must NOT send
    // another request - the in-flight run drains the same snapshot.
    let secondDone = false;
    service.syncNow().subscribe(() => (secondDone = true));
    await drainMicrotasks();

    const req = httpMock.expectOne(r => r.url === kioskEndpoint);
    req.flush(resultFor(req.request.body.events as OfflineKioskClockEvent[], 'applied'));
    await drainMicrotasks();

    expect(firstDone).toBe(true);
    expect(secondDone).toBe(true);
    expect(service.pendingCount().length).toBe(0);
    // Only ONE request was ever sent for the two overlapping calls.
    httpMock.expectNone(r => r.url === kioskEndpoint);
  });

  it('stops the whole replay (all groups) when a chunk fails with a network error', async () => {
    // Mixed queue: NFC group runs first, then the kiosk group. A network
    // failure in the NFC chunk must abort the kiosk group too - not just the
    // inner chunk loop - otherwise the kiosk events are sent against a dead
    // network (or worse, re-ordered) and the "stop here" intent is lost.
    service.enqueueNfc(nfcEvent('n1'));
    service.enqueueKiosk(kioskEvent('k1'));

    service.syncNow().subscribe();

    const nfcReq = httpMock.expectOne(r => r.url === nfcEndpoint);
    nfcReq.flush({}, { status: 500, statusText: 'Server Error' });
    await drainMicrotasks();

    // Kiosk group must NOT be sent: both queued events remain.
    httpMock.expectNone(r => r.url === kioskEndpoint);
    expect(service.pendingCount().length).toBe(2);
  });
});