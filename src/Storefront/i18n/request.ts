import { cookies, headers } from "next/headers";
import { getRequestConfig } from "next-intl/server";
import { resolveStorefront } from "@/lib/storefront-context";
import {
  DEFAULT_LOCALE,
  LOCALE_COOKIE,
  LOCALES,
  matchAcceptLanguage,
  matchLocale,
  type AbstractMessages,
  type LocaleCode,
} from "./locales";

/**
 * Per-request locale + messages (i18n_1). Resolution order — first hit wins:
 *   1. `3c_locale` cookie — the shopper's explicit choice for THIS session (header switcher).
 *   2. The active storefront's `defaultLanguage` (Catalog config, i18n_0) — what the tenant ships.
 *   3. `Accept-Language` — the browser's preference, when the storefront hasn't spoken.
 *   4. "en".
 * There is no locale URL segment: /au, /eu, /products/... keep their existing shape and a storefront's
 * currency/tax routing is untouched (language ≠ money).
 *
 * NOTE this makes the root layout cookie-dependent (i.e. dynamic). components.md §1 says to avoid
 * reading cookies in layouts — accepted deliberately here: a session-scoped language with no URL
 * segment cannot be resolved anywhere else, and every shopper-facing page in this app already reads
 * cookies (cart/session/storefront) and renders dynamically.
 */
export default getRequestConfig(async () => {
  const locale = await resolveLocale();
  return { locale, messages: await loadMessages(locale) };
});

async function resolveLocale(): Promise<LocaleCode> {
  const cookieStore = await cookies();
  const chosen = matchLocale(cookieStore.get(LOCALE_COOKIE)?.value);
  if (chosen) return chosen;

  // The tenant's default for this storefront. Never let a gateway hiccup 500 a page over a *language*:
  // fall through to the browser preference instead.
  try {
    const storefront = await resolveStorefront();
    const storefrontDefault = matchLocale(storefront?.defaultLanguage);
    if (storefrontDefault) return storefrontDefault;
  } catch {
    /* storefront config unavailable — locale is not worth failing a render over */
  }

  const headerStore = await headers();
  return matchAcceptLanguage(headerStore.get("accept-language")) ?? DEFAULT_LOCALE;
}

/**
 * English is the base catalog: a non-en catalog is deep-merged OVER it, so a partially translated
 * locale renders its translated keys and falls back to English everywhere else — never a raw key.
 * That is what makes dropping in `messages/zh.json` (or a half-finished one) safe.
 */
async function loadMessages(locale: LocaleCode): Promise<AbstractMessages> {
  const base = (await LOCALES[0].messages()).default;
  if (locale === DEFAULT_LOCALE) return base;

  const definition = LOCALES.find((l) => l.code === locale);
  if (!definition) return base;
  return deepMerge(base, (await definition.messages()).default);
}

function deepMerge(base: AbstractMessages, override: AbstractMessages): AbstractMessages {
  const merged: AbstractMessages = { ...base };
  for (const [key, value] of Object.entries(override)) {
    const existing = merged[key];
    merged[key] =
      typeof value === "object" && typeof existing === "object"
        ? deepMerge(existing, value)
        : value;
  }

  return merged;
}
