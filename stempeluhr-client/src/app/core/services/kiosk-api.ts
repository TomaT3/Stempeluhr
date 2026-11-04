import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { catchError, throwError, timeout } from 'rxjs';

import { Absence, KioskAbsenceOverview, SubmitAbsence } from '../models/absence.models';
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
  WorkTimeHints,
} from '../models/kiosk.models';
import { LOCAL_NFC_SCAN_PORT } from './local-nfc-scan.service';

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

/** Woher ein Stempel kommt (Issue #146). */
export interface ClockOptions {
  /** Über den Agenten des authentifizierten Terminals senden. */
  viaAgent?: boolean;
  /** Nur die Seite /clock sagt das: 'web'. Karte oder PIN erkennt der Server selbst. */
  source?: 'web' | null;
}

@Injectable({
  providedIn: 'root',
})
export class KioskApi {
  private readonly http = inject(HttpClient);
  private readonly agentPort = inject(LOCAL_NFC_SCAN_PORT);

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
    options: ClockOptions = {},
  ) {
    const body = { employeeId, pin, action, nfcCardId, taskId, eventId, ...(options.source ? { source: options.source } : {}) };
    const direct = this.http.post<ClockStatus>('/api/kiosk/clock', body).pipe(timeout(REQUEST_TIMEOUT_MS));
    if (!options.viaAgent) {
      return direct;
    }
    // Über den Agenten erfährt der Server das Terminal (Issue #146). Ein
    // älterer Agent kennt die Route nicht (404), einer ohne Token antwortet
    // 503, ein gestoppter Agent (Update, Absturz) ist nicht erreichbar (0) -
    // in allen drei Fällen kam nichts beim Server an: dann direkt, wie
    // bisher. Sonst bliebe ein PIN-Stempel in der Queue, deren Nachtrag
    // ebenfalls nur über den Agenten läuft. Ein Timeout (kein HTTP-Status)
    // fällt nicht zurück: der Agent könnte die Anfrage schon weitergegeben haben.
    return this.http
      .post<ClockStatus>(`http://127.0.0.1:${this.agentPort}/terminal/clock`, body)
      .pipe(
        timeout(REQUEST_TIMEOUT_MS),
        catchError(error =>
          error instanceof HttpErrorResponse && [0, 404, 503].includes(error.status) ? direct : throwError(() => error),
        ),
      );
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

  /**
   * Abwesenheiten (Urlaub, Krank, Gleittag; nur online, keine Queue).
   * Anmeldung wie bei den Korrekturanträgen, Tage als `yyyy-MM-dd`. Ein
   * unzulässiger Antrag kommt als 400 `{ message }` auf Deutsch. Gebucht wird
   * hier nichts: erst die Freigabe trägt die Tage in Kimai ein.
   */
  absenceOverview(auth: CorrectionAuth) {
    return this.http
      .post<KioskAbsenceOverview>('/api/kiosk/absences/balance', authBody(auth))
      .pipe(timeout(REQUEST_TIMEOUT_MS));
  }

  submitAbsence(absence: SubmitAbsence) {
    return this.http.post<Absence>('/api/kiosk/absences', absence).pipe(timeout(REQUEST_TIMEOUT_MS));
  }

  /** Offene Anträge und die der letzten 12 Monate, neueste zuerst. */
  myAbsences(auth: CorrectionAuth) {
    return this.http.post<Absence[]>('/api/kiosk/absences/mine', authBody(auth)).pipe(timeout(REQUEST_TIMEOUT_MS));
  }

  /** Zieht einen eigenen offenen Antrag zurück (404 fremd/unbekannt, 409 schon entschieden). */
  withdrawAbsence(auth: CorrectionAuth, id: string) {
    return this.http
      .post<Absence>(`/api/kiosk/absences/${encodeURIComponent(id)}/withdraw`, authBody(auth))
      .pipe(timeout(REQUEST_TIMEOUT_MS));
  }

  /**
   * Arbeitszeit-Hinweise nach der Anmeldung: rein lesend, bucht nichts.
   * Anmeldung wie bei den Korrekturanträgen.
   */
  workTimeHints(auth: CorrectionAuth) {
    return this.http.post<WorkTimeHints>('/api/kiosk/work-time-hints', authBody(auth)).pipe(timeout(REQUEST_TIMEOUT_MS));
  }

  /** Server-Version (aus der AssemblyInformationalVersion, im Container = Release-Tag). */
  health() {
    return this.http.get<HealthStatus>('/api/health');
  }
}
