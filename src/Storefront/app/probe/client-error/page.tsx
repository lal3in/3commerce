import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { ClientErrorProbe } from "@/components/telemetry/ClientErrorProbe";
import { probesEnabled } from "@/lib/telemetry/probe";

export const dynamic = "force-dynamic";
export const metadata: Metadata = { robots: { index: false, follow: false } };

// Deliberate BROWSER render error (lib/telemetry/probe.ts): no digest exists, so app/error.tsx shows the SSR trace
// id from <meta name="traceparent"> as the reference and reports the error to Faro. 404 in production.
export default function ClientErrorProbePage() {
  if (!probesEnabled()) notFound();
  return <ClientErrorProbe />;
}
