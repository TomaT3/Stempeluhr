import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import {
  KioskDiagnostics,
  KioskDiagnosticState,
  kioskDiagnosticsInterceptor,
} from './kiosk-diagnostics';

describe('KioskDiagnostics', () => {
  let http: HttpTestingController;
  let service: KioskDiagnostics;
  let stop: () => void;
  const heartbeat = 'http://127.0.0.1:8737/diagnostics/heartbeat';
  const state: KioskDiagnosticState = {
    screen: 'session',
    blocked: 'status',
    busy: false,
    offline: false,
    pending: 1,
    rejected: 0,
  };

  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([kioskDiagnosticsInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    service = TestBed.inject(KioskDiagnostics);
    http = TestBed.inject(HttpTestingController);
    stop = service.start(() => state);
  });
  afterEach(() => {
    stop();
    http.verify({ ignoreCancelled: true });
    vi.useRealTimers();
  });

  it('records blocked state and request errors without credentials or error bodies', () => {
    TestBed.inject(HttpClient)
      .post('/api/kiosk/clock', { eventId: 'event-123', pin: 'SECRET', nfcCardId: 'SECRET' })
      .subscribe({ error: () => undefined });
    http
      .expectOne('/api/kiosk/clock')
      .flush({ message: 'SECRET' }, { status: 400, statusText: 'Bad Request' });
    window.dispatchEvent(new Event('unhandledrejection'));
    window.dispatchEvent(new Event('pointerdown'));
    vi.advanceTimersByTime(15_000);
    const request = http.expectOne(heartbeat);
    expect(request.request.body).toMatchObject({ blocked: 'status', pending: 1 });
    expect(request.request.body.events).toContainEqual(
      expect.objectContaining({ operation: 'clock', status: 400, eventId: 'event-123' }),
    );
    expect(request.request.body.events).toContainEqual(
      expect.objectContaining({ kind: 'error', code: 'promise' }),
    );
    expect(JSON.stringify(request.request.body)).not.toContain('SECRET');
    request.flush({ ok: true });
  });

  it('times out a hanging agent, retries later and cleans up on leaving the page', () => {
    vi.advanceTimersByTime(15_000);
    const first = http.expectOne(heartbeat);
    vi.advanceTimersByTime(3000);
    expect(first.cancelled).toBe(true);
    vi.advanceTimersByTime(12_000);
    http.expectOne(heartbeat).flush({ ok: true });
    stop();
    vi.advanceTimersByTime(30_000);
    http.expectNone(heartbeat);
  });

  it('reports an unfinished clock request and bounds accumulated records', () => {
    const subscription = TestBed.inject(HttpClient).post('/api/kiosk/clock', {}).subscribe();
    http.expectOne('/api/kiosk/clock');
    for (let i = 0; i < 100; i++) window.dispatchEvent(new Event('unhandledrejection'));
    vi.advanceTimersByTime(15_000);
    const request = http.expectOne(heartbeat);
    expect(request.request.body.requests).toContainEqual(
      expect.objectContaining({ operation: 'clock' }),
    );
    expect(request.request.body.events.length).toBeLessThanOrEqual(20);
    request.flush({ ok: true });
    subscription.unsubscribe();
  });
});
