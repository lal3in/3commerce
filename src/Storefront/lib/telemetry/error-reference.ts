// The support reference an error page shows the shopper (app/error.tsx, app/global-error.tsx).
//
// Preference order:
//   1. the server error's `digest` — Next attaches it to every server-side render error, and instrumentation.ts
//      `onRequestError` logs it next to the trace id, so support goes digest → log line → trace;
//   2. the trace id of the server render that produced the page (<meta name="traceparent">, emitted via
//      next.config `experimental.clientTraceMetadata`) — for an error raised in the browser after hydration;
//   3. a random reference, reported with the error to Faro so it is still searchable.
// None of these identifies the shopper.

export interface ErrorLike {
  message?: string;
  digest?: string;
}

/** The trace id inside a W3C `traceparent` (`00-<32 hex>-<16 hex>-<2 hex>`), or null. */
export function traceIdFromTraceparent(value: string | null | undefined): string | null {
  const match = /^[0-9a-f]{2}-([0-9a-f]{32})-[0-9a-f]{16}-[0-9a-f]{2}$/i.exec(value?.trim() ?? "");
  if (!match || /^0+$/.test(match[1])) return null;
  return match[1].toLowerCase();
}

function randomReference(): string {
  const bytes = new Uint8Array(8);
  globalThis.crypto?.getRandomValues?.(bytes);
  return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
}

export function errorReference(error: ErrorLike, doc?: Pick<Document, "querySelector"> | null): string {
  if (error.digest) return error.digest;
  const meta = doc?.querySelector('meta[name="traceparent"]')?.getAttribute("content");
  return traceIdFromTraceparent(meta) ?? randomReference();
}

interface FaroGlobal {
  api?: { pushError?: (error: Error, options?: { context?: Record<string, string> }) => void };
}

/** Reports the error to browser RUM (when Faro is running) tagged with the reference the shopper sees. */
export function reportToRum(error: Error, reference: string): void {
  try {
    const faro = (globalThis as { faro?: FaroGlobal }).faro;
    faro?.api?.pushError?.(error, { context: { reference, boundary: "error-page" } });
  } catch {
    /* telemetry must never break the error page */
  }
}
