"use client";

import { useEffect, useState } from "react";
import { useTranslations } from "next-intl";
import { readConsent, writeConsent } from "@/lib/consent";
import { clearFirstPartyIds } from "@/lib/visitor";

// Client leaf: mirrors the ConsentBanner semantics — writeConsent broadcasts the change and
// withdrawing analytics drops the first-party ids (components.md §1).
export function ConsentSettings() {
  const t = useTranslations("privacy");
  const [analytics, setAnalytics] = useState(false);
  const [marketing, setMarketing] = useState(false);
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    const state = readConsent();
    setAnalytics(state.analytics);
    setMarketing(state.marketing);
  }, []);

  function save() {
    writeConsent({ analytics, marketing });
    if (!analytics) clearFirstPartyIds();
    setSaved(true);
  }

  return (
    <div className="mt-6 space-y-4">
      <label className="flex items-start gap-3 rounded-md border border-neutral-200 p-4" title={t("tips.necessary")}>
        <input type="checkbox" checked readOnly disabled className="mt-1" aria-label={t("necessaryTitle")} title={t("tips.necessary")} />
        <span>
          <span className="block text-sm font-medium text-neutral-400">{t("necessaryTitle")}</span>
          <span className="block text-sm text-neutral-500">{t("necessaryDescription")}</span>
        </span>
      </label>
      <label className="flex items-start gap-3 rounded-md border border-neutral-200 p-4" title={t("tips.analytics")}>
        <input
          type="checkbox"
          checked={analytics}
          title={t("tips.analytics")}
          onChange={(e) => {
            setAnalytics(e.target.checked);
            setSaved(false);
          }}
          className="mt-1"
        />
        <span>
          <span className="block text-sm font-medium">{t("analyticsTitle")}</span>
          <span className="block text-sm text-neutral-500">{t("analyticsDescription")}</span>
        </span>
      </label>
      <label className="flex items-start gap-3 rounded-md border border-neutral-200 p-4" title={t("tips.marketing")}>
        <input
          type="checkbox"
          checked={marketing}
          title={t("tips.marketing")}
          onChange={(e) => {
            setMarketing(e.target.checked);
            setSaved(false);
          }}
          className="mt-1"
        />
        <span>
          <span className="block text-sm font-medium">{t("marketingTitle")}</span>
          <span className="block text-sm text-neutral-500">{t("marketingDescription")}</span>
        </span>
      </label>
      <button
        onClick={save}
        title={t("tips.saveChoices")}
        className="rounded-md bg-neutral-900 px-4 py-2 text-sm font-medium text-white"
      >
        {t("saveChoices")}
      </button>
      {saved && (
        <p role="status" className="text-sm text-green-700">
          {t("saved")}
        </p>
      )}
    </div>
  );
}
