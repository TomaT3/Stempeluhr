import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';

import { ClockAction, ClockStatus, KioskEmployeeSession, NfcLatestEvent } from '../models/kiosk.models';

@Injectable({
  providedIn: 'root',
})
export class KioskApi {
  private readonly http = inject(HttpClient);

  pinLogin(pin: string) {
    return this.http.post<KioskEmployeeSession>('/api/kiosk/pin-login', { pin });
  }

  clock(employeeId: string, pin: string, action: ClockAction) {
    return this.http.post<ClockStatus>('/api/kiosk/clock', { employeeId, pin, action });
  }

  latestNfcEvent(terminalId = 'default') {
    return this.http.get<NfcLatestEvent>('/api/nfc/events/latest', { params: { terminalId } });
  }
}
