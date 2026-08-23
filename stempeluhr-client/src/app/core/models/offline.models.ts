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
