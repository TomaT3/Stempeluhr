import { CorrectionAuth } from './kiosk.models';

// ---- Abwesenheiten (Epic #132): gemeinsam für Admin-Seite und Kiosk ----

export type AbsenceKind = 'vacation' | 'sick' | 'flexDay';

export type AbsenceStatus = 'pending' | 'applied' | 'rejected' | 'failed' | 'withdrawn' | 'resolvedManually' | 'cancelled';

/** Urlaubskonto eines Jahres. `remaining` = Anspruch + Übertrag + Korrektur − genommen − beantragt. */
export interface VacationBalance {
  year: number;
  entitlement: number;
  carryOver: number;
  correction: number;
  taken: number;
  planned: number;
  remaining: number;
}

/**
 * Abwesenheitsantrag; Tage als `yyyy-MM-dd` (Kimai-Zeitzone des
 * Mitarbeiters). Am Kiosk ist `error` bei `failed` ein neutraler Hinweis.
 */
export interface Absence {
  id: string;
  employeeId: string;
  employeeName: string;
  kind: AbsenceKind;
  status: AbsenceStatus;
  days: string[];
  /** Terminal-ID, `clock` oder `admin`. */
  source: string;
  createdAt: string;
  createdBy: string;
  comment: string | null;
  decidedAt: string | null;
  decidedBy: string | null;
  decisionNote: string | null;
  /** Kimai-Meldung (failed) oder ein unvollständiges Storno (applied). */
  error: string | null;
  /** Tage, die schon in Kimai stehen. */
  appliedDays: string[];
  cancelledAt: string | null;
  cancelledBy: string | null;
  /** Pending mit Zeitpunkt: das Eintragen wurde unterbrochen, erneut genehmigen setzt fort. */
  applyStartedAt: string | null;
  /** Nur nach der Admin-Erfassung, z. B. Urlaub über dem Rest. */
  warning?: string | null;
}

/** Zeitfenster einer Art am Kiosk; `missing` nennt fehlende Einstellungen. */
export interface AbsenceKindWindow {
  kind: AbsenceKind;
  earliest: string;
  latest: string;
  missing: string | null;
}

/** Ein belegter Tag (offen oder eingetragen) für den Kalender. */
export interface AbsenceDay {
  day: string;
  kind: AbsenceKind;
  status: AbsenceStatus;
}

/** POST /api/kiosk/absences/balance: Konten ab dem laufenden Jahr, Fenster je Art, belegte Tage. */
export interface KioskAbsenceOverview {
  /** Heute in der Kimai-Zeitzone des Mitarbeiters. */
  today: string;
  balances: VacationBalance[];
  kinds: AbsenceKindWindow[];
  days: AbsenceDay[];
}

export interface SubmitAbsence extends CorrectionAuth {
  kind: AbsenceKind;
  days: string[];
  /** Nur auf /clock, höchstens 300 Zeichen. */
  comment: string | null;
  /** Terminal-ID oder `clock`. */
  source: string;
}
