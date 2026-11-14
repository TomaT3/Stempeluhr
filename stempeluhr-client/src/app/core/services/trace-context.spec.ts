import { traceIdOf, traceparentFor } from './trace-context';

describe('trace context (issue #148)', () => {
  it('uses the event ID as trace ID', () => {
    expect(traceIdOf('4BF92F3577B34DA6A3CE929D0E0E4736')).toBe('4bf92f3577b34da6a3ce929d0e0e4736');
  });

  it('pads the shorter fallback ID of a kiosk without crypto.randomUUID', () => {
    expect(traceIdOf('19a2b3c4d5e00ff00aa')).toBe('000000000000019a2b3c4d5e00ff00aa');
  });

  it.each([null, undefined, '', 'not-hex', '0000', 'a'.repeat(33)])('has no trace ID for %j', eventId => {
    expect(traceIdOf(eventId)).toBeNull();
    expect(traceparentFor(eventId)).toBeNull();
  });

  it('builds a sampled W3C traceparent with a new span ID per attempt', () => {
    const first = traceparentFor('4bf92f3577b34da6a3ce929d0e0e4736');
    const second = traceparentFor('4bf92f3577b34da6a3ce929d0e0e4736');

    expect(first).toMatch(/^00-4bf92f3577b34da6a3ce929d0e0e4736-[0-9a-f]{16}-01$/);
    expect(first).not.toMatch(/-0{16}-/);
    expect(second?.slice(0, 35)).toBe(first?.slice(0, 35));
    expect(second).not.toBe(first);
  });
});
