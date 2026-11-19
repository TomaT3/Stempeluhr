import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { WifiNetwork, WifiStatus } from '../../../core/services/terminal-wifi';
import { CONNECT_POLL_MS, MAINTENANCE_IDLE_MS, TerminalMaintenance } from './terminal-maintenance';

const AGENT = 'http://127.0.0.1:8737';

const CONNECTED: WifiStatus = {
  device: 'wlan0', state: 'connected', ssid: 'Stempeluhr-Service', ip: '172.20.10.4',
  connection: 'stempeluhr-service', serviceWifi: true, ethernet: false,
};
const NETWORKS: WifiNetwork[] = [
  { ssid: 'Stempeluhr-Service', signal: 90, security: 'WPA2', inUse: true },
  { ssid: 'Kunde', signal: 70, security: 'WPA2', inUse: false },
  { ssid: 'Gast', signal: 40, security: '', inUse: false },
];

describe('TerminalMaintenance', () => {
  let fixture: ComponentFixture<TerminalMaintenance>;
  let component: TerminalMaintenance;
  let http: HttpTestingController;
  let closed: number;

  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(TerminalMaintenance);
    component = fixture.componentInstance;
    closed = 0;
    component.closed.subscribe(() => closed++);
    fixture.detectChanges();
  });

  afterEach(() => {
    http.verify();
    vi.useRealTimers();
  });

  const text = () => (fixture.nativeElement as HTMLElement).textContent ?? '';

  function enterPin(pin: string): void {
    for (const digit of pin) component.pressDigit(digit);
    component.submitPin();
  }

  /** PIN accepted, status and WLAN list answered. */
  function unlocked(): void {
    enterPin('482915');
    const unlock = http.expectOne(`${AGENT}/maintenance/unlock`);
    expect(unlock.request.body).toEqual({ pin: '482915' });
    unlock.flush({ session: 's1' });
    http.expectOne(`${AGENT}/wifi/status`).flush({ status: CONNECTED, job: { state: 'idle' } });
    http.expectOne(`${AGENT}/wifi/networks`).flush({ networks: NETWORKS });
    fixture.detectChanges();
  }

  function pollStatus(job: object, status: WifiStatus = CONNECTED): void {
    vi.advanceTimersByTime(CONNECT_POLL_MS);
    const request = http.expectOne(`${AGENT}/wifi/status`);
    expect(request.request.body).toEqual({ session: 's1' });
    request.flush({ status, job });
    fixture.detectChanges();
  }

  it('shows status and networks after the PIN', () => {
    unlocked();

    expect(text()).toContain('Über das Wartungs-WLAN');
    expect(text()).toContain('IP 172.20.10.4');
    expect([...fixture.nativeElement.querySelectorAll('.network-name')].map(n => (n as HTMLElement).textContent))
      .toEqual(['Stempeluhr-Service', 'Kunde', 'Gast']);
  });

  it.each([
    [401, { error: 'wrong pin' }, 'PIN falsch.'],
    [429, { error: 'locked', retryAfterSeconds: 120 }, 'Bitte 120 s warten.'],
    [403, { error: 'maintenance disabled' }, 'Keine Wartungs-PIN eingerichtet'],
    [404, { error: 'not found' }, 'Agent-Update abwarten'],
  ])('explains a rejected PIN (%s)', (status, body, message) => {
    enterPin('000000');
    http.expectOne(`${AGENT}/maintenance/unlock`).flush(body, { status, statusText: 'x' });
    fixture.detectChanges();

    expect(text()).toContain(message);
    expect(component.step()).toBe('pin');
    expect(component.pin()).toBe('');
  });

  it('connects to a secured network with the typed password', () => {
    unlocked();
    component.choose(NETWORKS[1]);
    fixture.detectChanges();
    expect(text()).toContain('Passwort für „Kunde“');

    component.password.set('kunden-pass');
    component.connect();
    const connect = http.expectOne(`${AGENT}/wifi/connect`);
    expect(connect.request.body).toEqual({ session: 's1', ssid: 'Kunde', psk: 'kunden-pass', hidden: false });
    connect.flush({ ok: true }, { status: 202, statusText: 'Accepted' });
    fixture.detectChanges();
    expect(text()).toContain('Verbinde mit „Kunde“');

    pollStatus({ state: 'running', ssid: 'Kunde' });
    expect(component.step()).toBe('connecting');
    pollStatus({ state: 'connected', ssid: 'Kunde' }, { ...CONNECTED, ssid: 'Kunde', ip: '192.168.1.23', serviceWifi: false });

    expect(text()).toContain('Verbunden mit „Kunde“ (IP 192.168.1.23).');
    expect(component.password()).toBe('');
    component.finish();
    http.expectOne(`${AGENT}/maintenance/lock`).flush({ ok: true });
    expect(closed).toBe(1);
  });

  it('reports a failed attempt and offers to retry with a new password', () => {
    unlocked();
    component.choose(NETWORKS[1]);
    component.password.set('falsches-pw');
    component.connect();
    http.expectOne(`${AGENT}/wifi/connect`).flush({ ok: true }, { status: 202, statusText: 'Accepted' });
    pollStatus({ state: 'failed', ssid: 'Kunde', message: 'Verbindung abgelehnt. Bitte das Passwort prüfen.' });

    expect(text()).toContain('Nicht verbunden');
    expect(text()).toContain('Bitte das Passwort prüfen.');
    component.retry();
    expect(component.step()).toBe('password');
    expect(component.password()).toBe('');
  });

  it('connects to an open network without asking for a password', () => {
    unlocked();
    component.choose(NETWORKS[2]);

    const connect = http.expectOne(`${AGENT}/wifi/connect`);
    expect(connect.request.body).toEqual({ session: 's1', ssid: 'Gast', psk: '', hidden: false });
    connect.flush({ ok: true }, { status: 202, statusText: 'Accepted' });
    pollStatus({ state: 'connected', ssid: 'Gast' });
  });

  it('rejects a short password without asking the agent', () => {
    unlocked();
    component.choose(NETWORKS[1]);
    component.password.set('kurz');
    component.connect();
    fixture.detectChanges();

    expect(text()).toContain('mindestens 8 Zeichen');
    http.expectNone(`${AGENT}/wifi/connect`);
  });

  it('enters a hidden network by name', () => {
    unlocked();
    component.openHidden();
    component.confirmHidden();
    fixture.detectChanges();
    expect(text()).toContain('Bitte den WLAN-Namen eingeben.');

    component.hiddenSsid.set('Versteckt');
    component.confirmHidden();
    component.password.set('geheim-123');
    component.connect();
    expect(http.expectOne(`${AGENT}/wifi/connect`).request.body)
      .toEqual({ session: 's1', ssid: 'Versteckt', psk: 'geheim-123', hidden: true });
  });

  it('asks for the PIN again when the session expired', () => {
    unlocked();
    component.refresh();
    http.expectOne(`${AGENT}/wifi/status`).flush({ error: 'session expired' }, { status: 401, statusText: 'x' });
    http.expectOne(`${AGENT}/wifi/networks`).flush({ error: 'session expired' }, { status: 401, statusText: 'x' });
    fixture.detectChanges();

    expect(component.step()).toBe('pin');
    expect(text()).toContain('Sitzung abgelaufen');
  });

  it('shows the message of the helper', () => {
    enterPin('482915');
    http.expectOne(`${AGENT}/maintenance/unlock`).flush({ session: 's1' });
    http.expectOne(`${AGENT}/wifi/status`).flush({ status: CONNECTED, job: { state: 'idle' } });
    http.expectOne(`${AGENT}/wifi/networks`)
      .flush({ message: 'WLAN-Suche fehlgeschlagen: Gerät belegt' }, { status: 502, statusText: 'x' });
    fixture.detectChanges();

    expect(text()).toContain('WLAN-Suche fehlgeschlagen: Gerät belegt');
  });

  it('closes and ends the session after inactivity', () => {
    unlocked();
    vi.advanceTimersByTime(MAINTENANCE_IDLE_MS - 1);
    expect(closed).toBe(0);
    fixture.nativeElement.dispatchEvent(new Event('pointerdown'));
    vi.advanceTimersByTime(MAINTENANCE_IDLE_MS - 1);
    expect(closed).toBe(0);
    vi.advanceTimersByTime(1);

    expect(http.expectOne(`${AGENT}/maintenance/lock`).request.body).toEqual({ session: 's1' });
    expect(closed).toBe(1);
  });

  it('closes without a session request before the PIN', () => {
    component.close();

    http.expectNone(`${AGENT}/maintenance/lock`);
    expect(closed).toBe(1);
  });
});
