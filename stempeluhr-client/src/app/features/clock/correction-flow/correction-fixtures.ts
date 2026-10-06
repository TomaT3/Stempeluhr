import { CorrectionTimesheets, KioskCorrection } from '../../../core/models/kiosk.models';

/**
 * Testdaten für die Korrekturanträge der Specs. „Jetzt“ ist dort
 * 2026-10-05T14:00Z, in Europe/Berlin (Sommerzeit) also 16:00 Uhr.
 */
export const CORRECTION_NOW = new Date('2026-10-05T14:00:00Z');

/** Schichten, neueste zuerst; die Nachtschicht läuft über Mitternacht. */
export const CORRECTION_TIMESHEETS: CorrectionTimesheets = {
  timeZone: 'Europe/Berlin',
  shifts: [
    {
      begin: '2026-10-05T07:00',
      end: null,
      entries: [
        { id: 21, begin: '2026-10-05T07:00', end: '2026-10-05T11:00', kind: 'work', label: 'Arbeit', hasOpenRequest: false },
        { id: 22, begin: '2026-10-05T11:00', end: '2026-10-05T11:30', kind: 'pause', label: 'Pause', hasOpenRequest: false },
        { id: 23, begin: '2026-10-05T11:30', end: null, kind: 'work', label: 'Arbeit', hasOpenRequest: false },
      ],
    },
    {
      begin: '2026-10-03T22:00',
      end: '2026-10-04T06:00',
      entries: [
        { id: 12, begin: '2026-10-03T22:00', end: '2026-10-04T02:00', kind: 'work', label: 'Nachtdienst', hasOpenRequest: false },
        { id: 13, begin: '2026-10-04T02:00', end: '2026-10-04T06:00', kind: 'work', label: 'Nachtdienst', hasOpenRequest: true },
      ],
    },
    {
      begin: '2026-09-30T08:00',
      end: '2026-09-30T16:30',
      entries: [
        { id: 7, begin: '2026-09-30T08:00', end: '2026-09-30T16:30', kind: 'work', label: 'Kunde X', hasOpenRequest: false },
      ],
    },
  ],
};

export function correction(overrides: Partial<KioskCorrection> = {}): KioskCorrection {
  return {
    id: 'c1',
    kind: 'setEnd',
    status: 'pending',
    createdAt: '2026-10-05T13:00:00Z',
    comment: null,
    timeZone: 'Europe/Berlin',
    timesheetId: 21,
    begin: null,
    end: '2026-10-05T10:00',
    pauseBegin: null,
    pauseEnd: null,
    taskLabel: null,
    original: { begin: '2026-10-05T07:00', end: '2026-10-05T11:00', kind: 'work', label: 'Arbeit' },
    decidedAt: null,
    decisionNote: null,
    error: null,
    observedAtApply: false,
    observedEndAtApply: null,
    ...overrides,
  };
}
