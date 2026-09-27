import { isOnDefaultTask } from '../models/kiosk.models';
import { ClockState, projectClockStatus } from './clock-state';

const idle = {
  isRunning: false,
  activeTimesheetId: null,
  startedAt: null,
  durationSeconds: 0,
  state: 'clockedOut' as const,
  stateText: 'Nicht eingestempelt',
};

describe('projectClockStatus', () => {
  const at = '2026-09-18T06:55:00.000Z';

  it('turns a queued start into a running status', () => {
    const projected = projectClockStatus(idle, 'start', at);

    expect(projected.state).toBe('working');
    expect(projected.isRunning).toBe(true);
    expect(projected.startedAt).toBe(at);
    expect(projected.stateText).toBe('Eingestempelt');
  });

  it('turns a queued pause and its end into paused/working again', () => {
    const paused = projectClockStatus(projectClockStatus(idle, 'start', at), 'pauseStart', at);
    expect(paused.state).toBe('paused');
    expect(paused.isRunning).toBe(true);

    const resumed = projectClockStatus(paused, 'pauseEnd', at);
    expect(resumed.state).toBe('working');
    expect(resumed.startedAt).toBe(at);
    // The server resumes the task from before the pause - unknown offline.
    expect(isOnDefaultTask(resumed)).toBe(false);
    expect(resumed.activeTaskId ?? null).toBeNull();
  });

  it('treats a queued start as the default task', () => {
    expect(isOnDefaultTask(projectClockStatus(idle, 'start', at))).toBe(true);
  });

  it('turns a queued start on a task into working on that task', () => {
    const onTask = projectClockStatus(idle, 'start', at, { id: 'kx', label: 'Kunde X' });
    expect(onTask.state).toBe('working');
    expect(onTask.activeTaskId).toBe('kx');
    expect(onTask.activeTaskLabel).toBe('Kunde X');
    expect(isOnDefaultTask(onTask)).toBe(false);
  });

  it('turns a queued task switch into working on that task - and back', () => {
    const working = projectClockStatus(idle, 'start', at);

    const onTask = projectClockStatus(working, 'switch', at, { id: 'kx', label: 'Kunde X' });
    expect(onTask.state).toBe('working');
    expect(onTask.activeTaskId).toBe('kx');
    expect(onTask.activeTaskLabel).toBe('Kunde X');
    expect(onTask.stateText).toBe('Wechsel zu Kunde X');

    expect(isOnDefaultTask(onTask)).toBe(false);

    const back = projectClockStatus(onTask, 'switch', at, null);
    expect(back.state).toBe('working');
    expect(back.activeTaskId).toBeNull();
    expect(isOnDefaultTask(back)).toBe(true);
    expect(back.stateText).toBe('Zurück zur Standard-Tätigkeit');

    const backNamed = projectClockStatus(onTask, 'switch', at, null, 'Büro');
    expect(backNamed.stateText).toBe('Wechsel zu Büro');
  });

  it('turns a queued stop into a clocked-out status', () => {
    const stopped = projectClockStatus({ ...idle, state: 'working', isRunning: true }, 'stop', at);

    expect(stopped.state).toBe('clockedOut');
    expect(stopped.isRunning).toBe(false);
    expect(stopped.startedAt).toBeNull();
    expect(stopped.stateText).toBe('Ausgestempelt');
  });
});

describe('isOnDefaultTask', () => {
  const working = { ...idle, state: 'working' as const, isRunning: true };

  it('follows the server flag', () => {
    expect(isOnDefaultTask({ ...working, activeIsDefaultTask: true })).toBe(true);
    expect(isOnDefaultTask({ ...working, activeIsDefaultTask: false })).toBe(false);
  });

  it('falls back to "no task" for statuses without the flag (older API, cached)', () => {
    expect(isOnDefaultTask(working)).toBe(true);
    expect(isOnDefaultTask({ ...working, activeTaskId: 'kx' })).toBe(false);
  });
});

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
