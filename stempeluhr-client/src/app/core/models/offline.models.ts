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
  /**
   * Display name of the employee, ONLY so a refused stamp can name the person
   * who has to be repaired in Kimai. The API ignores the extra field; it is
   * not part of the replay contract.
   */
  employeeName?: string | null;
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

/**
 * A queued stamp the server REFUSED during replay (wrong PIN, unknown
 * employee, revoked card). It is dropped from the queue, so the time is
 * missing and somebody has to repair it in Kimai by hand - the kiosk keeps
 * these records to say so out loud instead of losing them silently.
 */
export interface RejectedOfflineStamp {
  eventId: string;
  employeeId: string;
  /** Display name, so the notice can name the person to be repaired. */
  employeeName: string;
  /** When the employee actually pressed the button (ISO-8601). */
  performedAt: string;
  /** When the replay refused it (client clock, ISO-8601). */
  rejectedAt: string;
  /** Reason the server reported, shown to whoever has to fix it. */
  message: string;
  /** Only kiosk events carry an action; reader-token events do not. */
  action: 'start' | 'stop' | 'pauseStart' | 'pauseEnd' | null;
  /**
   * Set when somebody pressed "Alle erledigt". Acknowledged records are only
   * hidden from the kiosk notice - they stay in storage, because the time may
   * still be missing in Kimai and this record is the only trace left.
   */
  acknowledgedAt?: string | null;
}
