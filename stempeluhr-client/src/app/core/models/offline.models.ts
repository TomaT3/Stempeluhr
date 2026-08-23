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

export interface OfflineSyncResult {
  accepted: number;
  duplicates: number;
  buffered: number;
}
