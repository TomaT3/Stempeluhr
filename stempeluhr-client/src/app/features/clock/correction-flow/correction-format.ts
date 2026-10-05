import { CorrectionKind, CorrectionStatus, LocalDateTime } from '../../../core/models/kiosk.models';
import { formatRange } from '../../../shared/components/time-stepper/local-time';

/** Ein Eintrag in „Vorher“ oder „Nachher“, z. B. „Pause Mo 05.10. 12:00–12:30“. */
export interface CorrectionLine {
  label: string;
  range: string;
}

/**
 * Was ein Antrag ändert, in der Form, in der Zusammenfassung und
 * „Meine Anträge“ ihn zeigen. Fehlende Zeiten (`null`) bleiben beim Original.
 */
export interface CorrectionChange {
  kind: CorrectionKind;
  /** Der betroffene Eintrag vor dem Antrag; null bei `addShift`. */
  original: { label: string; begin: LocalDateTime; end: LocalDateTime | null } | null;
  begin: LocalDateTime | null;
  end: LocalDateTime | null;
  pauseBegin: LocalDateTime | null;
  pauseEnd: LocalDateTime | null;
  /** Bezeichnung der Tätigkeit einer nachgetragenen Schicht. */
  taskLabel: string | null;
}

/** Dieselben Bezeichnungen wie auf der Admin-Seite, nur kurz für den Kiosk. */
export const KIND_LABELS: Record<CorrectionKind, string> = {
  addPause: 'Pause nachtragen',
  setEnd: 'Ausstempeln nachtragen',
  addShift: 'Schicht nachtragen',
  changeTimes: 'Zeiten ändern',
};

/** Status aus Sicht des Mitarbeiters (die Admin-Seite sagt „Manuell erledigt“). */
export const STATUS_LABELS: Record<CorrectionStatus, string> = {
  pending: 'Wartet auf Freigabe',
  applied: 'Eingetragen',
  rejected: 'Abgelehnt',
  failed: 'Fehlgeschlagen',
  withdrawn: 'Zurückgezogen',
  resolvedManually: 'Vom Chef eingetragen',
};

export function beforeLines(change: CorrectionChange): CorrectionLine[] {
  const original = change.original;
  return original ? [{ label: original.label, range: formatRange(original.begin, original.end) }] : [];
}

export function afterLines(change: CorrectionChange): CorrectionLine[] {
  const original = change.original;
  switch (change.kind) {
    case 'addPause':
      return original && change.pauseBegin && change.pauseEnd
        ? split(original.label, original.begin, original.end, change.pauseBegin, change.pauseEnd)
        : [];
    case 'setEnd':
      return original ? [{ label: original.label, range: formatRange(original.begin, change.end ?? original.end) }] : [];
    case 'changeTimes':
      return original
        ? [{ label: original.label, range: formatRange(change.begin ?? original.begin, change.end ?? original.end) }]
        : [];
    case 'addShift': {
      if (!change.begin) {
        return [];
      }
      const label = change.taskLabel ?? 'Arbeit';
      return change.pauseBegin && change.pauseEnd
        ? split(label, change.begin, change.end, change.pauseBegin, change.pauseEnd)
        : [{ label, range: formatRange(change.begin, change.end) }];
    }
  }
}

function split(label: string, begin: string, end: string | null, pauseBegin: string, pauseEnd: string): CorrectionLine[] {
  const lines = [
    { label, range: formatRange(begin, pauseBegin) },
    { label: 'Pause', range: formatRange(pauseBegin, pauseEnd) },
  ];
  // Endet die Pause am alten Ende, entfällt die Rest-Arbeit.
  if (end !== pauseEnd) {
    lines.push({ label, range: formatRange(pauseEnd, end) });
  }
  return lines;
}
