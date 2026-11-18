import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { probesEnabled } from "@/lib/telemetry/probe";

export const dynamic = "force-dynamic";
export const metadata: Metadata = { robots: { index: false, follow: false } };

// Deliberate SERVER render error (lib/telemetry/probe.ts): app/error.tsx must show the digest as the support
// reference, and instrumentation.ts onRequestError must log that digest with the trace id. 404 in production.
export default function ServerErrorProbe(): never {
  if (!probesEnabled()) notFound();
  throw new Error("Deliberate storefront error probe (server render)");
}
