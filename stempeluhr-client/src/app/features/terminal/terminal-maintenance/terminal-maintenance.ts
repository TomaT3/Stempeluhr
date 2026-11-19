import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, DestroyRef, inject, output, signal } from '@angular/core';
import { Subscription, switchMap, timer } from 'rxjs';

import { TerminalWifiApi, WifiNetwork, WifiStatus } from '../../../core/services/terminal-wifi';
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
/** Etwas länger als der Helfer (55 s Verbinden plus Rückfall auf das alte WLAN). */
export const CONNECT_GIVE_UP_MS = 150_000;
const PIN_MAX_LENGTH = 12;

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
      next: answer => this.status.set(answer.status),
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
    if (!network.security) {
      this.password.set('');
      this.connect();
      return;
    }
    this.openPassword();
  }

  openHidden(): void {
    this.hiddenSsid.set('');
    this.message.set('');
    this.step.set('hidden');
  }

  confirmHidden(): void {
    const ssid = this.hiddenSsid().trim();
    if (!ssid) {
      this.message.set('Bitte den WLAN-Namen eingeben.');
      return;
    }
    this.target.set({ ssid, security: 'WPA2', hidden: true });
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
    return `${network.signal} %${network.security ? '' : ', offen'}`;
  }

  connect(): void {
    const target = this.target();
    const session = this.session;
    if (!target || !session || this.busy()) return;
    const password = this.password();
    // Ein verstecktes WLAN ohne Passwort ist offen; sonst gilt WPA.
    if ((target.security || (target.hidden && password)) && password.length < 8) {
      this.message.set('Das Passwort hat mindestens 8 Zeichen.');
      return;
    }
    this.busy.set(true);
    this.message.set('');
    this.requests.add(this.api.connect(session, target.ssid, password, target.hidden).subscribe({
      next: () => this.waitForResult(session, target.ssid),
      error: (error: unknown) => {
        // 409: Ein Versuch läuft bereits (z. B. Doppeltipp) - auf ihn warten.
        if (error instanceof HttpErrorResponse && error.status === 409) {
          this.waitForResult(session, target.ssid);
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
    if (target?.security || target?.hidden) {
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
    const startedAt = Date.now();
    this.poll?.unsubscribe();
    this.poll = timer(CONNECT_POLL_MS, CONNECT_POLL_MS)
      .pipe(switchMap(() => this.api.status(session)))
      .subscribe({
        next: ({ status, job }) => {
          this.touch();
          if (job.state === 'running') {
            if (Date.now() - startedAt > CONNECT_GIVE_UP_MS) {
              this.finishConnect({ ok: false, text: 'Keine Rückmeldung vom Terminal. Bitte erneut versuchen.' });
            }
            return;
          }
          this.status.set(status);
          this.finishConnect(job.state === 'connected'
            ? { ok: true, text: `Verbunden mit „${ssid}“${status.ip ? ` (IP ${status.ip})` : ''}.` }
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
