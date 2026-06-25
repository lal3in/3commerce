// Consent-aware, batched first-party analytics (mt5_5). track() is a no-op without Analytics consent;
// events are queued and flushed in batches to the collector (mt5_4). GOTCHA: discrete events only —
// no session replay, no keystroke logging.

import { hasConsent } from "./consent";
import { sessionId, visitorId } from "./visitor";

interface QueuedEvent {
  schemaVersion: number;
  eventType: string;
  occurredAt: string;
  eventId: string;
  payload?: Record<string, string>;
}

const MAX_BATCH = 20;
const FLUSH_MS = 5000;

const queue: QueuedEvent[] = [];
let timer: ReturnType<typeof setTimeout> | null = null;

function newId(): string {
  if (typeof crypto !== "undefined" && typeof crypto.randomUUID === "function") {
    return crypto.randomUUID();
  }
  return `${Math.random().toString(36).slice(2)}${Date.now().toString(36)}`;
}

export function track(eventType: string, payload?: Record<string, string>): void {
  if (typeof window === "undefined" || !hasConsent("analytics")) return; // consent-gated at the source

  queue.push({
    schemaVersion: 1,
    eventType,
    occurredAt: new Date().toISOString(),
    eventId: newId(),
    payload,
  });

  if (queue.length >= MAX_BATCH) {
    void flush();
  } else if (timer === null) {
    timer = setTimeout(() => {
      timer = null;
      void flush();
    }, FLUSH_MS);
  }
}

export async function flush(): Promise<void> {
  if (typeof window === "undefined" || queue.length === 0 || !hasConsent("analytics")) {
    queue.length = 0;
    return;
  }

  const events = queue.splice(0, queue.length).map((event) => ({
    ...event,
    visitorId: visitorId(),
    sessionId: sessionId(),
    analyticsConsent: true,
  }));

  try {
    await fetch("/api/analytics/events", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ events }),
      keepalive: true,
    });
  } catch {
    // Best-effort: analytics is non-critical, drop the batch on failure rather than disrupt the page.
  }
}
