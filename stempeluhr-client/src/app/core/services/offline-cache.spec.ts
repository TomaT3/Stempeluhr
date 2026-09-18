import { ClockStatus, Employee } from '../models/kiosk.models';
import {
  forgetEmployeePin,
  formatShortTime,
  lastKnownStatus,
  normalizeCardId,
  readEmployeeCardCache,
  rememberEmployeeCard,
  rememberEmployeePin,
  rememberObservedStatus,
  rememberProjectedStatus,
  resolveEmployeeByCard,
  resolveEmployeeByPin,
  toOfflineStatus,
} from './offline-cache';

const CARD_CACHE_KEY = 'stempeluhr.employee-card-cache.v1';
const PIN_CACHE_KEY = 'stempeluhr.employee-pin-cache.v1';

const employee: Employee = {
  id: 'max',
  displayName: 'Max Mustermann',
  initials: 'MM',
  color: '#123456',
  imageUrl: null,
  requiresPin: true,
};

const otherEmployee: Employee = { ...employee, id: 'anna', displayName: 'Anna Beispiel', initials: 'AB' };

const idleStatus: ClockStatus = {
  isRunning: false,
  activeTimesheetId: null,
  startedAt: null,
  durationSeconds: 0,
  state: 'clockedOut',
  stateText: 'Nicht eingestempelt',
};

/**
 * Builds a cache entry the way the module does (SHA-256 over `salt:pin`), so a
 * test can plant cache states the production code deliberately never creates.
 */
async function pinEntryFor(pin: string, salt: string, owner: Employee) {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(`${salt}:${pin}`));
  const verifier = [...new Uint8Array(digest)].map(byte => byte.toString(16).padStart(2, '0')).join('');
  return { salt, verifier, employee: owner };
}

describe('offline-cache', () => {
  beforeEach(() => {
    window.localStorage.clear();
  });

  describe('card cache', () => {
    it('resolves a remembered card regardless of the id formatting', () => {
      rememberEmployeeCard('04:ab:cd', employee);

      expect(readEmployeeCardCache()['04ABCD']).toEqual(employee);
      expect(resolveEmployeeByCard('04abcd')?.id).toBe('max');
      expect(resolveEmployeeByCard('04-AB-CD')?.id).toBe('max');
    });

    it('returns null for unknown or unusable card ids', () => {
      rememberEmployeeCard('04ABCD', employee);

      expect(resolveEmployeeByCard('9999')).toBeNull();
      expect(resolveEmployeeByCard('----')).toBeNull();
      expect(resolveEmployeeByCard(null)).toBeNull();
      expect(normalizeCardId('  ')).toBeNull();
    });

    it('does not remember a card id that normalizes to nothing', () => {
      rememberEmployeeCard('----', employee);

      expect(readEmployeeCardCache()).toEqual({});
    });

    it('ignores a corrupted cache instead of throwing', () => {
      window.localStorage.setItem(CARD_CACHE_KEY, '{not json');
      expect(readEmployeeCardCache()).toEqual({});

      window.localStorage.setItem(CARD_CACHE_KEY, JSON.stringify({ '04ABCD': null }));
      expect(resolveEmployeeByCard('04ABCD')).toBeNull();
    });
  });

  describe('pin cache', () => {
    it('resolves a PIN that was used for a successful online login', async () => {
      await rememberEmployeePin('1234', employee);

      await expect(resolveEmployeeByPin('1234')).resolves.toEqual(employee);
      await expect(resolveEmployeeByPin('4321')).resolves.toBeNull();
      await expect(resolveEmployeeByPin('')).resolves.toBeNull();
    });

    it('never stores the PIN itself', async () => {
      await rememberEmployeePin('4711', employee);

      const entries = JSON.parse(window.localStorage.getItem(PIN_CACHE_KEY) ?? '[]');
      expect(entries).toHaveLength(1);
      expect(entries[0].verifier).toMatch(/^[0-9a-f]{64}$/);
      expect(entries[0].verifier).not.toContain('4711');
      expect(entries[0].salt).not.toContain('4711');
      expect(Object.keys(entries[0])).toEqual(['salt', 'verifier', 'employee']);
    });

    it('keeps one entry per employee when the PIN changes', async () => {
      await rememberEmployeePin('1234', employee);
      await rememberEmployeePin('5678', employee);

      await expect(resolveEmployeeByPin('5678')).resolves.toEqual(employee);
      await expect(resolveEmployeeByPin('1234')).resolves.toBeNull();
      expect(JSON.parse(window.localStorage.getItem(PIN_CACHE_KEY) ?? '[]')).toHaveLength(1);
    });

    it('forgets a PIN the server rejected', async () => {
      await rememberEmployeePin('1234', employee);
      await rememberEmployeePin('4321', otherEmployee);

      await forgetEmployeePin('1234');

      await expect(resolveEmployeeByPin('1234')).resolves.toBeNull();
      await expect(resolveEmployeeByPin('4321')).resolves.toEqual(otherEmployee);
    });

    it('ignores a corrupted cache instead of throwing', async () => {
      window.localStorage.setItem(PIN_CACHE_KEY, '{"not":"an array"}');
      await expect(resolveEmployeeByPin('1234')).resolves.toBeNull();
    });

    it('does not store a PIN that already identifies somebody else', async () => {
      await rememberEmployeePin('1234', employee);
      await rememberEmployeePin('1234', otherEmployee);

      expect(JSON.parse(window.localStorage.getItem(PIN_CACHE_KEY) ?? '[]')).toHaveLength(1);
      await expect(resolveEmployeeByPin('1234')).resolves.toEqual(employee);
    });

    it('treats a PIN that two employees share as ambiguous, like the server', async () => {
      // Legacy/hand-edited cache: two verifiers for the same PIN (the guard in
      // rememberEmployeePin keeps the kiosk from creating that state itself).
      window.localStorage.setItem(PIN_CACHE_KEY, JSON.stringify([
        await pinEntryFor('1234', 'aabbccddeeff00112233445566778899', employee),
        await pinEntryFor('1234', '99887766554433221100ffeeddccbbaa', otherEmployee),
      ]));

      // EmployeeService.FindEmployeeByPin resolves such a PIN to nobody; the
      // kiosk may not unlock a foreign name whose stamps the replay rejects.
      await expect(resolveEmployeeByPin('1234')).resolves.toBeNull();
    });
  });

  describe('status cache', () => {
    it('labels an observed status with the time it was seen', () => {
      rememberObservedStatus('max', { ...idleStatus, state: 'working', isRunning: true, stateText: 'Eingestempelt' });

      const entry = lastKnownStatus('max');
      expect(entry?.origin).toBe('observed');
      const labelled = toOfflineStatus(entry!);
      expect(labelled.state).toBe('working');
      expect(labelled.stateText).toContain('Eingestempelt');
      expect(labelled.stateText).toContain('offline');
      expect(labelled.stateText).toContain(`Stand ${formatShortTime(entry!.observedAt)}`);
      expect(formatShortTime(new Date(2026, 8, 18, 7, 5).toISOString())).toBe('07:05');
    });

    it('labels a projected status as queued, not as a booking', () => {
      rememberProjectedStatus('max', { ...idleStatus, state: 'working', isRunning: true, stateText: 'Eingestempelt' });

      const labelled = toOfflineStatus(lastKnownStatus('max')!);
      expect(labelled.stateText).toContain('offline vorgemerkt');
    });

    it('lets a fresh server status overwrite a projection', () => {
      rememberProjectedStatus('max', { ...idleStatus, state: 'working', isRunning: true, stateText: 'Eingestempelt' });
      rememberObservedStatus('max', idleStatus);

      expect(lastKnownStatus('max')?.origin).toBe('observed');
      expect(toOfflineStatus(lastKnownStatus('max')!).stateText).toContain('Nicht eingestempelt');
    });

    it('knows nothing about an employee it never saw', () => {
      expect(lastKnownStatus('unbekannt')).toBeNull();
      expect(lastKnownStatus(null)).toBeNull();
      expect(lastKnownStatus(undefined)).toBeNull();
    });

    it('ignores a corrupted cache instead of throwing', () => {
      window.localStorage.setItem('stempeluhr.employee-status-cache.v1', '[]');
      expect(lastKnownStatus('max')).toBeNull();
    });

    it('renders an unparsable timestamp instead of NaN', () => {
      expect(formatShortTime('kaputt')).toBe('--:--');
    });

    it('keeps the entry that was refreshed most recently when trimming', () => {
      for (let index = 0; index < 100; index += 1) {
        rememberObservedStatus(`emp-${index}`, idleStatus);
      }
      // Refresh the oldest entry, then push one past the capacity: FIFO on the
      // FIRST insertion would now evict exactly the entry just refreshed.
      rememberObservedStatus('emp-0', idleStatus);
      rememberObservedStatus('emp-100', idleStatus);

      expect(lastKnownStatus('emp-0')).not.toBeNull();
      expect(lastKnownStatus('emp-1')).toBeNull();
      expect(lastKnownStatus('emp-100')).not.toBeNull();
    });
  });
});
