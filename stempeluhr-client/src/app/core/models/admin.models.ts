export type AdminTerminalState = 'online' | 'problem' | 'unreachable' | 'never';

/** Letzter Diagnosebericht eines Terminals (nur technische Felder). */
export interface AdminTerminalReport {
  agentVersion: string | null;
  appVersion: string | null;
  uiStatus: 'alive' | 'missing' | 'not-seen' | null;
  heartbeatAgeSeconds: number | null;
  screen: 'idle' | 'session' | null;
  blocked: 'none' | 'request' | 'backlog' | 'status' | null;
  busy: boolean | null;
  offline: boolean | null;
  pending: number | null;
  rejected: number | null;
  cpuPercent: number | null;
  availableMemoryKb: number | null;
  chromiumRssSumKb: number | null;
  temperatureC: number | null;
  throttledFlags: number | null;
  diskFreeMb: number | null;
  uptimeSeconds: number | null;
  load1: number | null;
}

export interface AdminTerminalStatus {
  terminalId: string;
  state: AdminTerminalState;
  lastReportAt: string | null;
  problems: { kind: string; since: string; text: string }[];
  report: AdminTerminalReport | null;
  powerFlags: string[];
}

export interface AdminEmployeeStatus {
  employeeId: string;
  displayName: string;
  isRunning: boolean;
  startedAt: string | null;
  durationSeconds: number;
  state: 'clockedOut' | 'working' | 'paused';
  stateText: string;
  isAvailable: boolean;
  /** Laufende weitere Tätigkeit (null = Standard-Tätigkeit, Pause oder aus). */
  activeTaskLabel?: string | null;
}

export interface AdminSettings {
  baseUrl: string;
  hasAdminPassword: boolean;
  hasAdminApiToken: boolean;
  defaultProjectId: number | null;
  defaultActivityId: number | null;
  pauseActivityId: number | null;
  employees: AdminEmployee[];
}

export interface AdminEmployee {
  id: string;
  kimaiUserId: number | null;
  displayName: string;
  pin: string | null;
  nfcCardId: string | null;
  hasApiToken: boolean;
  apiToken?: string;
  projectId: number | null;
  activityId: number | null;
  color: string;
  imageUrl: string | null;
  description: string | null;
  tags: string[];
  billable: boolean;
  isEnabled: boolean;
  /** Weitere Tätigkeiten (andere Kunden), am Kiosk ohne Ausstempeln wählbar. */
  tasks: AdminEmployeeTask[];
  /** Anzeigename der Haupttätigkeit am Kiosk; leer = „Standard-Tätigkeit“. */
  defaultTaskLabel: string | null;
}

export interface AdminEmployeeTask {
  id: string;
  label: string;
  projectId: number | null;
  activityId: number | null;
  billable: boolean;
}

export interface KimaiActivity {
  id: number;
  name: string;
  parentTitle: string | null;
  projectId: number | null;
  visible: boolean;
}

export interface KimaiProject {
  id: number;
  name: string;
  parentTitle: string | null;
  customerId: number | null;
  visible: boolean;
}

export interface KimaiUser {
  id: number;
  username: string | null;
  email: string | null;
  displayName: string;
  avatarUrl: string | null;
}

export interface AdminRejectedOfflineEvent {
  eventId: string;
  employeeId: string;
  employeeName: string;
  action: string;
  performedAt: string;
  rejectedAt: string;
  message: string;
  resolvedAt: string | null;
}
