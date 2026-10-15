import { AbsenceKind } from '../core/models/absence.models';
import { addDays, weekday } from './components/day-picker-calendar/day-picker-calendar';

export const AbsenceKindLabels: Record<AbsenceKind, string> = {
  vacation: 'Urlaub',
  sick: 'Krank',
  flexDay: 'Gleittag',
};

/**
 * Tage kompakt, z. B. „Mo 03.–Fr 07.11., Di 11.11.“. Aufeinanderfolgende
 * Tage werden zu Bereichen; ein anderes Jahr als `currentYear` steht dabei.
 */
export function compactDays(days: string[], currentYear: number): string {
  const sorted = [...days].sort();
  const ranges: [string, string][] = [];
  for (const day of sorted) {
    const last = ranges.at(-1);
    if (last && addDays(last[1], 1) === day) {
      last[1] = day;
    } else {
      ranges.push([day, day]);
    }
  }

  const format = (day: string, withMonth: boolean, withYear: boolean) =>
    `${weekday(day)} ${day.slice(8, 10)}.${withMonth ? `${day.slice(5, 7)}.` : ''}${withYear ? day.slice(0, 4) : ''}`;
  return ranges.map(([first, last]) => {
    const otherYear = (day: string) => Number(day.slice(0, 4)) !== currentYear;
    if (first === last) {
      return format(first, true, otherYear(first));
    }
    const sameYear = first.slice(0, 4) === last.slice(0, 4);
    const sameMonth = first.slice(0, 7) === last.slice(0, 7);
    return `${format(first, !sameMonth, !sameYear)}–${format(last, true, !sameYear || otherYear(last))}`;
  }).join(', ');
}

/** Tage als „1 Tag“ bzw. „2,5 Tage“. */
export function dayCount(value: number): string {
  const text = value.toLocaleString('de-DE', { maximumFractionDigits: 2 });
  return `${text} ${value === 1 ? 'Tag' : 'Tage'}`;
}

/** Zahl der Tage wie im Urlaubskonto, z. B. „22,5“ oder „−1“. */
export function dayNumber(value: number): string {
  return value.toLocaleString('de-DE', { maximumFractionDigits: 2 }).replace('-', '−');
}
