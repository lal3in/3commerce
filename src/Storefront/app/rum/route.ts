import { NextResponse } from "next/server";
import { scrubTelemetry } from "@/lib/telemetry/scrub";
import { faroBatchToEvents, sendToInbox } from "@/lib/telemetry/error-inbox";

// Same-origin relay for browser RUM (audit P1-2, lib/telemetry/faro.ts → here → the collector's `faro` receiver).
// The collector is internal-only (port 8027); the browser only ever talks to the storefront.
//
// Anonymity is enforced HERE as well as in the browser (owner decision Q3, 2026-11-15):
//   - the batch is parsed and scrubbed again (lib/telemetry/scrub.ts): session/user meta, emails, tokens and query
//     strings never reach the collector, even from a tampered or outdated client;
//   - NO client header is forwarded — not the IP (X-Forwarded-For / X-Real-IP / Forwarded), not cookies, not the
//     user agent, not Faro's x-faro-session-id. The outbound request carries only a content type;
//   - the payload is capped (MAX_BYTES) and must be JSON.
// Fail-soft: a missing, slow or failing receiver is answered 202 with nothing relayed, so Faro never retries in a
// loop and the page never notices. Cross-site posts are refused.
//
// Errors also go to the GlitchTip inbox (ADR-0067): each Faro EXCEPTION in the scrubbed batch becomes a Sentry store
// event (lib/telemetry/error-inbox.ts), sent server-side to SENTRY_DSN when it is set — the browser never talks to
// GlitchTip and runs no Sentry SDK.
//
// FARO_RECEIVER_URL — the receiver's URL (it accepts POSTs at "/"); dev default http://localhost:8027/ (compose/k8s: http://otel-collector:8027/). It needs Content-Type application/json and answers 202; the receiver is alpha (ADR-0065).

export const dynamic = "force-dynamic";

/** Largest batch accepted, in bytes. Faro batches are a few KB; this leaves room for a trace export. */
const MAX_BYTES = 256 * 1024;
const FORWARD_TIMEOUT_MS = 3_000;

function receiverUrl(): string {
  return (process.env.FARO_RECEIVER_URL ?? "").trim() || "http://localhost:8027/";
}

/** Reads at most MAX_BYTES of the body; null when it is larger. */
async function readCapped(req: Request): Promise<string | null> {
  const declared = Number(req.headers.get("content-length") ?? "0");
  if (declared > MAX_BYTES) return null;
  if (!req.body) return "";
  const reader = req.body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.byteLength;
    if (total > MAX_BYTES) {
      await reader.cancel().catch(() => undefined);
      return null;
    }
    chunks.push(value);
  }
  return Buffer.concat(chunks).toString("utf8");
}

const accepted = () => new NextResponse(null, { status: 202 });

export async function POST(req: Request) {
  // Same-origin only: the browser SDK posts from our own pages; anything cross-site is not ours to relay.
  if (req.headers.get("sec-fetch-site") === "cross-site") {
    return NextResponse.json({ title: "Cross-site telemetry is not accepted." }, { status: 403 });
  }

  const raw = await readCapped(req).catch(() => null);
  if (raw === null) {
    return NextResponse.json(
      { type: "about:blank", title: "Payload too large.", status: 413, detail: `At most ${MAX_BYTES} bytes.` },
      { status: 413, headers: { "content-type": "application/problem+json" } },
    );
  }

  let body: unknown;
  try {
    body = JSON.parse(raw);
  } catch {
    return NextResponse.json(
      { type: "about:blank", title: "Malformed telemetry payload.", status: 400 },
      { status: 400, headers: { "content-type": "application/problem+json" } },
    );
  }
  if (typeof body !== "object" || body === null || Array.isArray(body)) return accepted();

  const scrubbed = scrubTelemetry(body);
  // Both hops are best-effort and run side by side; neither failure reaches the page.
  await Promise.allSettled([relayToReceiver(scrubbed), sendToInbox(faroBatchToEvents(scrubbed))]);
  return accepted();
}

async function relayToReceiver(scrubbed: unknown): Promise<void> {
  try {
    await fetch(receiverUrl(), {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(scrubbed),
      signal: AbortSignal.timeout(FORWARD_TIMEOUT_MS),
      cache: "no-store",
      // Don't trace the relay hop itself (it would add a span per beacon) and never propagate context to it.
      opentelemetry: { ignore: true },
    });
  } catch {
    // Receiver not deployed / not reachable: telemetry is best-effort, the shopper's page is unaffected.
  }
}
