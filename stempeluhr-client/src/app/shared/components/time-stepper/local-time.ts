/**
 * Rechnen mit lokaler Zeit `yyyy-MM-ddTHH:mm` (Wanduhr der Kimai-Zeitzone,
 * ohne Offset): so schickt und liefert die API Korrekturzeiten. Die Werte
 * werden als „UTC-Millisekunden der Wanduhr“ gerechnet, damit Schritte weder
 * von der Zeitzone des Geräts noch von der Sommerzeit des Geräts abhängen.
 */

export const MINUTE_MS = 60_000;

const LOCAL_FORMAT = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/;
const WEEKDAYS = ['So', 'Mo', 'Di', 'Mi', 'Do', 'Fr', 'Sa'];

/** Wanduhr als Millisekunden; NaN, wenn der Text kein `yyyy-MM-ddTHH:mm` ist. */
export function localToMs(value: string): number {
  const match = LOCAL_FORMAT.exec(value);
  if (!match) {
    return Number.NaN;
  }
  const [year, month, day, hour, minute] = match.slice(1).map(Number);
  return Date.UTC(year, month - 1, day, hour, minute);
}

export function msToLocal(ms: number): string {
  return new Date(ms).toISOString().slice(0, 16);
}

export function addMinutes(value: string, minutes: number): string {
  return msToLocal(localToMs(value) + minutes * MINUTE_MS);
}

/** Minuten von `from` bis `to` (negativ, wenn `to` früher liegt). */
export function minutesBetween(from: string, to: string): number {
  return Math.round((localToMs(to) - localToMs(from)) / MINUTE_MS);
}

/** Frühere der beiden Zeiten (lexikografisch, das Format ist dafür gebaut). */
export function earlier(a: string, b: string): string {
  return a <= b ? a : b;
}

export function later(a: string, b: string): string {
  return a >= b ? a : b;
}

/** Rundet auf ein Vielfaches von `step` Minuten ab (z. B. 08:07 → 08:05). */
export function floorToStep(value: string, step: number): string {
  const ms = localToMs(value);
  const stepMs = step * MINUTE_MS;
  return msToLocal(Math.floor(ms / stepMs) * stepMs);
}

/**
 * Jetzt als Wanduhr in `timeZone` (IANA). Unbekannte oder fehlende Zone: die
 * Zeit des Geräts, damit die Zukunftsgrenze nie ausfällt.
 */
export function nowInZone(timeZone: string | null | undefined, now: Date = new Date()): string {
  try {
    const parts = new Intl.DateTimeFormat('en-US', {
      timeZone: timeZone || undefined,
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      hourCycle: 'h23',
    }).formatToParts(now);
    const get = (type: string) => parts.find(part => part.type === type)?.value ?? '00';
    return `${get('year')}-${get('month')}-${get('day')}T${get('hour')}:${get('minute')}`;
  } catch {
    const pad = (n: number) => String(n).padStart(2, '0');
    return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}T${pad(now.getHours())}:${pad(now.getMinutes())}`;
  }
}

/** `Mo 05.10.` */
export function formatDay(value: string): string {
  const ms = localToMs(value);
  return `${WEEKDAYS[new Date(ms).getUTCDay()]} ${value.slice(8, 10)}.${value.slice(5, 7)}.`;
}

/** `08:30` */
export function formatTime(value: string): string {
  return value.slice(11, 16);
}

/** `Mo 05.10. 08:30` */
export function formatDayTime(value: string): string {
  return `${formatDay(value)} ${formatTime(value)}`;
}

/** `Mo 05.10. 08:00–16:30`, über Mitternacht `Mo 05.10. 22:00 – Di 06.10. 06:00`, offen `… – läuft`. */
export function formatRange(begin: string, end: string | null): string {
  if (!end) {
    return `${formatDayTime(begin)} – läuft`;
  }
  return end.slice(0, 10) === begin.slice(0, 10)
    ? `${formatDayTime(begin)}–${formatTime(end)}`
    : `${formatDayTime(begin)} – ${formatDayTime(end)}`;
}
