"use client";

import Link from "next/link";
import { useTranslations } from "next-intl";

// The segment's error state: nothing was charged by merely loading this page, so the honest message is "try again".
export default function PayRenewalError({ reset }: { error: Error; reset: () => void }) {
  const t = useTranslations("subscriptions");
  return (
    <div className="max-w-xl">
      <h1 className="mb-2 text-xl font-semibold">{t("pageTitle")}</h1>
      <p role="alert" className="text-sm text-red-700">{t("failed")}</p>
      <div className="mt-4 flex gap-4 text-sm">
        <button type="button" onClick={reset} className="underline">{t("tryAgain")}</button>
        <Link href="/account" className="underline">{t("backToAccount")}</Link>
      </div>
    </div>
  );
}
