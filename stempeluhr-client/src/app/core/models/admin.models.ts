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
  pcscdRssKb: number | null;
  pcscdAnonymousKb: number | null;
  pcscdSwapKb: number | null;
  pcscdAnonymousAndSwapKb: number | null;
  pcscdPid: number | null;
  pcscdStartTicks: number | null;
  pcscdVersion: string | null;
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
  /** Chat für Korrekturanträge mit Genehmigen/Ablehnen-Knöpfen; leer = nur die Admin-Seite. */
  telegramCorrectionChatId?: string | null;
  /** Telegram-User-IDs, die Korrekturanträge entscheiden dürfen; leer = jedes Mitglied des Chats. */
  telegramApproverUserIds?: number[];
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

export type TimeCorrectionKind = 'addPause' | 'setEnd' | 'addShift' | 'changeTimes';

export type TimeCorrectionStatus = 'pending' | 'applied' | 'rejected' | 'failed' | 'withdrawn' | 'resolvedManually';

/**
 * Korrekturantrag. Zeiten sind lokale Zeit (`yyyy-MM-ddTHH:mm`) in der
 * Kimai-Zeitzone des Mitarbeiters (`timeZone`), `createdAt`/`decidedAt`
 * dagegen absolute Zeitpunkte.
 */
export interface AdminTimeCorrection {
  id: string;
  employeeId: string;
  employeeName: string;
  kind: TimeCorrectionKind;
  status: TimeCorrectionStatus;
  /** Terminal-ID oder `clock`. */
  source: string;
  createdAt: string;
  comment: string | null;
  timeZone: string;
  timesheetId: number | null;
  begin: string | null;
  end: string | null;
  pauseBegin: string | null;
  pauseEnd: string | null;
  taskId: string | null;
  taskLabel: string | null;
  /**
   * Timesheet zum Zeitpunkt des Antrags (fehlt bei `addShift`). `setEnd` und
   * `changeTimes` gibt es auch für Pausen-Einträge.
   */
  original: {
    begin: string;
    end: string | null;
    description: string | null;
    kind: 'work' | 'pause';
    /** „Pause“, „Arbeit“ oder die Bezeichnung der Tätigkeit. */
    label: string;
  } | null;
  decidedAt: string | null;
  decidedBy: string | null;
  decisionNote: string | null;
  /** Kimai-Meldung eines gescheiterten Antrags. */
  error: string | null;
  appliedSteps: string[];
}
