// Where browser telemetry may run and what it may send — pure helpers for lib/telemetry/faro.ts, kept free of SDK
// imports so the unit spec (e2e/telemetry-scrub.spec.ts) can load them in Node.

import { scrubTransportItem } from "./scrub";

/** Same-origin relay that receives Faro batches (app/rum/route.ts). */
export const RUM_PATH = "/rum";

/** Third-party payment hosts: never traced, never given a `traceparent`, their errors never reported. */
export const STRIPE_URL_PATTERNS: RegExp[] = [
  /^https?:\/\/([a-z0-9-]+\.)*stripe\.com(\/|:|$)/i,
  /^https?:\/\/([a-z0-9-]+\.)*stripe\.network(\/|:|$)/i,
];

export function isStripeUrl(url: string | undefined | null): boolean {
  return !!url && STRIPE_URL_PATTERNS.some((p) => p.test(url));
}

/**
 * Whether Faro may start in this window: only the storefront's own top-level page. A frame (a Stripe Elements
 * iframe, or the storefront framed by anything) and any Stripe-hosted document are excluded.
 */
export function mayStartFaro(win: { self: unknown; top: unknown; location: { href: string } }): boolean {
  let framed = true;
  try {
    framed = win.self !== win.top;
  } catch {
    framed = true; // cross-origin parent access throws — that IS a frame
  }
  if (framed) return false;
  return !isStripeUrl(win.location.href);
}

interface ItemLike {
  type: string;
  payload: unknown;
  meta: unknown;
}

/** An exception whose every stack frame is in a Stripe script. */
export function isStripeError(item: ItemLike): boolean {
  if (item.type !== "exception") return false;
  const frames = (item.payload as { stacktrace?: { frames?: Array<{ filename?: string }> } })?.stacktrace?.frames ?? [];
  return frames.length > 0 && frames.every((f) => isStripeUrl(f.filename));
}

/** Faro's `beforeSend`: drop Stripe-originated errors, then scrub (always into a new object). */
export function beforeSend<T extends ItemLike>(item: T): T | null {
  if (isStripeError(item)) return null;
  return scrubTransportItem(item);
}
