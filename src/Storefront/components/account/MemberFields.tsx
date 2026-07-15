"use client";

// Structured member profile inputs (mem_1) reused by registration, the post-checkout account
// offer (pre-filled from the order), and the account profile editor. Title/Middle/Preferred are
// optional; First/Last/Phone/DOB are the required member details for recurrent-payment services.
// i18n_1: labels + tooltips come from the `member` catalog; every field carries both a `title`
// (hover) and an sr-only `aria-describedby` help text.

import { useTranslations } from "next-intl";

export const TITLES = ["", "Mr", "Mrs", "Ms", "Miss", "Mx", "Dr", "Prof"] as const;

export type MemberDefaults = {
  title?: string | null;
  firstName?: string | null;
  middleName?: string | null;
  lastName?: string | null;
  preferredName?: string | null;
  phone?: string | null;
  dateOfBirth?: string | null;
  marketingConsent?: boolean | null;
};

const field = "mt-1 w-full rounded-md border border-neutral-300 px-3 py-2 text-sm";

export function MemberFields({ defaults }: { defaults?: MemberDefaults }) {
  const t = useTranslations("member");
  const tc = useTranslations("common");
  return (
    <div className="space-y-3">
      <div className="grid grid-cols-3 gap-3">
        <div>
          <label htmlFor="title" className="block text-sm font-medium" title={t("tips.title")}>
            {t("title")}
          </label>
          <select
            id="title"
            name="title"
            defaultValue={defaults?.title ?? ""}
            title={t("tips.title")}
            aria-describedby="title-tip"
            className={field}
          >
            {TITLES.map((option) => (
              <option key={option} value={option}>
                {option || t("titleNone")}
              </option>
            ))}
          </select>
          <span id="title-tip" className="sr-only">{t("tips.title")}</span>
        </div>
        <div className="col-span-2">
          <label htmlFor="firstName" className="block text-sm font-medium" title={t("tips.firstName")}>
            {t("firstName")} <span className="text-red-600">*</span>
          </label>
          <input
            id="firstName"
            name="firstName"
            required
            autoComplete="given-name"
            defaultValue={defaults?.firstName ?? ""}
            title={t("tips.firstName")}
            aria-describedby="firstName-tip"
            className={field}
          />
          <span id="firstName-tip" className="sr-only">{t("tips.firstName")}</span>
        </div>
      </div>
      <div className="grid grid-cols-2 gap-3">
        <div>
          <label htmlFor="middleName" className="block text-sm font-medium" title={t("tips.middleName")}>
            {t("middleName")}
          </label>
          <input
            id="middleName"
            name="middleName"
            autoComplete="additional-name"
            defaultValue={defaults?.middleName ?? ""}
            title={t("tips.middleName")}
            aria-describedby="middleName-tip"
            className={field}
          />
          <span id="middleName-tip" className="sr-only">{t("tips.middleName")}</span>
        </div>
        <div>
          <label htmlFor="lastName" className="block text-sm font-medium" title={t("tips.lastName")}>
            {t("lastName")} <span className="text-red-600">*</span>
          </label>
          <input
            id="lastName"
            name="lastName"
            required
            autoComplete="family-name"
            defaultValue={defaults?.lastName ?? ""}
            title={t("tips.lastName")}
            aria-describedby="lastName-tip"
            className={field}
          />
          <span id="lastName-tip" className="sr-only">{t("tips.lastName")}</span>
        </div>
      </div>
      <div>
        <label htmlFor="preferredName" className="block text-sm font-medium" title={t("tips.preferredName")}>
          {t("preferredName")} <span className="text-neutral-400">{tc("optional")}</span>
        </label>
        <input
          id="preferredName"
          name="preferredName"
          autoComplete="nickname"
          defaultValue={defaults?.preferredName ?? ""}
          title={t("tips.preferredName")}
          aria-describedby="preferredName-tip"
          className={field}
        />
        <span id="preferredName-tip" className="sr-only">{t("tips.preferredName")}</span>
      </div>
      <div className="grid grid-cols-2 gap-3">
        <div>
          <label htmlFor="phone" className="block text-sm font-medium" title={t("tips.phone")}>
            {t("phone")} <span className="text-red-600">*</span>
          </label>
          <input
            id="phone"
            name="phone"
            type="tel"
            required
            autoComplete="tel"
            defaultValue={defaults?.phone ?? ""}
            title={t("tips.phone")}
            aria-describedby="phone-tip"
            className={field}
          />
          <span id="phone-tip" className="sr-only">{t("tips.phone")}</span>
        </div>
        <div>
          <label htmlFor="dateOfBirth" className="block text-sm font-medium" title={t("tips.dateOfBirth")}>
            {t("dateOfBirth")} <span className="text-red-600">*</span>
          </label>
          <input
            id="dateOfBirth"
            name="dateOfBirth"
            type="date"
            required
            defaultValue={defaults?.dateOfBirth ?? ""}
            title={t("tips.dateOfBirth")}
            aria-describedby="dateOfBirth-tip"
            className={field}
          />
          <span id="dateOfBirth-tip" className="sr-only">{t("tips.dateOfBirth")}</span>
        </div>
      </div>
      <label className="flex items-center gap-2 text-sm" title={t("tips.marketingConsent")}>
        <input
          type="checkbox"
          name="marketingConsent"
          defaultChecked={defaults?.marketingConsent ?? false}
          title={t("tips.marketingConsent")}
          className="h-4 w-4"
        />
        {t("marketingConsent")}
      </label>
    </div>
  );
}
