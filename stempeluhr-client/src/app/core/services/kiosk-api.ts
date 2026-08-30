import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';

import { ClockAction, ClockStatus, HealthStatus, HoursOverview, KioskEmployeeSession, NfcClockEvent, NfcLatestEvent } from '../models/kiosk.models';

@Injectable({
  providedIn: 'root',
})
export class KioskApi {
  private readonly http = inject(HttpClient);

  pinLogin(pin: string) {
    return this.http.post<KioskEmployeeSession>('/api/kiosk/pin-login', { pin });
  }

  clock(employeeId: string, pin: string, action: ClockAction, nfcCardId: string | null = null) {
    return this.http.post<ClockStatus>('/api/kiosk/clock', { employeeId, pin, action, nfcCardId });
  }

  latestNfcEvent(terminalId = 'default') {
    return this.http.get<NfcLatestEvent>('/api/nfc/events/latest', { params: { terminalId } });
  }

  /**
   * Resolves a scanned card id to an employee WITHOUT stamping (used by the
   * kiosk's local-scan path when the card is not in the local cache yet).
   * Returns the NfcClockEvent the server would publish for that card; 4xx
   * means the card is unknown or unreadable.
   */
  identify(cardId: string, terminalId = 'default') {
    return this.http.post<NfcClockEvent>('/api/kiosk/identify', { cardId, terminalId });
  }

  hoursOverview(pin: string) {
    return this.http.post<HoursOverview>('/api/kiosk/hours', { pin });
  }

  /** Server-Version (aus der AssemblyInformationalVersion, im Container = Release-Tag). */
  health() {
    return this.http.get<HealthStatus>('/api/health');
  }
}
