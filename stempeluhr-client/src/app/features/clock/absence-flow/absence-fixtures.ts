import { Absence, KioskAbsenceOverview } from '../../../core/models/absence.models';

/** Freitag, 16.10.2026 (Ortszeit) - „heute“ in allen Abwesenheits-Tests. */
export const ABSENCE_NOW = new Date(2026, 9, 16, 9, 0);

/**
 * Konto 2026: 30 + 2 Übertrag, 20 genommen, 1 beantragt, Rest 11. Der
 * 20.10. ist schon als Urlaub beantragt, der 05.10. war krank.
 */
export const ABSENCE_OVERVIEW: KioskAbsenceOverview = {
  today: '2026-10-16',
  balances: [
    { year: 2026, entitlement: 30, carryOver: 2, correction: 0, taken: 20, planned: 1, remaining: 11 },
    { year: 2027, entitlement: 30, carryOver: 0, correction: 0, taken: 0, planned: 0, remaining: 30 },
  ],
  kinds: [
    { kind: 'vacation', earliest: '2026-09-15', latest: '2028-04-16', missing: null },
    { kind: 'sick', earliest: '2026-09-15', latest: '2026-10-30', missing: null },
    { kind: 'flexDay', earliest: '2026-09-15', latest: '2027-10-16', missing: null },
  ],
  days: [
    { day: '2026-10-05', kind: 'sick', status: 'applied' },
    { day: '2026-10-20', kind: 'vacation', status: 'pending' },
  ],
};

export function absence(overrides: Partial<Absence> = {}): Absence {
  return {
    id: 'a1',
    employeeId: 'max',
    employeeName: 'Max Mustermann',
    kind: 'vacation',
    status: 'pending',
    days: ['2026-10-26', '2026-10-27'],
    source: 'term-1',
    createdAt: '2026-10-16T07:00:00Z',
    createdBy: 'Max Mustermann',
    comment: null,
    decidedAt: null,
    decidedBy: null,
    decisionNote: null,
    error: null,
    appliedDays: [],
    cancelledAt: null,
    cancelledBy: null,
    applyStartedAt: null,
    ...overrides,
  };
}
