import { ClockStatus, Employee } from '../models/kiosk.models';

/**
 * Local identities and last known/projected status. Terminal catalogs replace
 * the card and salted SHA-256(salt:pin) caches as a complete snapshot; ordinary
 * /clock browsers learn identities on successful online login. Short PINs are
 * brute-forceable offline even with salt: protect the kiosk and browser profile.
 * Terminal replay trusts the terminal identity and rechecks active employees;
 * legacy replay still validates the supplied employee PIN/card.
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

/**
 * Remembers a card -> employee pair seen while ONLINE for later offline use.
 * The whole entry counts, not just the name: tasks and the default task label
 * change in the admin area, and a cached card must not keep offering a
 * deleted task (or hide a new one) forever.
 */
export function rememberEmployeeCard(cardId: string | null | undefined, employee: Employee): void {
  const normalized = normalizeCardId(cardId);
  if (!normalized) {
    return;
  }

  const cache = readEmployeeCardCache();
  if (JSON.stringify(cache[normalized]) === JSON.stringify(employee)) {
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
  const cached = readPinCache();
  const entries = cached.filter(entry => entry.employee.id !== employee.id);
  entries.push({ salt, verifier, employee });
  writeJson(PIN_CACHE_KEY, entries.slice(-Math.max(MAX_PIN_ENTRIES, cached.length)));
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

/** Promote only the state the replay response actually confirmed. */
export function confirmSyncedStatus(employeeId: string, state: string): void {
  if (state !== 'clockedOut' && state !== 'working' && state !== 'paused') {
    return;
  }
  const entry = lastKnownStatus(employeeId);
  if (!entry || entry.origin !== 'projected') {
    return;
  }
  const stateText = state === 'clockedOut' ? 'Ausgestempelt' : state === 'paused' ? 'Pause' : 'Eingestempelt';
  rememberObservedStatus(employeeId, {
    isRunning: state !== 'clockedOut',
    activeTimesheetId: null,
    startedAt: null,
    durationSeconds: 0,
    state,
    stateText,
  });
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
 * mistakes an estimate or a queued action for a confirmed booking.
 *
 * The label describes the ORIGIN of the value, never the connection state:
 * whether the kiosk is online is the banner's business. A status the kiosk
 * only remembers may be shown while the server's own answer is still on its
 * way (or never arrives on a hung connection), and "offline" would then be a
 * claim about the network that nobody verified.
 */
export function withOfflineLabel(
  status: ClockStatus,
  origin: OfflineStatusOrigin,
  observedAt: string,
): ClockStatus {
  const label = origin === 'projected'
    ? 'offline vorgemerkt'
    : `zuletzt gesehen ${formatShortTime(observedAt)}`;

  return { ...status, stateText: `${status.stateText} (${label})` };
}

/** Builds the offline status the kiosk displays from a cache entry. */
export function toOfflineStatus(entry: OfflineStatusEntry): ClockStatus {
  return withOfflineLabel(entry.status, entry.origin, entry.observedAt);
}

/** Server-generated verifiers use the same SHA-256(salt:pin) format. */
export interface CatalogEntry {
  employee: Employee;
  cardId: string | null;
  salt: string;
  verifier: string | null;
}

/** Replace the complete identity snapshot, removing revoked cards and stale PINs. */
export function replaceEmployeeCatalog(entries: CatalogEntry[]): void {
  if (!Array.isArray(entries) || !entries.every(entry => isEmployee(entry?.employee)
    && typeof entry.salt === 'string' && (entry.verifier === null || typeof entry.verifier === 'string')
    && (entry.cardId === null || typeof entry.cardId === 'string'))) return;
  const cards: Record<string, Employee> = {};
  const ambiguous = new Set<string>();
  for (const entry of entries) {
    const card = normalizeCardId(entry.cardId);
    if (!card) continue;
    if (cards[card]) ambiguous.add(card);
    cards[card] = entry.employee;
  }
  for (const card of ambiguous) delete cards[card];
  writeJson(CARD_CACHE_KEY, cards);
  writeJson(PIN_CACHE_KEY, entries.filter(entry => entry.verifier !== null)
    .map(({ employee, salt, verifier }) => ({ employee, salt, verifier })));
}
