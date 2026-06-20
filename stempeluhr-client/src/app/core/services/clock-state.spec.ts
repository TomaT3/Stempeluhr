import { ClockState } from './clock-state';

describe('ClockState', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-06-18T10:30:00.000Z'));
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('calculates running elapsed time from the startedAt timestamp', () => {
    const state = new ClockState();

    state.setStatus({
      isRunning: true,
      activeTimesheetId: 42,
      startedAt: '2026-06-18T08:15:00.000Z',
      durationSeconds: 0,
      state: 'working',
      stateText: 'Eingestempelt',
    });

    expect(state.elapsed()).toBe(8100);

    vi.advanceTimersByTime(1000);

    expect(state.elapsed()).toBe(8101);
  });

  it('falls back to the server duration when startedAt is missing', () => {
    const state = new ClockState();

    state.setStatus({
      isRunning: true,
      activeTimesheetId: 42,
      startedAt: null,
      durationSeconds: 30,
      state: 'working',
      stateText: 'Eingestempelt',
    });

    vi.advanceTimersByTime(2000);

    expect(state.elapsed()).toBe(32);
  });
});
