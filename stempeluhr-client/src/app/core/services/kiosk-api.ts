import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { timeout } from 'rxjs';

import { ClockAction, ClockStatus, HealthStatus, HoursOverview, KioskEmployeeSession, NfcClockEvent } from '../models/kiosk.models';

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

@Injectable({
  providedIn: 'root',
})
export class KioskApi {
  private readonly http = inject(HttpClient);

  pinLogin(pin: string) {
    return this.http.post<KioskEmployeeSession>('/api/kiosk/pin-login', { pin }).pipe(timeout(REQUEST_TIMEOUT_MS));
  }

  clock(employeeId: string, pin: string, action: ClockAction, nfcCardId: string | null = null) {
    return this.http
      .post<ClockStatus>('/api/kiosk/clock', { employeeId, pin, action, nfcCardId })
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

  /** Server-Version (aus der AssemblyInformationalVersion, im Container = Release-Tag). */
  health() {
    return this.http.get<HealthStatus>('/api/health');
  }
}
