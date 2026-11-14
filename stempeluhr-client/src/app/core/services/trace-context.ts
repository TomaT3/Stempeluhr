/**
 * W3C Trace Context für Stempel (Issue #148): Die Trace-ID ist die Event-ID.
 * Live-Versuch, Nachtrag aus der Queue und die Kimai-Aufrufe darunter liegen
 * so in einer Trace, und wer die Event-ID kennt (Diagnose, Ablehnung), hat
 * auch die Trace. Der Kiosk zeichnet selbst keine Spans auf; die Span-ID ist
 * nur der Eltern-Verweis für die API.
 */

/**
 * Event-IDs sind 32 Hex-Zeichen (UUID ohne Bindestriche). Ohne
 * `crypto.randomUUID` (Kiosk über http://) sind sie kürzer und werden links
 * aufgefüllt. Alles andere hat keine Trace-ID.
 */
export function traceIdOf(eventId: string | null | undefined): string | null {
  if (!eventId || !/^[0-9a-f]{1,32}$/i.test(eventId) || /^0+$/.test(eventId)) return null;
  return eventId.toLowerCase().padStart(32, '0');
}

/** `traceparent` für einen Versuch dieses Stempels, oder null ohne passende Event-ID. */
export function traceparentFor(eventId: string | null | undefined): string | null {
  const traceId = traceIdOf(eventId);
  return traceId ? `00-${traceId}-${randomSpanId()}-01` : null;
}

function randomSpanId(): string {
  const bytes = new Uint8Array(8);
  do {
    crypto.getRandomValues(bytes);
  } while (bytes.every(byte => byte === 0));
  return Array.from(bytes, byte => byte.toString(16).padStart(2, '0')).join('');
}
