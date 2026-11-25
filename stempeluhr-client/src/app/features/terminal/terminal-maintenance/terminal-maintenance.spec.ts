import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { WifiNetwork, WifiStatus } from '../../../core/services/terminal-wifi';
import { CONNECT_GIVE_UP_MS, CONNECT_POLL_MS, MAINTENANCE_IDLE_MS, TerminalMaintenance } from './terminal-maintenance';

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
    expect(component.step()).toBe('password');
    component.password.set('geheim-123');
    component.connect();
    expect(http.expectOne(`${AGENT}/wifi/connect`).request.body)
      .toEqual({ session: 's1', ssid: 'Versteckt', psk: 'geheim-123', hidden: true, security: 'WPA2' });
  });

  it('retries a hidden network from the step with name and encryption', () => {
    unlocked();
    component.openHidden();
    component.hiddenSsid.set('Versteckt3');
    component.confirmHidden();
    component.password.set('geheim-123');
    component.connect();
    http.expectOne(`${AGENT}/wifi/connect`).flush({ ok: true }, { status: 202, statusText: 'Accepted' });
    pollStatus({ state: 'failed', ssid: 'Versteckt3', message: 'Verbindung abgelehnt. Bitte das Passwort prüfen.' });

    component.retry();
    fixture.detectChanges();

    expect(component.step()).toBe('hidden');
    expect(component.hiddenSsid()).toBe('Versteckt3');
    // Now the encryption can be corrected, e.g. to WPA3 only.
    component.hiddenSecurity.set('WPA3');
    component.confirmHidden();
    component.password.set('geheim-123');
    component.connect();
    expect(http.expectOne(`${AGENT}/wifi/connect`).request.body).toMatchObject({ ssid: 'Versteckt3', security: 'WPA3' });
  });

  it('passes WPA3 for a hidden WPA3-only network', () => {
    unlocked();
    component.openHidden();
    fixture.detectChanges();
    component.hiddenSsid.set('Versteckt3');
    (fixture.nativeElement.querySelector('[role="radio"]:nth-child(2)') as HTMLButtonElement).click();
    component.confirmHidden();
    component.password.set('geheim-123');
    component.connect();

    expect(http.expectOne(`${AGENT}/wifi/connect`).request.body)
      .toEqual({ session: 's1', ssid: 'Versteckt3', psk: 'geheim-123', hidden: true, security: 'WPA3' });
  });

  it('connects to an open hidden network without asking for a password', () => {
    unlocked();
    component.openHidden();
    component.hiddenSsid.set('Offen versteckt');
    component.hiddenSecurity.set('');
    component.confirmHidden();

    expect(http.expectOne(`${AGENT}/wifi/connect`).request.body)
      .toEqual({ session: 's1', ssid: 'Offen versteckt', psk: '', hidden: true, security: '' });
  });

  it('still requires a password for a hidden secured network', () => {
    unlocked();
    component.openHidden();
    component.hiddenSsid.set('Versteckt');
    component.confirmHidden();
    component.connect();
    fixture.detectChanges();

    expect(text()).toContain('mindestens 8 Zeichen');
    http.expectNone(`${AGENT}/wifi/connect`);
  });

  it('does not cancel slow status queries and stays open while waiting', () => {
    unlocked();
    component.choose(NETWORKS[1]);
    component.password.set('kunden-pass');
    component.connect();
    http.expectOne(`${AGENT}/wifi/connect`).flush({ ok: true }, { status: 202, statusText: 'Accepted' });

    // One query that takes 10 s: further ticks neither cancel it nor start more.
    vi.advanceTimersByTime(CONNECT_POLL_MS);
    const slow = http.expectOne(`${AGENT}/wifi/status`);
    vi.advanceTimersByTime(4 * CONNECT_POLL_MS);
    expect(slow.cancelled).toBe(false);
    http.expectNone(`${AGENT}/wifi/status`);
    slow.flush({ status: CONNECTED, job: { state: 'running', ssid: 'Kunde' } });

    // The agent hangs: every query runs into its own timeout, no answer
    // arrives - the dialog still does not close as idle.
    vi.advanceTimersByTime(MAINTENANCE_IDLE_MS + CONNECT_POLL_MS);
    expect(closed).toBe(0);
    const open = http.match(`${AGENT}/wifi/status`).filter(request => !request.cancelled);
    expect(open).toHaveLength(1);
    open[0].flush({ status: CONNECTED, job: { state: 'connected', ssid: 'Kunde' } });
    fixture.detectChanges();

    expect(component.step()).toBe('result');
  });

  it('explains a missing helper', () => {
    enterPin('482915');
    http.expectOne(`${AGENT}/maintenance/unlock`).flush({ session: 's1' });
    http.expectOne(`${AGENT}/wifi/status`)
      .flush({ status: null, job: { state: 'idle' }, helper: 'missing', message: 'WLAN-Status gerade nicht verfügbar.' });
    http.expectOne(`${AGENT}/wifi/networks`).flush({ networks: [] });
    fixture.detectChanges();

    expect(text()).toContain('noch nicht eingerichtet');
  });

  it('keeps waiting for the outcome when status queries fail meanwhile', () => {
    unlocked();
    component.choose(NETWORKS[1]);
    component.password.set('kunden-pass');
    component.connect();
    http.expectOne(`${AGENT}/wifi/connect`).flush({ ok: true }, { status: 202, statusText: 'Accepted' });

    vi.advanceTimersByTime(CONNECT_POLL_MS);
    http.expectOne(`${AGENT}/wifi/status`).flush({ error: 'wifi setup unavailable' }, { status: 503, statusText: 'x' });
    vi.advanceTimersByTime(CONNECT_POLL_MS);
    http.expectOne(`${AGENT}/wifi/status`).error(new ProgressEvent('error'));
    pollStatus({ state: 'running', ssid: 'Kunde' });
    expect(component.step()).toBe('connecting');
    // The helper could not read the status, the agent still reports the outcome.
    vi.advanceTimersByTime(CONNECT_POLL_MS);
    http.expectOne(`${AGENT}/wifi/status`).flush({ status: null, job: { state: 'connected', ssid: 'Kunde' }, message: 'x' });
    fixture.detectChanges();

    expect(text()).toContain('Verbunden mit „Kunde“.');
  });

  it('gives up after the agent would have answered', () => {
    unlocked();
    component.choose(NETWORKS[1]);
    component.password.set('kunden-pass');
    component.connect();
    http.expectOne(`${AGENT}/wifi/connect`).flush({ ok: true }, { status: 202, statusText: 'Accepted' });
    for (let elapsed = CONNECT_POLL_MS; elapsed <= CONNECT_GIVE_UP_MS; elapsed += CONNECT_POLL_MS) {
      vi.advanceTimersByTime(CONNECT_POLL_MS);
      http.expectOne(`${AGENT}/wifi/status`).flush({ error: 'x' }, { status: 503, statusText: 'x' });
    }
    // The first tick past the limit gives up without asking again.
    vi.advanceTimersByTime(CONNECT_POLL_MS);
    http.expectNone(`${AGENT}/wifi/status`);
    fixture.detectChanges();

    expect(component.step()).toBe('result');
    expect(text()).toContain('Keine Rückmeldung vom Terminal');
  });

  it('keeps waiting when the wall clock jumps ahead on connecting', () => {
    unlocked();
    component.choose(NETWORKS[1]);
    component.password.set('kunden-pass');
    component.connect();
    http.expectOne(`${AGENT}/wifi/connect`).flush({ ok: true }, { status: 202, statusText: 'Accepted' });

    // A Pi without RTC: timesyncd sets the clock hours ahead once the new WLAN is up.
    vi.setSystemTime(Date.now() + 5 * 60 * 60_000);
    pollStatus({ state: 'connected', ssid: 'Kunde' });

    expect(text()).toContain('Verbunden mit „Kunde“');
  });

  it('follows an earlier running attempt on 409 without passing it off as the new one', () => {
    unlocked();
    component.choose(NETWORKS[1]);
    component.password.set('richtig-pw');
    component.connect();
    http.expectOne(`${AGENT}/wifi/connect`)
      .flush({ error: 'connect already running', ssid: 'Alt' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(text()).toContain('Ein früherer Versuch zu „Alt“ läuft noch');
    expect(text()).toContain('Die neue Eingabe wurde nicht verwendet.');
    expect(component.password()).toBe('');
    pollStatus({ state: 'failed', ssid: 'Alt', message: 'Verbindung abgelehnt. Bitte das Passwort prüfen.' });

    expect(text()).toContain('Früherer Versuch: Verbindung abgelehnt.');
    // Now the own choice can be sent.
    component.retry();
    expect(component.step()).toBe('password');
  });

  it('labels the outcome of an earlier attempt to the same network as such', () => {
    // E.g. the first POST timed out in the kiosk, but the agent had started it.
    unlocked();
    component.choose(NETWORKS[1]);
    component.password.set('richtig-pw');
    component.connect();
    http.expectOne(`${AGENT}/wifi/connect`)
      .flush({ error: 'connect already running', ssid: 'Kunde' }, { status: 409, statusText: 'Conflict' });

    pollStatus({ state: 'connected', ssid: 'Kunde' });
    expect(text()).toContain('Früherer Versuch: Verbunden mit „Kunde“');
  });

  it('follows an attempt still running when the dialog opens', () => {
    enterPin('482915');
    http.expectOne(`${AGENT}/maintenance/unlock`).flush({ session: 's1' });
    http.expectOne(`${AGENT}/wifi/status`).flush({ status: CONNECTED, job: { state: 'running', ssid: 'Kunde' } });
    http.expectOne(`${AGENT}/wifi/networks`).flush({ networks: NETWORKS });
    fixture.detectChanges();

    expect(component.step()).toBe('connecting');
    expect(text()).toContain('Ein früherer Versuch zu „Kunde“ läuft noch');
    pollStatus({ state: 'connected', ssid: 'Kunde' });
    expect(text()).toContain('Früherer Versuch: Verbunden mit „Kunde“');
  });

  it('offers the WLAN list after an earlier attempt succeeded', () => {
    unlocked();
    component.choose(NETWORKS[1]);
    component.password.set('richtig-pw');
    component.connect();
    http.expectOne(`${AGENT}/wifi/connect`)
      .flush({ error: 'connect already running', ssid: 'Alt' }, { status: 409, statusText: 'Conflict' });
    pollStatus({ state: 'connected', ssid: 'Alt' });

    const list = [...fixture.nativeElement.querySelectorAll('button')]
      .find(button => (button as HTMLElement).textContent?.includes('WLAN-Liste')) as HTMLButtonElement | undefined;
    expect(list).toBeDefined();
    list!.click();

    // Same session, no new PIN: the own choice can be made now.
    expect(component.step()).toBe('overview');
    http.expectOne(`${AGENT}/wifi/status`).flush({ status: CONNECTED, job: { state: 'connected', ssid: 'Alt' } });
    http.expectOne(`${AGENT}/wifi/networks`).flush({ networks: NETWORKS });
    expect(closed).toBe(0);
  });

  it('offers only Fertig after an own attempt succeeded', () => {
    unlocked();
    component.choose(NETWORKS[1]);
    component.password.set('kunden-pass');
    component.connect();
    http.expectOne(`${AGENT}/wifi/connect`).flush({ ok: true }, { status: 202, statusText: 'Accepted' });
    pollStatus({ state: 'connected', ssid: 'Kunde' });

    expect(text()).not.toContain('WLAN-Liste');
  });

  it('unlocks the PIN entry again when the session expires while waiting', () => {
    // A job is running on open; the WLAN list request comes back 401 meanwhile.
    enterPin('482915');
    http.expectOne(`${AGENT}/maintenance/unlock`).flush({ session: 's1' });
    http.expectOne(`${AGENT}/wifi/status`).flush({ status: CONNECTED, job: { state: 'running', ssid: 'Kunde' } });
    expect(component.step()).toBe('connecting');
    http.expectOne(`${AGENT}/wifi/networks`).flush({ error: 'session' }, { status: 401, statusText: 'Unauthorized' });
    fixture.detectChanges();

    expect(component.step()).toBe('pin');
    expect(component.busy()).toBe(false);
    vi.advanceTimersByTime(CONNECT_POLL_MS);
    http.expectNone(`${AGENT}/wifi/status`);
    enterPin('482915');
    http.expectOne(`${AGENT}/maintenance/unlock`).flush({ session: 's1' });
    // A new refresh follows the still running job again.
    http.expectOne(`${AGENT}/wifi/status`).flush({ status: CONNECTED, job: { state: 'running', ssid: 'Kunde' } });
    http.expectOne(`${AGENT}/wifi/networks`).flush({ networks: NETWORKS });
    expect(component.step()).toBe('connecting');
  });

  it('retries an OWE network without asking for a password', () => {
    unlocked();
    component.choose({ ssid: 'Enhanced', signal: 60, security: 'OWE', inUse: false });
    http.expectOne(`${AGENT}/wifi/connect`).flush({ ok: true }, { status: 202, statusText: 'Accepted' });
    pollStatus({ state: 'failed', ssid: 'Enhanced', message: 'WLAN nicht gefunden. Ist es in Reichweite?' });

    component.retry();

    expect(component.step()).toBe('overview');
    http.expectOne(`${AGENT}/wifi/status`).flush({ status: CONNECTED, job: { state: 'failed' } });
    http.expectOne(`${AGENT}/wifi/networks`).flush({ networks: NETWORKS });
  });

  it('keeps spaces at the edges of a hidden SSID', () => {
    unlocked();
    component.openHidden();
    component.hiddenSsid.set(' ');
    component.confirmHidden();
    expect(component.message()).toBe('Bitte den WLAN-Namen eingeben.');

    component.hiddenSsid.set('Kunde ');
    component.hiddenSecurity.set('');
    component.confirmHidden();
    expect(http.expectOne(`${AGENT}/wifi/connect`).request.body).toMatchObject({ ssid: 'Kunde ' });
  });

  it('connects to an OWE network without asking for a password', () => {
    unlocked();
    component.choose({ ssid: 'Enhanced', signal: 60, security: 'OWE', inUse: false });

    expect(http.expectOne(`${AGENT}/wifi/connect`).request.body)
      .toEqual({ session: 's1', ssid: 'Enhanced', psk: '', hidden: false });
  });

  it('accepts a 64-digit hex key on the keyboard', () => {
    unlocked();
    component.choose(NETWORKS[1]);
    fixture.detectChanges();

    const keyboard = fixture.nativeElement.querySelector('app-onscreen-keyboard');
    expect(keyboard).not.toBeNull();
    expect(fixture.debugElement.query(element => element.nativeElement === keyboard).componentInstance.maxLength())
      .toBe(64);
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

  const button = (label: string) => [...(fixture.nativeElement as HTMLElement).querySelectorAll('button')]
    .find(candidate => candidate.textContent?.trim() === label) as HTMLButtonElement;

  it.each([
    ['Neu starten', 'reboot', 'Terminal neu starten?', 'Terminal startet neu'],
    ['Herunterfahren', 'poweroff', 'Terminal herunterfahren?', 'Terminal fährt herunter'],
  ])('%s after asking again', (label, action, question, progress) => {
    unlocked();
    button(label).click();
    fixture.detectChanges();
    expect(text()).toContain(question);
    http.expectNone(`${AGENT}/maintenance/power`);

    button(label).click();
    const request = http.expectOne(`${AGENT}/maintenance/power`);
    expect(request.request.body).toEqual({ session: 's1', action });
    request.flush({ ok: true }, { status: 202, statusText: 'Accepted' });
    fixture.detectChanges();

    expect(component.step()).toBe('powering');
    expect(text()).toContain(progress);
    // The agent ended the session already: closing sends no lock.
    component.close();
    http.expectNone(`${AGENT}/maintenance/lock`);
  });

  it('cancels a power action without asking the agent', () => {
    unlocked();
    button('Herunterfahren').click();
    fixture.detectChanges();
    button('Abbrechen').click();
    fixture.detectChanges();

    http.expectNone(`${AGENT}/maintenance/power`);
    expect(component.step()).toBe('overview');
  });

  it.each([
    [409, { message: 'Eine WLAN-Umstellung läuft noch. Bitte deren Ergebnis abwarten.' }, 'WLAN-Umstellung läuft noch'],
    [404, { error: 'not found' }, 'kennt Neustart und Herunterfahren noch nicht'],
    [503, { error: 'wifi setup unavailable' }, 'noch nicht eingerichtet'],
    [502, { message: 'Neustart bzw. Herunterfahren fehlgeschlagen.' }, 'Neustart bzw. Herunterfahren fehlgeschlagen.'],
  ])('explains a refused power action (%s)', (status, body, message) => {
    unlocked();
    component.askPower('reboot');
    component.confirmPower();
    http.expectOne(`${AGENT}/maintenance/power`).flush(body, { status, statusText: 'x' });
    fixture.detectChanges();

    expect(component.step()).toBe('overview');
    expect(text()).toContain(message);
  });
});
