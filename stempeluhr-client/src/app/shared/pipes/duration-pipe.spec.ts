import { DurationPipe } from './duration-pipe';

describe('DurationPipe', () => {
  const pipe = new DurationPipe();

  it('formatiert Sekunden als HH:mm:ss (Default)', () => {
    expect(pipe.transform(0)).toBe('00:00:00');
    expect(pipe.transform(3661)).toBe('01:01:01');
    expect(pipe.transform(180_000)).toBe('50:00:00');
  });

  it('formatiert ohne Sekunden als HH:mm, wenn includeSeconds=false', () => {
    expect(pipe.transform(0, false)).toBe('00:00');
    expect(pipe.transform(3661, false)).toBe('01:01');
    expect(pipe.transform(180_000, false)).toBe('50:00');
    expect(pipe.transform(59, false)).toBe('00:00');
    expect(pipe.transform(60, false)).toBe('00:01');
  });

  it('rundet negative/fehlende Werte auf 0 ab', () => {
    expect(pipe.transform(-5)).toBe('00:00:00');
    expect(pipe.transform(null)).toBe('00:00:00');
    expect(pipe.transform(undefined)).toBe('00:00:00');
  });
});
