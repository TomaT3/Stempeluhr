import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { AdminTerminalStatus } from '../../../core/models/admin.models';
import { AdminSession } from '../../../core/services/admin-session';
import { TerminalStatusPage } from './terminal-status-page';

describe('TerminalStatusPage', () => {
  beforeEach(() => sessionStorage.clear());
  afterEach(() => sessionStorage.clear());

  async function setup() {
    await TestBed.configureTestingModule({
      imports: [TerminalStatusPage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    const fixture = TestBed.createComponent(TerminalStatusPage);
    fixture.componentInstance.adminPassword.set('test-password');
    fixture.componentInstance.loadStatuses();
    return { fixture, http: TestBed.inject(HttpTestingController), page: fixture.nativeElement as HTMLElement };
  }

  it('shows problems, values and the state of every terminal', async () => {
    const { fixture, http, page } = await setup();
    const statuses: AdminTerminalStatus[] = [
      {
        terminalId: 'pi-01',
        state: 'problem',
        lastReportAt: new Date().toISOString(),
        problems: [{ kind: 'temperature', since: new Date().toISOString(), text: 'Temperatur 82 °C seit 12:00.' }],
        report: {
          agentVersion: '0.18.0', appVersion: '0.18.0', uiStatus: 'alive', heartbeatAgeSeconds: 4,
          screen: 'idle', blocked: 'none', busy: false, offline: false, pending: 0, rejected: 0,
          cpuPercent: 12, availableMemoryKb: 512_000, chromiumRssSumKb: 200_000, temperatureC: 82,
          pcscdRssKb: 102_400, pcscdAnonymousKb: 102_400, pcscdSwapKb: 51_200,
          pcscdAnonymousAndSwapKb: 153_600, pcscdPid: 726, pcscdStartTicks: 150,
          pcscdVersion: '2.5.2-1~stempeluhr13.1',
          throttledFlags: 0x50000, diskFreeMb: 9000, uptimeSeconds: 7200, load1: 0.4,
        },
        powerFlags: ['Unterspannung (seit Start)'],
      },
      { terminalId: 'pi-02', state: 'never', lastReportAt: null, problems: [], report: null, powerFlags: [] },
    ];

    const request = http.expectOne('/api/admin/terminal-statuses');
    expect(request.request.headers.get('X-Admin-Password')).toBe('test-password');
    request.flush(statuses);
    fixture.detectChanges();

    const rows = page.querySelectorAll('.terminal-row');
    expect(rows.length).toBe(2);
    expect(rows[0].getAttribute('data-state')).toBe('problem');
    expect(rows[0].textContent).toContain('Temperatur 82 °C seit 12:00.');
    expect(rows[0].textContent).toContain('500 MiB');
    expect(rows[0].textContent).toContain('150 MiB');
    expect(rows[0].textContent).toContain('2.5.2-1~stempeluhr13.1');
    expect(rows[0].textContent).toContain('2 h 0 min');
    expect(rows[0].textContent).toContain('Unterspannung (seit Start)');
    expect(rows[1].textContent).toContain('Noch nie gemeldet');
    expect(rows[1].textContent).toContain('Zuletzt gemeldet nie');
    expect(sessionStorage.getItem(AdminSession.StorageKey)).toBe('test-password');
    expect(page.querySelector('input[type="password"]')).toBeNull();
    fixture.destroy();
  });

  it('loads right away when already logged in on another admin page', async () => {
    sessionStorage.setItem(AdminSession.StorageKey, 'kept-password');
    await TestBed.configureTestingModule({
      imports: [TerminalStatusPage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    const fixture = TestBed.createComponent(TerminalStatusPage);
    const http = TestBed.inject(HttpTestingController);

    const request = http.expectOne('/api/admin/terminal-statuses');
    expect(request.request.headers.get('X-Admin-Password')).toBe('kept-password');
    request.flush([]);
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('input[type="password"]')).toBeNull();
    fixture.destroy();
  });

  it('stops refreshing and forgets the session on logout', async () => {
    vi.useFakeTimers();
    try {
      const { fixture, http, page } = await setup();
      http.expectOne('/api/admin/terminal-statuses').flush([]);

      fixture.componentInstance.logout();
      fixture.detectChanges();

      expect(sessionStorage.getItem(AdminSession.StorageKey)).toBeNull();
      expect(page.querySelector('input[type="password"]')).not.toBeNull();
      vi.advanceTimersByTime(TerminalStatusPage.RefreshIntervalMs * 2);
      http.expectNone('/api/admin/terminal-statuses');
      fixture.destroy();
    } finally {
      vi.useRealTimers();
    }
  });

  it('reports a wrong password without showing a list', async () => {
    const { fixture, http, page } = await setup();

    http.expectOne('/api/admin/terminal-statuses').flush(null, { status: 401, statusText: 'Unauthorized' });
    fixture.detectChanges();

    expect(page.querySelector('.terminal-list')).toBeNull();
    expect(page.querySelector('.message')?.textContent).toContain('Admin-Passwort stimmt nicht.');
    fixture.destroy();
  });

  it('cancels a pending request and refreshes nothing after leaving the page', async () => {
    vi.useFakeTimers();
    try {
      const { fixture, http } = await setup();
      const request = http.expectOne('/api/admin/terminal-statuses');

      fixture.destroy();
      expect(request.cancelled).toBe(true);

      vi.advanceTimersByTime(TerminalStatusPage.RefreshIntervalMs * 2);
      http.expectNone('/api/admin/terminal-statuses');
      http.verify();
    } finally {
      vi.useRealTimers();
    }
  });
});
