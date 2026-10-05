import { addMinutes, floorToStep, formatDay, formatRange, minutesBetween, nowInZone } from './local-time';

describe('local-time', () => {
  it('steps across hour, day and month boundaries by wall clock', () => {
    expect(addMinutes('2026-10-05T23:58', 5)).toBe('2026-10-06T00:03');
    expect(addMinutes('2026-10-01T00:00', -1)).toBe('2026-09-30T23:59');
    expect(addMinutes('2026-10-05T08:30', 24 * 60)).toBe('2026-10-06T08:30');
  });

  it('measures minutes between two times, negative when reversed', () => {
    expect(minutesBetween('2026-10-05T22:00', '2026-10-06T06:00')).toBe(480);
    expect(minutesBetween('2026-10-06T06:00', '2026-10-05T22:00')).toBe(-480);
  });

  it('floors to a multiple of the step', () => {
    expect(floorToStep('2026-10-05T08:07', 5)).toBe('2026-10-05T08:05');
    expect(floorToStep('2026-10-05T08:05', 5)).toBe('2026-10-05T08:05');
  });

  it('reads the wall clock of a time zone, independent of the device', () => {
    const instant = new Date('2026-07-01T22:30:00Z');
    expect(nowInZone('Europe/Berlin', instant)).toBe('2026-07-02T00:30');
    expect(nowInZone('America/New_York', instant)).toBe('2026-07-01T18:30');
    expect(nowInZone('Europe/Berlin', new Date('2026-01-01T23:30:00Z'))).toBe('2026-01-02T00:30');
  });

  it('falls back to the device clock for an unknown zone instead of failing', () => {
    expect(nowInZone('Nowhere/Land')).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/);
  });

  it('formats days and ranges, keeping a night shift over midnight readable', () => {
    expect(formatDay('2026-10-05T08:00')).toBe('Mo 05.10.');
    expect(formatRange('2026-10-05T08:00', '2026-10-05T16:30')).toBe('Mo 05.10. 08:00–16:30');
    expect(formatRange('2026-10-05T22:00', '2026-10-06T06:00')).toBe('Mo 05.10. 22:00 – Di 06.10. 06:00');
    expect(formatRange('2026-10-05T22:00', null)).toBe('Mo 05.10. 22:00 – läuft');
  });
});
