"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { useTranslations } from "next-intl";
import { consentDecided, readConsent, writeConsent } from "@/lib/consent";
import { clearFirstPartyIds } from "@/lib/visitor";

export default function ConsentBanner() {
  const t = useTranslations("consent");
  const [show, setShow] = useState(false);
  const [analytics, setAnalytics] = useState(false);
  const [marketing, setMarketing] = useState(false);

  useEffect(() => {
    const state = readConsent();
    setAnalytics(state.analytics);
    setMarketing(state.marketing);
    setShow(!consentDecided(state));
  }, []);

  if (!show) return null;

  function save(nextAnalytics: boolean, nextMarketing: boolean) {
    writeConsent({ analytics: nextAnalytics, marketing: nextMarketing });
    if (!nextAnalytics) clearFirstPartyIds(); // withdrawing analytics drops first-party ids
    setShow(false);
  }

  return (
    <div
      role="dialog"
      aria-label={t("bannerLabel")}
      className="fixed inset-x-0 bottom-0 z-50 border-t border-neutral-200 bg-white p-4 shadow-lg"
    >
      <div className="mx-auto flex max-w-6xl flex-col gap-3 md:flex-row md:items-center md:justify-between">
        <div className="text-sm text-neutral-700">
          <p className="font-medium">{t("title")}</p>
          <p className="text-neutral-500">{t("intro")}</p>
          <div className="mt-2 flex flex-wrap gap-4">
            <label className="flex items-center gap-1 text-neutral-400" title={t("tips.necessary")}>
              <input type="checkbox" checked readOnly disabled aria-label={t("necessaryAlwaysOn")} title={t("tips.necessary")} /> {t("necessary")}
            </label>
            <label className="flex items-center gap-1" title={t("tips.analytics")}>
              <input type="checkbox" checked={analytics} title={t("tips.analytics")} onChange={(e) => setAnalytics(e.target.checked)} /> {t("analytics")}
            </label>
            <label className="flex items-center gap-1" title={t("tips.marketing")}>
              <input type="checkbox" checked={marketing} title={t("tips.marketing")} onChange={(e) => setMarketing(e.target.checked)} /> {t("marketing")}
            </label>
            <Link href="/privacy" className="text-neutral-500 underline" title={t("tips.privacySettings")}>
              {t("privacySettings")}
            </Link>
          </div>
        </div>
        <div className="flex shrink-0 flex-wrap gap-2">
          <button
            type="button"
            onClick={() => save(false, false)}
            title={t("tips.rejectNonEssential")}
            className="rounded-md border border-neutral-300 px-3 py-2 text-sm hover:bg-neutral-50"
          >
            {t("rejectNonEssential")}
          </button>
          <button
            type="button"
            onClick={() => save(analytics, marketing)}
            title={t("tips.saveChoices")}
            className="rounded-md border border-neutral-300 px-3 py-2 text-sm hover:bg-neutral-50"
          >
            {t("saveChoices")}
          </button>
          <button
            type="button"
            onClick={() => save(true, true)}
            title={t("tips.acceptAll")}
            className="rounded-md bg-neutral-900 px-3 py-2 text-sm text-white hover:bg-neutral-700"
          >
            {t("acceptAll")}
          </button>
        </div>
      </div>
    </div>
  );
}
