"use client";

import { useEffect, useState } from "react";
import "./globals.css";
import { ErrorPanel } from "@/components/telemetry/ErrorPanel";
import { ERROR_COPY, errorCopyFor, type ErrorCopy } from "@/lib/telemetry/error-copy";

const LOCALE_COOKIE = "3c_locale"; // i18n/locales.ts LOCALE_COOKIE (not imported: that module pulls in every catalog)

function chooseCopy(): ErrorCopy {
  if (typeof document === "undefined") return ERROR_COPY.en;
  const cookie = document.cookie
    .split(";")
    .map((c) => c.trim())
    .find((c) => c.startsWith(`${LOCALE_COOKIE}=`))
    ?.slice(LOCALE_COOKIE.length + 1);
  return errorCopyFor(cookie ? decodeURIComponent(cookie) : navigator.language);
}

// Last-resort boundary: the ROOT layout itself failed, so this replaces it entirely — its own <html>/<body>, no
// next-intl provider. Copy comes from lib/telemetry/error-copy.ts (same strings as messages `errorPage`), chosen
// by the shopper's locale cookie, else the browser language, else English.
export default function GlobalError({ error, reset }: { error: Error & { digest?: string }; reset: () => void }) {
  const [copy, setCopy] = useState<ErrorCopy>(ERROR_COPY.en);
  useEffect(() => setCopy(chooseCopy()), []);
  return (
    <html lang="en">
      <body className="min-h-screen bg-white px-4 text-neutral-900">
        <ErrorPanel error={error} reset={reset} copy={copy} />
      </body>
    </html>
  );
}
