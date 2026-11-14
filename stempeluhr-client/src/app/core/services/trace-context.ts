/**
 * W3C Trace Context für Stempel (Issue #148): Die Trace-ID ist die Event-ID.
 * Live-Versuch, Nachtrag aus der Queue und die Kimai-Aufrufe darunter liegen
 * so in einer Trace, und wer die Event-ID kennt (Diagnose, Ablehnung), hat
 * auch die Trace. Der Kiosk zeichnet selbst keine Spans auf; die Span-ID ist
 * nur der Eltern-Verweis für die API.
 */

/**
 * Neue Event-ID: 32 Hex-Zeichen, damit sie unverändert Trace-ID ist.
 * `crypto.getRandomValues` gibt es anders als `crypto.randomUUID` auch ohne
 * sicheren Kontext (Kiosk über http://).
 */
export function newEventId(): string {
  return randomHex(16);
}

/**
 * Event-IDs sind 32 Hex-Zeichen. Ältere Kiosk-Versionen erzeugten über http://
 * kürzere IDs; solche Ereignisse können noch in einer Queue liegen und werden
 * links aufgefüllt. Alles andere hat keine Trace-ID.
 */
export function traceIdOf(eventId: string | null | undefined): string | null {
  if (!eventId || !/^[0-9a-f]{1,32}$/i.test(eventId) || /^0+$/.test(eventId)) return null;
  return eventId.toLowerCase().padStart(32, '0');
}

/** `traceparent` für einen Versuch dieses Stempels, oder null ohne passende Event-ID. */
export function traceparentFor(eventId: string | null | undefined): string | null {
  const traceId = traceIdOf(eventId);
  return traceId ? `00-${traceId}-${randomHex(8)}-01` : null;
}

/**
 * Zufällige Bytes als Hex, nie nur Nullen (in W3C ungültig). Ohne `crypto`
 * (sehr alte WebView) mit `Math.random`: Ein Stempel darf daran nie scheitern.
 */
function randomHex(length: number): string {
  const bytes = new Uint8Array(length);
  do {
    if (typeof crypto !== 'undefined' && typeof crypto.getRandomValues === 'function') {
      crypto.getRandomValues(bytes);
    } else {
      for (let i = 0; i < length; i++) bytes[i] = Math.floor(Math.random() * 256);
    }
  } while (bytes.every(byte => byte === 0));
  return Array.from(bytes, byte => byte.toString(16).padStart(2, '0')).join('');
}
