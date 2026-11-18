"use client";

import { useEffect } from "react";

interface FaroInitProps {
  /** deployment.environment.name, resolved on the server (lib/telemetry/release.ts). */
  environment: string;
  /** Extra origins that receive `traceparent` (a separate public gateway origin); same-origin always does. */
  traceOrigins: string[];
}

/**
 * Starts browser RUM + error telemetry (lib/telemetry/faro.ts) once the page is interactive. Renders nothing.
 * Strictly necessary and anonymous (owner decision Q3), so it is NOT gated on the consent banner. The SDK is
 * loaded with a dynamic import so it never weighs on the first render.
 */
export function FaroInit({ environment, traceOrigins }: FaroInitProps) {
  useEffect(() => {
    let cancelled = false;
    void import("@/lib/telemetry/faro")
      .then(({ startFaro }) => {
        if (!cancelled) startFaro({ environment, traceOrigins });
      })
      .catch(() => {
        /* telemetry must never break the page */
      });
    return () => {
      cancelled = true;
    };
  }, [environment, traceOrigins]);
  return null;
}
