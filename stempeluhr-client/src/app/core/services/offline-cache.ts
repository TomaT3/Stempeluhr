import { ClockStatus, Employee } from '../models/kiosk.models';

/**
 * Local knowledge the kiosk needs while the backend is unreachable.
 *
 * Three independent caches live in localStorage:
 *
 * 1. CARD CACHE - card id -> employee. Filled from ONLINE identifications and
 *    used to unlock an employee from a LOCAL agent scan while offline. Known
 *    limitation: entries are only overwritten by NEW online events, so a card
 *    revoked on the server may still unlock its former employee offline. The
 *    offline path only IDENTIFIES (no stamping) and every queued event is
 *    re-validated server-side during replay.
 * 2. PIN CACHE - salted verifier of a PIN that logged in ONLINE successfully
 *    -> employee. Lets the kiosk keep accepting PIN logins while offline.
 *    The verifier is a SHA-256 over a random per-device salt plus the PIN; a
 *    4-digit PIN space is brute-forceable by anyone holding the device, so the
 *    ONLY purpose is to avoid storing PINs in cleartext (the offline queue
 *    stores the PIN of a queued stamp anyway - tracked as issue #7). The
 *    server re-validates every replayed event, so a stale verifier can unlock
 *    the UI but never books time.
 * 3. STATUS CACHE - employee id -> last known ClockStatus plus how it was
 *    obtained ('observed' while ONLINE, 'projected' from a locally queued
 *    offline action). Offline the kiosk can therefore offer the plausible
 *    action instead of claiming "Nicht eingestempelt".
 */
const CARD_CACHE_KEY = 'stempeluhr.employee-card-cache.v1';
const PIN_CACHE_KEY = 'stempeluhr.employee-pin-cache.v1';
const STATUS_CACHE_KEY = 'stempeluhr.employee-status-cache.v1';

/** Keeps the caches bounded on a device that runs for months. */
const MAX_PIN_ENTRIES = 50;
const MAX_STATUS_ENTRIES = 100;

/** How a cached status was obtained - decides the label shown at the kiosk. */
export type OfflineStatusOrigin = 'observed' | 'projected';

export interface OfflineStatusEntry {
  status: ClockStatus;
  /** When the status was recorded (client clock, ISO-8601). */
  observedAt: string;
  origin: OfflineStatusOrigin;
}

interface PinCacheEntry {
  salt: string;
  verifier: string;
  employee: Employee;
}

/** Normalizes card ids the same way the admin page does (hex, uppercase). */
export function normalizeCardId(cardId: string | null | undefined): string | null {
  const normalized = cardId?.replace(/[^0-9a-f]/gi, '').toUpperCase() ?? '';
  return normalized.length > 0 ? normalized : null;
}

function readJson<T>(key: string, fallback: T): T {
  try {
    const raw = window.localStorage.getItem(key);
    return raw ? (JSON.parse(raw) as T) : fallback;
  } catch {
    return fallback;
  }
}

function writeJson(key: string, value: unknown): void {
  try {
    window.localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // Storage full/blocked: the cache then simply stays as it was - offline
    // identification/login degrades to what is still readable.
  }
}

function isEmployee(value: unknown): value is Employee {
  return typeof (value as Employee | null)?.id === 'string';
}

/* ------------------------------------------------------------------ cards */

export function readEmployeeCardCache(): Record<string, Employee> {
  const raw = readJson<Record<string, Employee>>(CARD_CACHE_KEY, {});
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) {
    return {};
  }

  return Object.fromEntries(Object.entries(raw).filter(([, employee]) => isEmployee(employee)));
}

export function resolveEmployeeByCard(cardId: string | null | undefined): Employee | null {
  const normalized = normalizeCardId(cardId);
  return normalized ? readEmployeeCardCache()[normalized] ?? null : null;
}

/** Remembers a card -> employee pair seen while ONLINE for later offline use. */
export function rememberEmployeeCard(cardId: string | null | undefined, employee: Employee): void {
  const normalized = normalizeCardId(cardId);
  if (!normalized) {
    return;
  }

  const cache = readEmployeeCardCache();
  const existing = cache[normalized];
  if (
    existing?.id === employee.id
    && existing.displayName === employee.displayName
    && existing.initials === employee.initials
  ) {
    return;
  }

  cache[normalized] = employee;
  writeJson(CARD_CACHE_KEY, cache);
}

/* --------------------------------------------------------------- pin login */

function toHex(bytes: Uint8Array): string {
  return [...bytes].map(byte => byte.toString(16).padStart(2, '0')).join('');
}

/**
 * SHA-256 of `salt:pin`. Returns null when the platform has no WebCrypto
 * (crypto.subtle needs a secure context - the kiosk is served over HTTPS);
 * in that case offline PIN login is simply unavailable and the kiosk keeps
 * telling the user that the PIN cannot be checked.
 */
async function computeVerifier(pin: string, salt: string): Promise<string | null> {
  const subtle = globalThis.crypto?.subtle;
  if (!subtle) {
    return null;
  }

  try {
    const digest = await subtle.digest('SHA-256', new TextEncoder().encode(`${salt}:${pin}`));
    return toHex(new Uint8Array(digest));
  } catch {
    return null;
  }
}

function createSalt(): string {
  const bytes = new Uint8Array(16);
  try {
    globalThis.crypto?.getRandomValues?.(bytes);
  } catch {
    // Falls back to the (still per-entry unique) zero salt - the verifier
    // stays a hash and never exposes the PIN itself.
  }

  return toHex(bytes);
}

function readPinCache(): PinCacheEntry[] {
  const raw = readJson<PinCacheEntry[]>(PIN_CACHE_KEY, []);
  if (!Array.isArray(raw)) {
    return [];
  }

  return raw.filter(entry =>
    typeof entry?.salt === 'string'
    && typeof entry?.verifier === 'string'
    && isEmployee(entry?.employee),
  );
}

/**
 * Resolves a PIN entered while OFFLINE against the locally cached verifiers.
 *
 * Mirrors `EmployeeService.FindEmployeeByPin`: a PIN that matches TWO
 * employees is ambiguous and resolves to nobody. Returning the first match
 * would unlock a foreign name whose queued stamps the replay rejects.
 */
export async function resolveEmployeeByPin(pin: string): Promise<Employee | null> {
  if (!pin) {
    return null;
  }

  let match: Employee | null = null;
  for (const entry of readPinCache()) {
    const verifier = await computeVerifier(pin, entry.salt);
    if (verifier === null || verifier !== entry.verifier) {
      continue;
    }

    if (match && match.id !== entry.employee.id) {
      return null; // ambiguous PIN - the server rejects this too
    }

    match = entry.employee;
  }

  return match;
}

/** Caches the verifier of a PIN that logged in ONLINE successfully. */
export async function rememberEmployeePin(pin: string, employee: Employee): Promise<void> {
  if (!pin) {
    return;
  }

  const salt = createSalt();
  const verifier = await computeVerifier(pin, salt);
  if (verifier === null) {
    return;
  }

  // A successful ONLINE login is authoritative: the server resolves a PIN to
  // exactly one employee (an ambiguous PIN is rejected with 401), so every
  // cached entry carrying THIS PIN belongs to a former owner - the PIN was
  // handed to somebody else, or an older build left duplicates behind.
  // Refusing to store would leave the stale entry in place and the kiosk
  // would keep unlocking the wrong name; dropping it makes the last
  // server-confirmed owner win and heals those states.
  await forgetEmployeePin(pin);

  // The employee may also have an entry for a PREVIOUS pin - that one can
  // only be stale too (the server just accepted this one), so it goes as well.
  const entries = readPinCache().filter(entry => entry.employee.id !== employee.id);
  entries.push({ salt, verifier, employee });
  writeJson(PIN_CACHE_KEY, entries.slice(-MAX_PIN_ENTRIES));
}

/**
 * Drops the cached verifier of a PIN the SERVER just rejected. Without this a
 * rotated PIN would keep unlocking the kiosk offline while every queued stamp
 * is rejected during replay.
 */
export async function forgetEmployeePin(pin: string): Promise<void> {
  const entries = readPinCache();
  if (entries.length === 0 || !pin) {
    return;
  }

  const kept: PinCacheEntry[] = [];
  for (const entry of entries) {
    const verifier = await computeVerifier(pin, entry.salt);
    if (verifier === null || verifier !== entry.verifier) {
      kept.push(entry);
    }
  }

  writeJson(PIN_CACHE_KEY, kept);
}

/* ------------------------------------------------------------ clock status */

function readStatusCache(): Record<string, OfflineStatusEntry> {
  const raw = readJson<Record<string, OfflineStatusEntry>>(STATUS_CACHE_KEY, {});
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) {
    return {};
  }

  return Object.fromEntries(Object.entries(raw).filter(([, entry]) =>
    typeof entry?.status?.state === 'string' && typeof entry.observedAt === 'string',
  ));
}

/** Remembers what the SERVER said about an employee (overwrites a projection). */
export function rememberObservedStatus(employeeId: string, status: ClockStatus): void {
  rememberStatus(employeeId, status, 'observed');
}

/** Remembers the state a locally queued offline action leads to. */
export function rememberProjectedStatus(employeeId: string, status: ClockStatus): void {
  rememberStatus(employeeId, status, 'projected');
}

function rememberStatus(employeeId: string, status: ClockStatus, origin: OfflineStatusOrigin): void {
  if (!employeeId) {
    return;
  }

  const cache = readStatusCache();
  cache[employeeId] = { status, observedAt: new Date().toISOString(), origin };
  // Trim by AGE, not by key order: JS objects iterate integer-like keys first,
  // so neither the insertion order nor a delete/re-set trick decides which
  // entry is oldest once employee ids look like numbers.
  const trimmed = Object.entries(cache)
    .sort(([, left], [, right]) => left.observedAt.localeCompare(right.observedAt))
    .slice(-MAX_STATUS_ENTRIES);
  writeJson(STATUS_CACHE_KEY, Object.fromEntries(trimmed));
}

export function lastKnownStatus(employeeId: string | null | undefined): OfflineStatusEntry | null {
  return employeeId ? readStatusCache()[employeeId] ?? null : null;
}

/** Local time (HH:mm) of an ISO timestamp, without locale dependencies. */
export function formatShortTime(isoTimestamp: string): string {
  const date = new Date(isoTimestamp);
  if (Number.isNaN(date.getTime())) {
    return '--:--';
  }

  return `${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}`;
}

/**
 * Adds the label that says where a locally known status came from, so nobody
 * mistakes an estimate or a queued action for a completed booking.
 */
export function withOfflineLabel(
  status: ClockStatus,
  origin: OfflineStatusOrigin,
  observedAt: string,
): ClockStatus {
  const label = origin === 'projected'
    ? 'offline vorgemerkt'
    : `offline, Stand ${formatShortTime(observedAt)}`;

  return { ...status, stateText: `${status.stateText} (${label})` };
}

/** Builds the offline status the kiosk displays from a cache entry. */
export function toOfflineStatus(entry: OfflineStatusEntry): ClockStatus {
  return withOfflineLabel(entry.status, entry.origin, entry.observedAt);
}
