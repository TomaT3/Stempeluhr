export interface Employee {
  id: string;
  displayName: string;
  initials: string;
  color: string;
  imageUrl: string | null;
  requiresPin: boolean;
}

export interface ClockStatus {
  isRunning: boolean;
  activeTimesheetId: number | null;
  startedAt: string | null;
  durationSeconds: number;
  state: 'clockedOut' | 'working' | 'paused';
  stateText: string;
}

export interface KioskEmployeeSession {
  employee: Employee;
  status: ClockStatus;
}

export interface NfcClockEvent {
  eventId: string;
  occurredAt: string;
  terminalId: string;
  cardId: string | null;
  employee: Employee | null;
  status: ClockStatus | null;
  message: string;
  success: boolean;
}

export interface NfcLatestEvent {
  event: NfcClockEvent | null;
}

export type ClockAction = 'start' | 'stop' | 'pauseStart' | 'pauseEnd';
