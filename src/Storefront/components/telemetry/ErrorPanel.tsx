"use client";

import { useEffect, useState } from "react";
import { errorReference, reportToRum } from "@/lib/telemetry/error-reference";
import type { ErrorCopy } from "@/lib/telemetry/error-copy";

interface ErrorPanelProps {
  error: Error & { digest?: string };
  reset: () => void;
  copy: ErrorCopy;
}

/**
 * The body of the storefront's error pages (app/error.tsx, app/global-error.tsx): a plain apology, a retry, a way
 * home, and the support reference (digest or trace id — lib/telemetry/error-reference.ts). Never the error
 * message or a stack trace (components.md §5). The error is reported to browser RUM with the same reference;
 * server-side errors are already logged with it by instrumentation.ts `onRequestError`.
 */
export function ErrorPanel({ error, reset, copy }: ErrorPanelProps) {
  // The digest is known on the first render; the trace id needs the document, so it settles after mount.
  const [reference, setReference] = useState<string | null>(error.digest ?? null);

  useEffect(() => {
    const ref = errorReference(error, typeof document === "undefined" ? null : document);
    setReference(ref);
    reportToRum(error, ref);
  }, [error]);

  return (
    <section role="alert" aria-labelledby="error-title" className="mx-auto max-w-xl py-16 text-center">
      <h1 id="error-title" className="text-2xl font-semibold">
        {copy.title}
      </h1>
      <p className="mt-3 text-neutral-600">{copy.body}</p>
      <p className="mt-6 text-sm text-neutral-600">
        {copy.reference}:{" "}
        <code data-testid="error-reference" className="rounded bg-neutral-100 px-2 py-1 font-mono text-neutral-900 select-all">
          {reference ?? "…"}
        </code>
      </p>
      <div className="mt-8 flex items-center justify-center gap-4">
        <button
          type="button"
          onClick={() => reset()}
          className="rounded-md bg-neutral-900 px-4 py-2 text-sm font-medium text-white hover:bg-neutral-700"
        >
          {copy.retry}
        </button>
        {/* A full navigation on purpose: after an error the client router may be in a bad state. */}
        {/* eslint-disable-next-line @next/next/no-html-link-for-pages */}
        <a href="/" className="text-sm underline">
          {copy.home}
        </a>
      </div>
    </section>
  );
}
