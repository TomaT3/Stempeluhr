import {
  HttpClient,
  HttpErrorResponse,
  HttpEventType,
  HttpInterceptorFn,
} from '@angular/common/http';
import { inject, Injectable, Injector } from '@angular/core';
import { finalize, Subscription, tap, timeout } from 'rxjs';
import { APP_VERSION } from '../app-version';
import { LOCAL_NFC_SCAN_PORT } from './local-nfc-scan.service';

export interface KioskDiagnosticState {
  screen: 'idle' | 'session';
  busy: boolean;
  blocked: 'none' | 'request' | 'backlog' | 'status';
  offline: boolean;
  pending: number;
  rejected: number;
}

/** Only allowlisted metadata is recorded. Never read PINs, cards, names,
 * error messages, response bodies, input contents or full request URLs. */
@Injectable({ providedIn: 'root' })
export class KioskDiagnostics {
  private readonly injector = inject(Injector);
  private readonly port = inject(LOCAL_NFC_SCAN_PORT);
  private timer: number | null = null;
  private request: Subscription | null = null;
  private lastInputAt: number | null = null;
  private lastBeatAt = 0;
  private lastState = '';
  private sequence = 0;
  private lastHealthStatus: number | null = null;
  private events: Array<Record<string, unknown>> = [];
  private active = new Map<number, { operation: string; started: number }>();
  private readonly onInput = () => {
    this.lastInputAt = performance.now();
  };
  private readonly onError = () => this.record({ kind: 'error', code: 'javascript' });
  private readonly onRejection = () => this.record({ kind: 'error', code: 'promise' });

  start(state: () => KioskDiagnosticState): () => void {
    this.stop();
    window.addEventListener('pointerdown', this.onInput, { passive: true, capture: true });
    window.addEventListener('keydown', this.onInput, { passive: true, capture: true });
    window.addEventListener('error', this.onError);
    window.addEventListener('unhandledrejection', this.onRejection);
    this.lastBeatAt = performance.now();
    this.timer = window.setInterval(() => this.heartbeat(state()), 15_000);
    return () => this.stop();
  }

  record(event: Record<string, unknown>): void {
    if (this.timer === null) return;
    this.events.push({ ...event, seq: ++this.sequence, at: new Date().toISOString() });
    this.events = this.events.slice(-40);
  }

  track(operation: string, eventId?: string): (status: number) => void {
    if (this.timer === null) return () => undefined;
    const id = ++this.sequence;
    const started = performance.now();
    this.active.set(id, { operation, started });
    return (status) => {
      this.active.delete(id);
      const recordHealth = operation === 'health' && status !== 200 && status !== this.lastHealthStatus;
      if (operation === 'health') this.lastHealthStatus = status;
      if (operation !== 'health' || recordHealth) {
        this.record({
          kind: 'http',
          operation,
          status,
          durationMs: Math.round(performance.now() - started),
          ...(eventId && /^[a-zA-Z0-9-]{1,64}$/.test(eventId) ? { eventId } : {}),
        });
      }
    };
  }

  private heartbeat(state: KioskDiagnosticState): void {
    const now = performance.now();
    const lagMs = Math.max(0, Math.round(now - this.lastBeatAt - 15_000));
    this.lastBeatAt = now;
    const serialized = JSON.stringify(state);
    if (serialized !== this.lastState) {
      this.record({ kind: 'state', ...state });
      this.lastState = serialized;
    }
    if (this.request && !this.request.closed) return;
    const sentEvents = this.events.slice(0, 20);
    const sentSequences = new Set(sentEvents.map((e) => e['seq']));
    this.request = this.injector
      .get(HttpClient)
      .post(`http://127.0.0.1:${this.port}/diagnostics/heartbeat`, {
        appVersion: APP_VERSION,
        ...state,
        lagMs,
        visible: document.visibilityState === 'visible',
        lastInputAgeMs: this.lastInputAt === null ? null : Math.round(now - this.lastInputAt),
        requests: [...this.active.values()]
          .slice(-10)
          .map((r) => ({ operation: r.operation, ageMs: Math.round(now - r.started) })),
        events: sentEvents,
      })
      .pipe(timeout(3000))
      .subscribe({
        next: () => {
          this.events = this.events.filter((e) => !sentSequences.has(e['seq']));
        },
        error: () => undefined, // Old/offline agents must never affect stamping.
      });
  }

  private stop(): void {
    if (this.timer !== null) window.clearInterval(this.timer);
    this.timer = null;
    this.request?.unsubscribe();
    this.request = null;
    window.removeEventListener('pointerdown', this.onInput, true);
    window.removeEventListener('keydown', this.onInput, true);
    window.removeEventListener('error', this.onError);
    window.removeEventListener('unhandledrejection', this.onRejection);
    this.active.clear();
    this.events = [];
    this.lastState = '';
    this.lastInputAt = null;
    this.lastHealthStatus = null;
  }
}

const operations: Record<string, string> = {
  '/api/kiosk/clock': 'clock',
  '/api/kiosk/clock/sync': 'sync',
  '/api/kiosk/pin-login': 'login',
  '/api/kiosk/identify': 'identify',
  '/api/kiosk/hours': 'hours',
  '/api/kiosk/work-time-hints': 'hints',
  '/api/health': 'health',
};

export const kioskDiagnosticsInterceptor: HttpInterceptorFn = (request, next) => {
  const operation =
    operations[request.url.split('?')[0]] ??
    (/^http:\/\/127\.0\.0\.1:\d+\/terminal\/sync$/.test(request.url) ? 'sync' : null);
  if (!operation) return next(request);
  const eventId =
    operation === 'clock' ? (request.body as { eventId?: string } | null)?.eventId : undefined;
  const finish = inject(KioskDiagnostics).track(operation, eventId);
  let status = -1; // Unsubscribed/timeout before an HTTP response.
  return next(request).pipe(
    tap({
      next: (event) => {
        if (event.type === HttpEventType.Response) status = event.status;
      },
      error: (error) => {
        status = error instanceof HttpErrorResponse ? error.status : 0;
      },
    }),
    finalize(() => finish(status)),
  );
};
