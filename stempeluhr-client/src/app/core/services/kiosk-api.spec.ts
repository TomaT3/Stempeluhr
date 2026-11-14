import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Observable, TimeoutError } from 'rxjs';

import { AGENT_FEATURES_TTL_MS, KioskApi, REQUEST_TIMEOUT_MS, isTransientHttpStatus } from './kiosk-api';
import { KioskDiagnostics } from './kiosk-diagnostics';

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

  const auth = { employeeId: 'max', pin: '', nfcCardId: '04ABCD' };
  const absenceSubmission = {
    kind: 'vacation' as const, days: ['2026-10-26', '2026-10-27'], comment: null, source: 'term-1',
  };
  const submission = {
    kind: 'setEnd' as const, timesheetId: 7, begin: null, end: '2026-10-04T17:00', pauseBegin: null, pauseEnd: null,
    taskId: null, comment: null, source: 'term-1',
  };

  // A hanging connection must end in the offline path after a few seconds
  // instead of keeping the kiosk busy for minutes.
  const hangingCalls: Array<[string, string, () => Observable<unknown>]> = [
    ['clock', '/api/kiosk/clock', () => api.clock('max', '1234', 'start')],
    ['pinLogin', '/api/kiosk/pin-login', () => api.pinLogin('1234')],
    ['hoursOverview', '/api/kiosk/hours', () => api.hoursOverview('1234')],
    ['ping', '/api/health', () => api.ping()],
    ['correctionTimesheets', '/api/kiosk/corrections/timesheets', () => api.correctionTimesheets(auth)],
    ['submitCorrection', '/api/kiosk/corrections', () => api.submitCorrection({ ...auth, ...submission })],
    ['myCorrections', '/api/kiosk/corrections/mine', () => api.myCorrections(auth)],
    ['withdrawCorrection', '/api/kiosk/corrections/c1/withdraw', () => api.withdrawCorrection(auth, 'c1')],
    ['absenceOverview', '/api/kiosk/absences/balance', () => api.absenceOverview(auth)],
    ['submitAbsence', '/api/kiosk/absences', () => api.submitAbsence({ ...auth, ...absenceSubmission })],
    ['myAbsences', '/api/kiosk/absences/mine', () => api.myAbsences(auth)],
    ['withdrawAbsence', '/api/kiosk/absences/a1/withdraw', () => api.withdrawAbsence(auth, 'a1')],
  ];

  it('sends the event ID the kiosk queues on failure with the live stamp (issue #67)', () => {
    api.clock('max', '1234', 'pauseEnd', null, null, 'ev1').subscribe();

    const request = http.expectOne('/api/kiosk/clock');
    expect(request.request.body).toMatchObject({ action: 'pauseEnd', eventId: 'ev1' });
    request.flush({});
  });

  describe('source of a live stamp (issue #146)', () => {
    const agentClock = 'http://127.0.0.1:8737/terminal/clock';

    function spyOnFlow() {
      return vi.spyOn(TestBed.inject(KioskDiagnostics), 'flow');
    }

    it('records the route of a stamp without the credentials', () => {
      const flow = spyOnFlow();
      api.clock('max', '1234', 'stop', '04ABCD', null, 'ev1').subscribe();
      http.expectOne('/api/kiosk/clock').flush({});

      expect(flow).toHaveBeenCalledExactlyOnceWith('stamp.route', { eventId: 'ev1', employeeId: 'max', action: 'stop', route: 'direct' });
    });

    it.each([0, 404, 503])('records the fallback to the direct call after status %i', status => {
      const flow = spyOnFlow();
      api.clock('max', '1234', 'stop', null, null, 'ev1', { viaAgent: true }).subscribe();

      const agent = http.expectOne(agentClock);
      if (status === 0) agent.error(new ProgressEvent('error'));
      else agent.flush({}, { status, statusText: 'agent' });
      http.expectOne('/api/kiosk/clock').flush({});

      expect(flow.mock.calls.map(call => call[1])).toEqual([
        { eventId: 'ev1', employeeId: 'max', action: 'stop', route: 'agent' },
        { eventId: 'ev1', employeeId: 'max', action: 'stop', route: 'direct', fallback: status },
      ]);
    });

    it('records no fallback when the agent passes a rejection on', () => {
      const flow = spyOnFlow();
      api.clock('max', '1234', 'stop', null, null, 'ev1', { viaAgent: true }).subscribe({ error: () => undefined });
      http.expectOne(agentClock).flush({}, { status: 401, statusText: 'x' });

      expect(flow.mock.calls.map(call => call[1]?.route)).toEqual(['agent']);
    });

    it('posts through the agent, which adds the terminal token, with the credentials unchanged', () => {
      let result: unknown;
      api.clock('max', '', 'start', '04ABCD', null, 'ev1', { viaAgent: true }).subscribe(status => (result = status));

      const request = http.expectOne(agentClock);
      expect(request.request.body).toEqual({
        employeeId: 'max', pin: '', action: 'start', nfcCardId: '04ABCD', taskId: null, eventId: 'ev1',
      });
      request.flush({ stateText: 'Eingestempelt' });
      http.expectNone('/api/kiosk/clock');
      expect(result).toEqual({ stateText: 'Eingestempelt' });
    });

    it.each([404, 503])('stamps directly when the agent answers %i (older agent, no token)', status => {
      let result: unknown;
      api.clock('max', '1234', 'stop', null, null, 'ev1', { viaAgent: true }).subscribe(answer => (result = answer));

      http.expectOne(agentClock).flush({}, { status, statusText: 'agent' });
      http.expectOne('/api/kiosk/clock').flush({ stateText: 'Ausgestempelt' });
      expect(result).toEqual({ stateText: 'Ausgestempelt' });
    });

    it('stamps directly when the agent is unreachable (stopped for an update)', () => {
      let result: unknown;
      api.clock('max', '1234', 'stop', null, null, 'ev1', { viaAgent: true }).subscribe(answer => (result = answer));

      http.expectOne(agentClock).error(new ProgressEvent('error'));
      http.expectOne('/api/kiosk/clock').flush({ stateText: 'Ausgestempelt' });
      expect(result).toEqual({ stateText: 'Ausgestempelt' });
    });

    it.each([401, 502])('passes status %i on without a second request', status => {
      let failure: { status?: number } | undefined;
      api.clock('max', '1234', 'stop', null, null, 'ev1', { viaAgent: true }).subscribe({ error: error => (failure = error) });

      http.expectOne(agentClock).flush({}, { status, statusText: 'x' });
      http.expectNone('/api/kiosk/clock');
      expect(failure?.status).toBe(status);
    });

    it('does not stamp a second time after a timeout - the agent may have forwarded it', () => {
      let failure: unknown;
      api.clock('max', '1234', 'stop', null, null, 'ev1', { viaAgent: true }).subscribe({ error: error => (failure = error) });

      http.expectOne(agentClock);
      vi.advanceTimersByTime(REQUEST_TIMEOUT_MS);
      http.expectNone('/api/kiosk/clock');
      expect(failure).toBeInstanceOf(TimeoutError);
    });

    it('names the browser only for the /clock page', () => {
      api.clock('max', '1234', 'start', null, null, 'ev1', { source: 'web' }).subscribe();
      expect(http.expectOne('/api/kiosk/clock').request.body).toMatchObject({ source: 'web' });

      api.clock('max', '1234', 'start').subscribe();
      expect(http.expectOne('/api/kiosk/clock').request.body).not.toHaveProperty('source');
    });
  });

  describe('traceparent (issue #148)', () => {
    const eventId = '4bf92f3577b34da6a3ce929d0e0e4736';
    const agentClock = 'http://127.0.0.1:8737/terminal/clock';
    const agentHealth = 'http://127.0.0.1:8737/health';
    const traceOf = (eventId: string) => new RegExp(`^00-${eventId}-[0-9a-f]{16}-01$`);

    function stampViaAgent() {
      api.clock('max', '1234', 'stop', null, null, eventId, { viaAgent: true }).subscribe({ error: () => undefined });
    }

    it('sends the event ID as trace ID with a direct stamp', () => {
      api.clock('max', '1234', 'stop', null, null, eventId).subscribe();

      expect(http.expectOne('/api/kiosk/clock').request.headers.get('traceparent')).toMatch(traceOf(eventId));
    });

    it('sends no traceparent without a usable event ID', () => {
      api.clock('max', '1234', 'stop').subscribe();

      expect(http.expectOne('/api/kiosk/clock').request.headers.has('traceparent')).toBe(false);
    });

    it('sends it to the agent only once the agent names the feature', () => {
      stampViaAgent();
      expect(http.expectOne(agentClock).request.headers.has('traceparent')).toBe(false);
      http.expectOne(agentHealth).flush({ ok: true, version: '1.0.0', features: ['traceparent'] });

      stampViaAgent();
      expect(http.expectOne(agentClock).request.headers.get('traceparent')).toMatch(traceOf(eventId));
      http.expectNone(agentHealth);
    });

    it('never sends it to an older agent, which would fail the preflight, and asks again later', () => {
      stampViaAgent();
      http.expectOne(agentClock);
      http.expectOne(agentHealth).flush({ ok: true, version: '0.9.0' });

      stampViaAgent();
      expect(http.expectOne(agentClock).request.headers.has('traceparent')).toBe(false);
      http.expectNone(agentHealth);

      vi.advanceTimersByTime(AGENT_FEATURES_TTL_MS.missing);
      stampViaAgent();
      http.expectOne(agentClock);
      http.expectOne(agentHealth);
    });

    it('asks again after the agent was unreachable, and keeps the header on the direct fallback', () => {
      stampViaAgent();
      http.expectOne(agentClock);
      http.expectOne(agentHealth).flush({ ok: true, features: ['traceparent'] });

      stampViaAgent();
      http.expectOne(agentClock).error(new ProgressEvent('error'));
      // Einmal ohne Header über den Agenten, dann direkt.
      const retry = http.expectOne(agentClock);
      expect(retry.request.headers.has('traceparent')).toBe(false);
      http.expectNone('/api/kiosk/clock');
      retry.error(new ProgressEvent('error'));
      expect(http.expectOne('/api/kiosk/clock').request.headers.get('traceparent')).toMatch(traceOf(eventId));

      stampViaAgent();
      expect(http.expectOne(agentClock).request.headers.has('traceparent')).toBe(false);
      http.expectOne(agentHealth);
    });

    it('retries via the agent without the header when a rolled-back agent rejects it, keeping the terminal', () => {
      stampViaAgent();
      http.expectOne(agentClock);
      http.expectOne(agentHealth).flush({ ok: true, features: ['traceparent'] });

      let status: unknown;
      api.clock('max', '1234', 'stop', null, null, eventId, { viaAgent: true }).subscribe(result => (status = result));
      const traced = http.expectOne(agentClock);
      expect(traced.request.headers.has('traceparent')).toBe(true);
      traced.error(new ProgressEvent('error'));

      const retry = http.expectOne(agentClock);
      expect(retry.request.headers.has('traceparent')).toBe(false);
      retry.flush({ ok: true });
      http.expectNone('/api/kiosk/clock');
      expect(status).toEqual({ ok: true });
    });

    it.each([404, 503])('asks again after the agent answered %i', status => {
      stampViaAgent();
      http.expectOne(agentClock);
      http.expectOne(agentHealth).flush({ ok: true, features: ['traceparent'] });

      stampViaAgent();
      http.expectOne(agentClock).flush({}, { status, statusText: 'x' });
      http.expectOne('/api/kiosk/clock');

      stampViaAgent();
      expect(http.expectOne(agentClock).request.headers.has('traceparent')).toBe(false);
      http.expectOne(agentHealth);
    });
  });

  it('authenticates the correction calls with employee ID and card (no PIN) like the clock call', () => {
    api.correctionTimesheets(auth).subscribe();
    const request = http.expectOne('/api/kiosk/corrections/timesheets');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ employeeId: 'max', pin: '', nfcCardId: '04ABCD' });
    request.flush({ timeZone: 'Europe/Berlin', shifts: [] });
  });

  it('submits a correction with its times as local text and the source', () => {
    api.submitCorrection({ ...auth, ...submission }).subscribe();
    const request = http.expectOne('/api/kiosk/corrections');
    expect(request.request.body).toEqual({ ...auth, ...submission });
    request.flush({});
  });

  it('withdraws by ID with the same authentication', () => {
    api.withdrawCorrection({ employeeId: 'max', pin: '1234', nfcCardId: null }, 'c1').subscribe();
    const request = http.expectOne('/api/kiosk/corrections/c1/withdraw');
    expect(request.request.body).toEqual({ employeeId: 'max', pin: '1234', nfcCardId: null });
    request.flush({});
  });

  it('asks for the absence account with only the login fields', () => {
    api.absenceOverview({ ...auth, extra: 'x' } as typeof auth).subscribe();
    const request = http.expectOne('/api/kiosk/absences/balance');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ employeeId: 'max', pin: '', nfcCardId: '04ABCD' });
    request.flush({ today: '2026-10-16', balances: [], kinds: [], days: [] });
  });

  it('submits an absence with its days as local dates and the source', () => {
    api.submitAbsence({ ...auth, ...absenceSubmission }).subscribe();
    const request = http.expectOne('/api/kiosk/absences');
    expect(request.request.body).toEqual({ ...auth, ...absenceSubmission });
    request.flush({});
  });

  it('lists and withdraws own absences with the same login, the ID escaped', () => {
    api.myAbsences(auth).subscribe();
    const mine = http.expectOne('/api/kiosk/absences/mine');
    expect(mine.request.body).toEqual(auth);
    mine.flush([]);

    api.withdrawAbsence({ employeeId: 'max', pin: '1234', nfcCardId: null }, 'a/1').subscribe();
    const request = http.expectOne('/api/kiosk/absences/a%2F1/withdraw');
    expect(request.request.body).toEqual({ employeeId: 'max', pin: '1234', nfcCardId: null });
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
