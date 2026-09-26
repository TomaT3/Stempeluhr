import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Observable, TimeoutError } from 'rxjs';

import { KioskApi, REQUEST_TIMEOUT_MS } from './kiosk-api';

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
    ['latestNfcEvent', '/api/nfc/events/latest?terminalId=t1', () => api.latestNfcEvent('t1')],
  ];

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
