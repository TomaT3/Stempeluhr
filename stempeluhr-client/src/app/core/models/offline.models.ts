export interface OfflineNfcClockEvent {
  eventId: string;
  cardId: string;
  terminalId?: string | null;
  scannedAt: string;
}

export interface OfflineKioskClockEvent {
  eventId: string;
  employeeId: string;
  pin?: string | null;
  action: 'start' | 'stop' | 'pauseStart' | 'pauseEnd';
  performedAt: string;
  /**
   * Card id of the NFC session that unlocked the terminal: the live path
   * authenticates card touches WITHOUT a pin, so the replay must mirror
   * that lookup - otherwise a queued stamp from an NFC session without pin
   * entry would be permanently rejected ("pin wrong"). Absent for sessions
   * opened by PIN login.
   */
  nfcCardId?: string | null;
}

export interface OfflineSyncEventResult {
  eventId: string;
  /** 'applied' | 'duplicate' | 'buffered' | 'rejected' */
  status: string;
  message?: string | null;
  state?: string | null;
}

export interface OfflineSyncResult {
  accepted: number;
  duplicates: number;
  buffered: number;
  results: OfflineSyncEventResult[];
}
