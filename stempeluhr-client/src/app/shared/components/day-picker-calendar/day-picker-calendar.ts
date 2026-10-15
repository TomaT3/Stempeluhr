import { Component, computed, input, linkedSignal, model } from '@angular/core';

/** Ein belegter, nicht wählbarer Tag, z. B. eine schon beantragte Abwesenheit. */
export interface BlockedDay {
  /** Kurzer Text für Tooltip und Screenreader, z. B. „Urlaub (offen)“. */
  label: string;
  /** Farbvariante (`data-tone`): `vacation`, `sick` oder `flexDay`. */
  tone?: string;
}

export interface CalendarCell {
  day: string;
  number: number;
  label: string;
  selectable: boolean;
  selected: boolean;
  today: boolean;
  blocked: BlockedDay | null;
}

const MonthNames = ['Januar', 'Februar', 'März', 'April', 'Mai', 'Juni', 'Juli', 'August', 'September', 'Oktober', 'November', 'Dezember'];
const Weekdays = ['So', 'Mo', 'Di', 'Mi', 'Do', 'Fr', 'Sa'];

/** Heutiges Datum des Geräts als `yyyy-MM-dd`. */
export function localToday(now = new Date()): string {
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

/** `yyyy-MM-dd` plus Tage, ohne Zeitzonen-Effekte (UTC-Rechnung). */
export function addDays(day: string, days: number): string {
  const date = toUtc(day);
  date.setUTCDate(date.getUTCDate() + days);
  return fromUtc(date);
}

/** `yyyy-MM-dd` plus Monate; der Tag wird auf das Monatsende begrenzt. */
export function addMonths(day: string, months: number): string {
  const [year, month, date] = day.split('-').map(Number);
  const target = new Date(Date.UTC(year, month - 1 + months, 1));
  const lastDay = new Date(Date.UTC(target.getUTCFullYear(), target.getUTCMonth() + 1, 0)).getUTCDate();
  target.setUTCDate(Math.min(date, lastDay));
  return fromUtc(target);
}

/** Wochentag-Kürzel eines `yyyy-MM-dd`, z. B. „Mo“. */
export function weekday(day: string): string {
  return Weekdays[toUtc(day).getUTCDay()];
}

function toUtc(day: string): Date {
  const [year, month, date] = day.split('-').map(Number);
  return new Date(Date.UTC(year, month - 1, date));
}

function fromUtc(date: Date): string {
  return `${date.getUTCFullYear()}-${pad(date.getUTCMonth() + 1)}-${pad(date.getUTCDate())}`;
}

function pad(value: number): string {
  return String(value).padStart(2, '0');
}

/**
 * Monatskalender zum Antippen einzelner Tage (Mehrfachauswahl). Belegte Tage
 * und Tage außerhalb von `min`/`max` sind nicht wählbar. Die Woche beginnt am
 * Montag. Größe über `--day-size` steuerbar (Admin-Seite, Kiosk 800×480).
 */
@Component({
  selector: 'app-day-picker-calendar',
  templateUrl: './day-picker-calendar.html',
  styleUrl: './day-picker-calendar.scss',
})
export class DayPickerCalendar {
  /** Gewählte Tage (`yyyy-MM-dd`), sortiert. */
  readonly selected = model<string[]>([]);
  readonly blocked = input<Record<string, BlockedDay>>({});
  readonly min = input<string | null>(null);
  readonly max = input<string | null>(null);
  readonly today = input<string>(localToday());
  readonly disabled = input(false);

  /** Angezeigter Monat als `yyyy-MM`; beginnt beim Monat von `today`. */
  readonly month = linkedSignal(() => this.today().slice(0, 7));

  readonly title = computed(() => {
    const [year, month] = this.month().split('-').map(Number);
    return `${MonthNames[month - 1]} ${year}`;
  });

  readonly weekdays = ['Mo', 'Di', 'Mi', 'Do', 'Fr', 'Sa', 'So'];

  readonly canGoBack = computed(() => {
    const min = this.min();
    return !min || min.slice(0, 7) < this.month();
  });

  readonly canGoForward = computed(() => {
    const max = this.max();
    return !max || max.slice(0, 7) > this.month();
  });

  /** Zellen des Monats; `null` füllt bis zum Montag der ersten Woche auf. */
  readonly cells = computed<(CalendarCell | null)[]>(() => {
    const first = `${this.month()}-01`;
    const offset = (toUtc(first).getUTCDay() + 6) % 7;
    const length = toUtc(addMonths(first, 1)).getTime() - toUtc(first).getTime();
    const count = Math.round(length / 86_400_000);
    const selected = new Set(this.selected());
    const blocked = this.blocked();
    const min = this.min();
    const max = this.max();
    const today = this.today();

    const cells: (CalendarCell | null)[] = Array.from({ length: offset }, () => null);
    for (let index = 0; index < count; index++) {
      const day = addDays(first, index);
      const mark = blocked[day] ?? null;
      const inRange = (!min || day >= min) && (!max || day <= max);
      cells.push({
        day,
        number: index + 1,
        label: `${weekday(day)} ${day.slice(8, 10)}.${day.slice(5, 7)}.${day.slice(0, 4)}${mark ? `, ${mark.label}` : ''}`,
        selectable: inRange && !mark,
        selected: selected.has(day),
        today: day === today,
        blocked: mark,
      });
    }
    return cells;
  });

  previous(): void {
    if (this.canGoBack()) {
      this.month.set(addMonths(`${this.month()}-01`, -1).slice(0, 7));
    }
  }

  next(): void {
    if (this.canGoForward()) {
      this.month.set(addMonths(`${this.month()}-01`, 1).slice(0, 7));
    }
  }

  toggle(cell: CalendarCell): void {
    if (this.disabled() || (!cell.selectable && !cell.selected)) {
      return;
    }

    const current = this.selected();
    this.selected.set(current.includes(cell.day)
      ? current.filter(day => day !== cell.day)
      : [...current, cell.day].sort());
  }
}
