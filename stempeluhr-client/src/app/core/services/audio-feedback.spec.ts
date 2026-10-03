import { AudioFeedback } from './audio-feedback';

describe('AudioFeedback', () => {
  const original = window.AudioContext;
  afterEach(() => {
    window.AudioContext = original;
  });

  it('reuses a context across stamps, disconnects finished nodes and closes on destroy', () => {
    const oscillators: any[] = [];
    const gains: any[] = [];
    const close = vi.fn(() => Promise.resolve());
    const constructor = vi.fn(function () {
      return {
        state: 'running',
        currentTime: 0,
        destination: {},
        close,
        createOscillator: () => {
          const node = {
            frequency: { value: 0 },
            connect: vi.fn(),
            disconnect: vi.fn(),
            start: vi.fn(),
            stop: vi.fn(),
            onended: null,
          };
          oscillators.push(node);
          return node;
        },
        createGain: () => {
          const node = {
            gain: { setValueAtTime: vi.fn(), exponentialRampToValueAtTime: vi.fn() },
            connect: vi.fn(),
            disconnect: vi.fn(),
          };
          gains.push(node);
          return node;
        },
      };
    });
    window.AudioContext = constructor as unknown as typeof AudioContext;
    const service = new AudioFeedback();
    for (let i = 0; i < 100; i++) service.playBeeps(1);
    expect(constructor).toHaveBeenCalledTimes(1);
    oscillators.forEach((node) => node.onended());
    expect(oscillators.every((node) => node.disconnect.mock.calls.length === 1)).toBe(true);
    expect(gains.every((node) => node.disconnect.mock.calls.length === 1)).toBe(true);
    service.ngOnDestroy();
    expect(close).toHaveBeenCalledTimes(1);
  });

  it('does not throw when Chromium cannot create an audio context', () => {
    window.AudioContext = function () {
      throw new Error('device unavailable');
    } as unknown as typeof AudioContext;
    expect(() => new AudioFeedback().playBeeps(2)).not.toThrow();
  });
});
