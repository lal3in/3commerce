// The storefront's locale registry (i18n_1).
//
// ADDING A LANGUAGE IS A DROP-IN, TWO STEPS:
//   1. Add `messages/<code>.json` (copy en.json, translate the values — partial is fine: any key you
//      leave out falls back to English, see i18n/request.ts).
//   2. Add one line to LOCALES below: { code, label, messages: () => import("../messages/<code>.json") }.
// Nothing else changes — the header switcher lists exactly the locales registered here, and the
// Catalog `defaultLanguage` of a storefront starts working for that code immediately.
//
// Language is INDEPENDENT of currency/tax: choosing 中文 changes UI text only. Prices keep coming
// from the storefront's currency config and are formatted by lib/money.ts (deliberately locale-fixed
// so a currency renders identically for every shopper — see the note there).

export type LocaleCode = string;

export type LocaleDefinition = {
  /** BCP-47 code, matching the Catalog SupportedLanguages vocabulary. */
  code: LocaleCode;
  /** Endonym — written in its own language, so a shopper who can't read the current UI still finds it. */
  label: string;
  /** Lazy catalog import; only the active locale's messages are loaded per request. */
  messages: () => Promise<{ default: AbstractMessages }>;
};

// Message catalogs are plain nested JSON objects of strings.
export type AbstractMessages = { [key: string]: string | AbstractMessages };

export const DEFAULT_LOCALE = "en";

/** Session locale override, set by the header language switcher (independent of `3c_storefront`). */
export const LOCALE_COOKIE = "3c_locale";

export const LOCALES: LocaleDefinition[] = [
  { code: "en", label: "English", messages: () => import("../messages/en.json") },
  { code: "zh", label: "中文", messages: () => import("../messages/zh.json") },
  { code: "yue", label: "廣東話", messages: () => import("../messages/yue.json") },
  { code: "de", label: "Deutsch", messages: () => import("../messages/de.json") },
  { code: "fr", label: "Français", messages: () => import("../messages/fr.json") },
  { code: "es", label: "Español", messages: () => import("../messages/es.json") },
];

export const LOCALE_CODES: LocaleCode[] = LOCALES.map((l) => l.code);

export function isSupportedLocale(code: string | undefined | null): code is LocaleCode {
  return Boolean(code) && LOCALE_CODES.includes(code as LocaleCode);
}

/**
 * Best match for a BCP-47 tag against the catalogs we actually ship: exact first ("zh-Hant" → "zh-Hant"),
 * then the primary subtag ("zh-Hant" → "zh"). A storefront may be configured with a language we have no
 * catalog for — that is not an error, it just falls through to the next resolution step / English.
 */
export function matchLocale(tag: string | undefined | null): LocaleCode | null {
  if (!tag) return null;
  const normalized = tag.trim().toLowerCase();
  if (!normalized) return null;
  const exact = LOCALE_CODES.find((code) => code.toLowerCase() === normalized);
  if (exact) return exact;
  const primary = normalized.split("-")[0];
  return LOCALE_CODES.find((code) => code.toLowerCase() === primary) ?? null;
}

/** Picks the highest-q Accept-Language entry we have a catalog for (`zh-CN,zh;q=0.9,en;q=0.8`). */
export function matchAcceptLanguage(header: string | null): LocaleCode | null {
  if (!header) return null;
  const ranked = header
    .split(",")
    .map((part) => {
      const [tag, ...params] = part.trim().split(";");
      const q = params.find((p) => p.trim().startsWith("q="));
      return { tag: tag.trim(), q: q ? Number(q.trim().slice(2)) || 0 : 1 };
    })
    .filter((entry) => entry.tag && entry.tag !== "*")
    .sort((a, b) => b.q - a.q);

  for (const entry of ranked) {
    const match = matchLocale(entry.tag);
    if (match) return match;
  }

  return null;
}
