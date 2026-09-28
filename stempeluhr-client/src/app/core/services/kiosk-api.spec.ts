import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Observable, TimeoutError } from 'rxjs';

import { KioskApi, REQUEST_TIMEOUT_MS, isTransientHttpStatus } from './kiosk-api';

describe('KioskApi', () => {
  let api: KioskApi;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(KioskApi);
    http = TestBed.inject(HttpTestingController);
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  // A hanging connection must end in the offline path after a few seconds
  // instead of keeping the kiosk busy for minutes.
  const hangingCalls: Array<[string, string, () => Observable<unknown>]> = [
    ['clock', '/api/kiosk/clock', () => api.clock('max', '1234', 'start')],
    ['pinLogin', '/api/kiosk/pin-login', () => api.pinLogin('1234')],
    ['hoursOverview', '/api/kiosk/hours', () => api.hoursOverview('1234')],
    ['ping', '/api/health', () => api.ping()],
  ];

  it('sends the event ID the kiosk queues on failure with the live stamp (issue #67)', () => {
    api.clock('max', '1234', 'pauseEnd', null, null, 'ev1').subscribe();

    const request = http.expectOne('/api/kiosk/clock');
    expect(request.request.body).toMatchObject({ action: 'pauseEnd', eventId: 'ev1' });
    request.flush({});
  });

  it('classifies answers like the API: network, 5xx, 408 and 429 are transient', () => {
    expect([0, 408, 429, 500, 502, 503].every(isTransientHttpStatus)).toBe(true);
    expect([400, 401, 403, 404, 409, 422].some(isTransientHttpStatus)).toBe(false);
  });

  for (const [name, url, call] of hangingCalls) {
    it(`${name} fails with a TimeoutError when the server does not answer`, () => {
      let error: unknown = null;
      call().subscribe({ error: err => (error = err) });
      const request = http.expectOne(url);

      vi.advanceTimersByTime(REQUEST_TIMEOUT_MS - 1);
      expect(error).toBeNull();

      vi.advanceTimersByTime(1);
      expect(error).toBeInstanceOf(TimeoutError);
      expect(request.cancelled).toBe(true);
    });
  }
});
