export interface Employee {
  id: string;
  displayName: string;
  initials: string;
  color: string;
  imageUrl: string | null;
  requiresPin: boolean;
  /**
   * Weitere Tätigkeiten (z. B. Arbeit für andere Kunden), auf die während der
   * Arbeitszeit gewechselt werden kann. Fehlt bei Einträgen aus älteren
   * Offline-Caches - dann gibt es keinen Wechselknopf.
   */
  tasks?: EmployeeTask[];
  /** Anzeigename der Haupttätigkeit (z. B. „Büro“); leer = „Standard-Tätigkeit“. */
  defaultTaskLabel?: string | null;
}

export interface ClockStatus {
  isRunning: boolean;
  activeTimesheetId: number | null;
  startedAt: string | null;
  durationSeconds: number;
  state: 'clockedOut' | 'working' | 'paused';
  stateText: string;
  /** Laufende weitere Tätigkeit; null/fehlend = Standard-Tätigkeit (oder Pause/aus). */
  activeTaskId?: string | null;
  activeTaskLabel?: string | null;
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

export interface HoursOverview {
  todaySeconds: number;
  todayPauseSeconds: number;
  weekSeconds: number;
  monthSeconds: number;
}

export interface HealthStatus {
  ok: boolean;
  version: string | null;
  configuredEmployees: number;
  settingsConfigured: boolean;
}

export interface EmployeeTask {
  id: string;
  label: string;
}

export type ClockAction = 'start' | 'stop' | 'pauseStart' | 'pauseEnd' | 'switch';
