"use client";

import { useTranslations } from "next-intl";
import { ErrorPanel } from "@/components/telemetry/ErrorPanel";

// Route-segment error boundary for every storefront page (audit P1-3). Renders inside the root layout, so the
// header, footer and the shopper's locale (messages/<code>.json `errorPage`) are all still there.
export default function ErrorPage({ error, reset }: { error: Error & { digest?: string }; reset: () => void }) {
  const t = useTranslations("errorPage");
  return (
    <ErrorPanel
      error={error}
      reset={reset}
      copy={{ title: t("title"), body: t("body"), reference: t("reference"), retry: t("retry"), home: t("home") }}
    />
  );
}
