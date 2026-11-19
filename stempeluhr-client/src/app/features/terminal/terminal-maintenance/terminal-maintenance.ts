import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, DestroyRef, inject, output, signal } from '@angular/core';
import { Subscription, catchError, exhaustMap, of, tap, throwError, timer } from 'rxjs';

import { HiddenSecurity, TerminalWifiApi, WifiNetwork, WifiStatus } from '../../../core/services/terminal-wifi';
import { OnscreenKeyboard } from '../../../shared/components/onscreen-keyboard/onscreen-keyboard';

type Step = 'pin' | 'overview' | 'hidden' | 'password' | 'connecting' | 'result';

interface Target {
  ssid: string;
  security: string;
  hidden: boolean;
}

/** Ohne Berührung schließt der Dialog und beendet die Sitzung. */
export const MAINTENANCE_IDLE_MS = 3 * 60_000;
export const CONNECT_POLL_MS = 2_000;
/** Länger, als der Agent auf den Helfer wartet (330 s, inkl. Rückfall auf das alte WLAN). */
export const CONNECT_GIVE_UP_MS = 345_000;
const PIN_MAX_LENGTH = 12;

/** OWE (Enhanced Open) ist verschlüsselt, hat aber kein Passwort. */
function needsPassword(security: string): boolean {
  return !!security && security.toUpperCase() !== 'OWE';
}

/**
 * WLAN am eingebauten Terminal ändern: versteckt hinter einer Geste auf der
 * Uhr und der Wartungs-PIN. Läuft vollständig über den lokalen Agenten, also
 * auch ohne Netz.
 */
@Component({
  selector: 'app-terminal-maintenance',
  imports: [OnscreenKeyboard],
  templateUrl: './terminal-maintenance.html',
  styleUrl: './terminal-maintenance.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '(pointerdown)': 'touch()',
  },
})
export class TerminalMaintenance {
  private readonly api = inject(TerminalWifiApi);
  readonly closed = output<void>();

  readonly step = signal<Step>('pin');
  readonly pin = signal('');
  readonly message = signal('');
  readonly busy = signal(false);
  readonly status = signal<WifiStatus | null>(null);
  readonly networks = signal<WifiNetwork[]>([]);
  readonly scanning = signal(false);
  readonly target = signal<Target | null>(null);
  readonly password = signal('');
  readonly showPassword = signal(false);
  readonly hiddenSsid = signal('');
  /** Verschlüsselung eines versteckten WLANs; der Scan kennt es nicht. */
  readonly hiddenSecurity = signal<HiddenSecurity>('WPA2');
  readonly hiddenSecurityOptions: ReadonlyArray<{ value: HiddenSecurity; label: string }> = [
    { value: 'WPA2', label: 'WPA2 / WPA3' },
    { value: 'WPA3', label: 'Nur WPA3' },
    { value: '', label: 'Offen' },
  ];
  readonly result = signal<{ ok: boolean; text: string } | null>(null);

  private session: string | null = null;
  private idleTimer: ReturnType<typeof setTimeout> | null = null;
  private requests = new Subscription();
  private poll: Subscription | null = null;

  constructor() {
    this.touch();
    inject(DestroyRef).onDestroy(() => {
      this.clearIdle();
      this.requests.unsubscribe();
      this.poll?.unsubscribe();
    });
  }

  touch(): void {
    this.clearIdle();
    this.idleTimer = setTimeout(() => this.close(), MAINTENANCE_IDLE_MS);
  }

  close(): void {
    if (this.session) {
      this.api.lock(this.session).subscribe({ error: () => undefined });
      this.session = null;
    }
    this.closed.emit();
  }

  // --- PIN ---

  pressDigit(digit: string): void {
    if (this.pin().length < PIN_MAX_LENGTH) this.pin.update(pin => pin + digit);
  }

  deleteDigit(): void {
    this.pin.update(pin => pin.slice(0, -1));
  }

  clearPin(): void {
    this.pin.set('');
  }

  submitPin(): void {
    if (!this.pin() || this.busy()) return;
    this.busy.set(true);
    this.message.set('');
    this.requests.add(this.api.unlock(this.pin()).subscribe({
      next: session => {
        this.session = session;
        this.pin.set('');
        this.busy.set(false);
        this.step.set('overview');
        this.refresh();
      },
      error: (error: unknown) => {
        this.pin.set('');
        this.busy.set(false);
        this.message.set(this.unlockError(error));
      },
    }));
  }

  // --- Übersicht ---

  refresh(): void {
    const session = this.session;
    if (!session) return;
    this.scanning.set(true);
    this.requests.add(this.api.status(session).subscribe({
      next: answer => {
        this.status.set(answer.status);
        if (!answer.status) {
          this.message.set(answer.helper === 'missing'
            ? 'Die WLAN-Einstellung ist auf diesem Terminal noch nicht eingerichtet (Agent-Update abwarten).'
            : answer.message ?? 'WLAN-Status gerade nicht verfügbar.');
        }
      },
      error: (error: unknown) => this.handleError(error),
    }));
    this.requests.add(this.api.networks(session).subscribe({
      next: networks => {
        this.networks.set(networks);
        this.scanning.set(false);
      },
      error: (error: unknown) => {
        this.scanning.set(false);
        this.handleError(error);
      },
    }));
  }

  choose(network: WifiNetwork): void {
    this.target.set({ ssid: network.ssid, security: network.security, hidden: false });
    this.message.set('');
    if (!needsPassword(network.security)) {
      this.password.set('');
      this.connect();
      return;
    }
    this.openPassword();
  }

  openHidden(): void {
    this.hiddenSsid.set('');
    this.hiddenSecurity.set('WPA2');
    this.message.set('');
    this.step.set('hidden');
  }

  confirmHidden(): void {
    // Kein Trim: Leerzeichen am Rand sind Teil einer SSID.
    const ssid = this.hiddenSsid();
    if (!ssid.trim()) {
      this.message.set('Bitte den WLAN-Namen eingeben.');
      return;
    }
    const security = this.hiddenSecurity();
    this.target.set({ ssid, security, hidden: true });
    if (!security) {
      this.password.set('');
      this.connect();
      return;
    }
    this.openPassword();
  }

  backToOverview(): void {
    this.message.set('');
    this.password.set('');
    this.step.set('overview');
  }

  // --- Passwort und Verbinden ---

  togglePassword(): void {
    this.showPassword.update(show => !show);
  }

  maskedPin(): string {
    return this.pin() ? '•'.repeat(this.pin().length) : '–';
  }

  maskedPassword(): string {
    return this.showPassword() ? this.password() : '•'.repeat(this.password().length);
  }

  signalLabel(network: WifiNetwork): string {
    return `${network.signal} %${needsPassword(network.security) ? '' : ', offen'}`;
  }

  connect(): void {
    const target = this.target();
    const session = this.session;
    if (!target || !session || this.busy()) return;
    const password = this.password();
    // Ein gesichertes WLAN braucht ein WPA-Passwort; ein offenes (auch OWE) hat keins.
    if ((needsPassword(target.security) || password) && password.length < 8) {
      this.message.set('Das Passwort hat mindestens 8 Zeichen.');
      return;
    }
    this.busy.set(true);
    this.message.set('');
    const security = target.hidden ? (target.security as HiddenSecurity) : undefined;
    this.requests.add(this.api.connect(session, target.ssid, password, target.hidden, security).subscribe({
      next: () => this.waitForResult(session, target.ssid),
      error: (error: unknown) => {
        // 409: Ein anderer Versuch läuft noch - dieser Dialog hat ihn nicht
        // gestartet (busy() verhindert einen zweiten), etwa vor dem Schließen
        // und erneuten Öffnen. Auch bei gleicher SSID kann er ein anderes
        // Passwort haben: Sein Ergebnis darf nicht als das der neuen Eingabe
        // erscheinen.
        if (error instanceof HttpErrorResponse && error.status === 409) {
          const running: unknown = error.error?.ssid;
          this.busy.set(false);
          this.message.set(`Eine WLAN-Umstellung${typeof running === 'string' ? ` zu „${running}“` : ''} läuft noch; `
            + 'die neue Eingabe wurde nicht verwendet. Bitte warten und dann erneut versuchen.');
          return;
        }
        this.busy.set(false);
        this.handleError(error);
      },
    }));
  }

  retry(): void {
    this.result.set(null);
    const target = this.target();
    if (target?.hidden) {
      // Bei versteckten Netzen sind SSID und Verschlüsselung die häufigste
      // Ursache: zurück in den Schritt mit beiden (die Eingaben bleiben).
      this.message.set('');
      this.step.set('hidden');
    } else if (target && needsPassword(target.security)) {
      this.openPassword();
    } else {
      this.backToOverview();
      this.refresh();
    }
  }

  finish(): void {
    this.close();
  }

  private openPassword(): void {
    this.password.set('');
    this.showPassword.set(false);
    this.message.set('');
    this.step.set('password');
  }

  private waitForResult(session: string, ssid: string): void {
    this.step.set('connecting');
    // Monotone Uhr: Ein Pi ohne RTC stellt die Wanduhr genau dann um Stunden
    // vor, wenn das neue WLAN verbindet - mit Date.now() gäbe das Warten auf.
    const startedAt = performance.now();
    this.poll?.unsubscribe();
    this.poll = timer(CONNECT_POLL_MS, CONNECT_POLL_MS)
      .pipe(
        // Jeder Takt hält den Dialog offen und prüft die Obergrenze - auch
        // wenn gerade keine Antwort kommt. finishConnect() beendet die
        // Subscription synchron, der Takt erreicht exhaustMap dann nicht mehr.
        tap(() => {
          this.touch();
          if (performance.now() - startedAt > CONNECT_GIVE_UP_MS) {
            this.finishConnect({ ok: false, text: 'Keine Rückmeldung vom Terminal. Bitte den WLAN-Status prüfen.' });
          }
        }),
        // Eine langsame Abfrage läuft zu Ende, statt vom nächsten Takt
        // abgebrochen zu werden; Takte währenddessen entfallen.
        exhaustMap(() => this.api.status(session).pipe(
          // Nur eine abgelaufene Sitzung beendet das Warten. Ist der Agent kurz
          // nicht erreichbar oder antwortet nicht rechtzeitig, weiter fragen:
          // Das Ergebnis des Versuchs kommt sonst nie an.
          catchError((error: unknown) =>
            error instanceof HttpErrorResponse && error.status === 401 ? throwError(() => error) : of(null)),
        )),
      )
      .subscribe({
        next: answer => {
          const job = answer?.job;
          if (!job || job.state === 'running' || job.state === 'idle') return;
          if (answer.status) this.status.set(answer.status);
          const ip = answer.status?.ip;
          this.finishConnect(job.state === 'connected'
            ? { ok: true, text: `Verbunden mit „${ssid}“${ip ? ` (IP ${ip})` : ''}.` }
            : { ok: false, text: job.message || 'Verbindung fehlgeschlagen.' });
        },
        error: (error: unknown) => {
          this.poll = null;
          this.busy.set(false);
          this.handleError(error);
        },
      });
  }

  private finishConnect(result: { ok: boolean; text: string }): void {
    this.poll?.unsubscribe();
    this.poll = null;
    this.busy.set(false);
    this.password.set('');
    this.result.set(result);
    this.step.set('result');
  }

  private handleError(error: unknown): void {
    if (error instanceof HttpErrorResponse && error.status === 401) {
      this.session = null;
      this.poll?.unsubscribe();
      this.step.set('pin');
      this.message.set('Sitzung abgelaufen. Bitte die Wartungs-PIN erneut eingeben.');
      return;
    }
    if (error instanceof HttpErrorResponse && error.status === 502 && typeof error.error?.message === 'string') {
      this.message.set(error.error.message);
      return;
    }
    if (error instanceof HttpErrorResponse && error.status === 503) {
      this.message.set('Die WLAN-Einstellung ist auf diesem Terminal noch nicht eingerichtet (Agent-Update abwarten).');
      return;
    }
    this.message.set('Der Agent des Terminals antwortet nicht. Bitte erneut versuchen.');
  }

  private unlockError(error: unknown): string {
    if (!(error instanceof HttpErrorResponse)) return 'Der Agent des Terminals antwortet nicht.';
    switch (error.status) {
      case 401:
        return 'PIN falsch.';
      case 429: {
        const seconds = Number(error.error?.retryAfterSeconds);
        return `Zu viele Fehlversuche. Bitte ${Number.isFinite(seconds) && seconds > 0 ? seconds : 60} s warten.`;
      }
      case 403:
        return error.error?.error === 'maintenance disabled'
          ? 'Keine Wartungs-PIN eingerichtet (Admin-Seite, Abschnitt Terminal-Wartung).'
          : 'Zugriff verweigert.';
      case 404:
        return 'Der Agent dieses Terminals kennt die WLAN-Einstellung noch nicht (Agent-Update abwarten).';
      default:
        return 'Der Agent des Terminals antwortet nicht.';
    }
  }

  private clearIdle(): void {
    if (this.idleTimer !== null) {
      clearTimeout(this.idleTimer);
      this.idleTimer = null;
    }
  }
}
