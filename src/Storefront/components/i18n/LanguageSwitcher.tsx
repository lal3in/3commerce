"use client";

import { useTransition } from "react";
import { useLocale, useTranslations } from "next-intl";
import { useRouter } from "next/navigation";
import { LOCALES } from "@/i18n/locales";
import { setLocale } from "@/lib/locale-actions";

/**
 * Header language switcher (i18n_1). Client leaf: the only interactive part of the header
 * (components.md §1). Lists exactly the locales that HAVE a message catalog (i18n/locales.ts) —
 * register a new one there and it appears here with no change to this file.
 *
 * Switching writes the `3c_locale` session cookie via a Server Action and re-renders the tree. It is
 * independent of the `3c_storefront` currency/tax context: language carries no financial meaning.
 */
export function LanguageSwitcher() {
  const t = useTranslations("header");
  const locale = useLocale();
  const router = useRouter();
  const [pending, start] = useTransition();

  return (
    <label htmlFor="locale" className="flex items-center gap-1" title={t("tips.language")}>
      <span className="sr-only">{t("language")}</span>
      <select
        id="locale"
        name="locale"
        value={locale}
        disabled={pending}
        aria-label={t("language")}
        aria-describedby="locale-tip"
        title={t("tips.language")}
        onChange={(event) =>
          start(async () => {
            await setLocale(event.target.value);
            router.refresh();
          })
        }
        className="rounded-md border border-neutral-300 bg-transparent px-2 py-1 text-sm disabled:opacity-50"
      >
        {LOCALES.map((option) => (
          <option key={option.code} value={option.code} lang={option.code}>
            {option.label}
          </option>
        ))}
      </select>
      <span id="locale-tip" className="sr-only">
        {t("tips.language")}
      </span>
    </label>
  );
}
