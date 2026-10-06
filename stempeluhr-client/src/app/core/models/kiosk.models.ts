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
  /** Laufende weitere Tätigkeit; null/fehlend = Standard-Tätigkeit, fremde Buchung (oder Pause/aus). */
  activeTaskId?: string | null;
  activeTaskLabel?: string | null;
  /**
   * Arbeit läuft auf der Standard-Tätigkeit. false bei einer Buchung, die
   * keiner Tätigkeit entspricht (gelöschte Tätigkeit, Kimai-Oberfläche)
   * oder deren Tätigkeit offline unbekannt ist. Fehlt bei älteren Antworten
   * und Cache-Einträgen - dann gilt `!activeTaskId` (siehe isOnDefaultTask).
   */
  activeIsDefaultTask?: boolean | null;
  /**
   * Die Aktion ist nicht wie gewählt gelungen: was stattdessen gebucht ist
   * (z. B. ein von Kimai abgelehnter Wechsel). Ersetzt `stateText` als Meldung.
   */
  warning?: string | null;
}

/** Läuft die Arbeit auf der Standard-Tätigkeit? Ältere Status ohne Flag: kein activeTaskId. */
export function isOnDefaultTask(status: ClockStatus | null | undefined): boolean {
  return status?.activeIsDefaultTask ?? !status?.activeTaskId;
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

// ---- Korrekturanträge (POST /api/kiosk/corrections*) ----

/**
 * Zeiten der Korrekturanträge sind lokale Zeit `yyyy-MM-ddTHH:mm` in der
 * Kimai-Zeitzone des Mitarbeiters, nie UTC und ohne Offset.
 */
export type LocalDateTime = string;

/** Anmeldung jeder Korrektur-Anfrage: Mitarbeiter plus PIN **oder** Karte (`pin` leer). */
export interface CorrectionAuth {
  employeeId: string;
  pin: string;
  nfcCardId: string | null;
}

export interface CorrectionEntry {
  id: number;
  begin: LocalDateTime;
  /** null = läuft noch. */
  end: LocalDateTime | null;
  kind: 'work' | 'pause';
  /** „Arbeit“, „Pause“ oder die Bezeichnung der Tätigkeit. */
  label: string;
  hasOpenRequest: boolean;
}

/** Eine Schicht (eine Nachtschicht bleibt über Mitternacht eine Schicht) mit ihren Einträgen. */
export interface CorrectionShift {
  begin: LocalDateTime;
  end: LocalDateTime | null;
  entries: CorrectionEntry[];
}

/** Auswahlliste der letzten 31 Tage, Schichten neueste zuerst. */
export interface CorrectionTimesheets {
  /** IANA-Zeitzone, in der alle Zeiten stehen. */
  timeZone: string;
  shifts: CorrectionShift[];
}

export type CorrectionKind = 'addPause' | 'setEnd' | 'addShift' | 'changeTimes';

export type CorrectionStatus = 'pending' | 'applied' | 'rejected' | 'failed' | 'withdrawn' | 'resolvedManually';

export interface SubmitCorrection extends CorrectionAuth {
  kind: CorrectionKind;
  /** Nur bei Arten mit vorhandenem Eintrag (nicht `addShift`). */
  timesheetId: number | null;
  begin: LocalDateTime | null;
  end: LocalDateTime | null;
  pauseBegin: LocalDateTime | null;
  pauseEnd: LocalDateTime | null;
  /** Nur `addShift`: weitere Tätigkeit, null = Haupttätigkeit. */
  taskId: string | null;
  /** Nur auf /clock, höchstens 300 Zeichen. */
  comment: string | null;
  /** Terminal-ID oder `clock`. */
  source: string;
}

/** Ein Antrag, wie ihn der Mitarbeiter sieht (bei `failed` ist `error` ein neutraler Hinweis). */
export interface KioskCorrection {
  id: string;
  kind: CorrectionKind;
  status: CorrectionStatus;
  createdAt: string;
  comment: string | null;
  timeZone: string;
  timesheetId: number | null;
  begin: LocalDateTime | null;
  end: LocalDateTime | null;
  pauseBegin: LocalDateTime | null;
  pauseEnd: LocalDateTime | null;
  taskLabel: string | null;
  /** Timesheet zum Zeitpunkt des Antrags (fehlt bei `addShift`). */
  original: {
    begin: LocalDateTime;
    end: LocalDateTime | null;
    kind: 'work' | 'pause';
    label: string;
  } | null;
  decidedAt: string | null;
  decisionNote: string | null;
  error: string | null;
  /**
   * Pause in einem laufenden Eintrag: true, sobald beim Genehmigen der Stand
   * festgehalten wurde; `observedEndAtApply` null = lief da noch.
   */
  observedAtApply: boolean;
  observedEndAtApply: LocalDateTime | null;
}
