import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { timeout } from 'rxjs';

import {
  ClockAction,
  ClockStatus,
  CorrectionAuth,
  CorrectionTimesheets,
  HealthStatus,
  HoursOverview,
  KioskCorrection,
  KioskEmployeeSession,
  NfcClockEvent,
  SubmitCorrection,
} from '../models/kiosk.models';

/** Timeout für den read-only identify-Call (Kiosk bleibt sonst stumm bei hängendem Backend). */
export const IDENTIFY_TIMEOUT_MS = 10_000;

/**
 * Obergrenze für PIN-Login, Stempel und Stundenabfrage. Hängt die Verbindung
 * (WLAN steht, Uplink tot; Cloudflare wartet auf den Origin), käme der Fehler
 * sonst erst nach Minuten - so lange bliebe der Kiosk "busy". Ein Timeout
 * trägt keinen HTTP-Status und landet damit im Offline-Pfad (Status 0).
 * Ein nach dem Timeout doch noch serverseitig übernommener Stempel ist
 * unkritisch: der Nachtrag prüft jede Aktion gegen den Kimai-Status und wird
 * dann zum No-op ("Lief bereits", "Pause lief bereits", ...).
 */
export const REQUEST_TIMEOUT_MS = 8_000;

/**
 * Antwort, die später gelingen kann: kein HTTP-Status (Netz, Timeout), 5xx
 * sowie 408/429, die die API von Kimai durchreicht. Dieselbe Einstufung wie
 * in der API (KimaiApiException.IsTransient) - alles andere ist endgültig.
 */
export function isTransientHttpStatus(status: number): boolean {
  return status === 0 || status >= 500 || status === 408 || status === 429;
}

/** Nur die Anmeldefelder: die Karten-Session schickt `pin` leer. */
function authBody(auth: CorrectionAuth): CorrectionAuth {
  return { employeeId: auth.employeeId, pin: auth.pin, nfcCardId: auth.nfcCardId };
}

@Injectable({
  providedIn: 'root',
})
export class KioskApi {
  private readonly http = inject(HttpClient);

  pinLogin(pin: string) {
    return this.http.post<KioskEmployeeSession>('/api/kiosk/pin-login', { pin }).pipe(timeout(REQUEST_TIMEOUT_MS));
  }

  /**
   * `taskId` nur bei 'start'/'switch': Tätigkeit bzw. Ziel, null = Standard-Tätigkeit.
   * `eventId`: die ID, unter der der Kiosk diese Aktion bei einem Fehler
   * einreiht - bleibt ein Pausenende oder Wechsel halb gebucht, setzt der
   * Nachtrag genau dieses Ereignis fort (Issue #67).
   */
  clock(
    employeeId: string,
    pin: string,
    action: ClockAction,
    nfcCardId: string | null = null,
    taskId: string | null = null,
    eventId: string | null = null,
  ) {
    return this.http
      .post<ClockStatus>('/api/kiosk/clock', { employeeId, pin, action, nfcCardId, taskId, eventId })
      .pipe(timeout(REQUEST_TIMEOUT_MS));
  }

  /**
   * Erreichbarkeits-Poll des Kiosks (jede Sekunde). Mit Timeout: ein
   * hängender Poll bliebe sonst offen, und der Offline-Zustand würde nie
   * erkannt.
   */
  ping() {
    return this.http.get<HealthStatus>('/api/health').pipe(timeout(REQUEST_TIMEOUT_MS));
  }

  /**
   * Resolves a scanned card id to an employee WITHOUT stamping (used by the
   * kiosk's local-scan path when the card is not in the local cache yet).
   * Returns the NfcClockEvent the server would publish for that card; 4xx
   * means the card is unknown or unreadable.
   */
  identify(cardId: string, terminalId = 'default') {
    // Timeout: identify ist read-only/idempotent und darf den Kiosk nie
    // stumm lassen, wenn das Backend hängt.
    return this.http.post<NfcClockEvent>('/api/kiosk/identify', { cardId, terminalId }).pipe(
      timeout(IDENTIFY_TIMEOUT_MS),
    );
  }

  hoursOverview(pin: string) {
    return this.http.post<HoursOverview>('/api/kiosk/hours', { pin }).pipe(timeout(REQUEST_TIMEOUT_MS));
  }

  /**
   * Korrekturanträge (nur online, keine Offline-Queue). Alle vier Aufrufe
   * melden mit Mitarbeiter-ID plus PIN oder Karten-ID an wie `clock`; die
   * Zeiten sind lokale Zeit `yyyy-MM-ddTHH:mm` in der Kimai-Zeitzone. Fehler
   * eines unzulässigen Antrags kommen als 400 `{ message }` auf Deutsch.
   */
  correctionTimesheets(auth: CorrectionAuth) {
    return this.http
      .post<CorrectionTimesheets>('/api/kiosk/corrections/timesheets', authBody(auth))
      .pipe(timeout(REQUEST_TIMEOUT_MS));
  }

  submitCorrection(correction: SubmitCorrection) {
    return this.http.post<KioskCorrection>('/api/kiosk/corrections', correction).pipe(timeout(REQUEST_TIMEOUT_MS));
  }

  /** Eigene Anträge der letzten 31 Tage. */
  myCorrections(auth: CorrectionAuth) {
    return this.http.post<KioskCorrection[]>('/api/kiosk/corrections/mine', authBody(auth)).pipe(timeout(REQUEST_TIMEOUT_MS));
  }

  /** Zieht einen eigenen offenen Antrag zurück (404 fremd/unbekannt, 409 schon entschieden). */
  withdrawCorrection(auth: CorrectionAuth, id: string) {
    return this.http
      .post<KioskCorrection>(`/api/kiosk/corrections/${encodeURIComponent(id)}/withdraw`, authBody(auth))
      .pipe(timeout(REQUEST_TIMEOUT_MS));
  }

  /** Server-Version (aus der AssemblyInformationalVersion, im Container = Release-Tag). */
  health() {
    return this.http.get<HealthStatus>('/api/health');
  }
}
