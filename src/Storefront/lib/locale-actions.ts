"use server";

import { cookies } from "next/headers";
import { revalidatePath } from "next/cache";
import { isSupportedLocale, LOCALE_COOKIE } from "@/i18n/locales";

/**
 * Set the shopper's session language (i18n_1). A per-session override of the storefront's
 * `defaultLanguage` — it changes UI text ONLY: currency, tax, prices, and the `3c_storefront`
 * routing cookie are untouched.
 *
 * Not httpOnly on purpose: it carries no authority (an unknown value is ignored below) and staying
 * readable lets client code reflect the current language without a round-trip.
 */
export async function setLocale(code: string): Promise<void> {
  if (!isSupportedLocale(code)) {
    return; // unknown/tampered value: keep the resolved locale rather than fail the request
  }

  const cookieStore = await cookies();
  cookieStore.set(LOCALE_COOKIE, code, {
    path: "/",
    sameSite: "lax",
    maxAge: 60 * 60 * 24 * 365,
  });
  // Every page's text comes from the request locale, so the whole tree is stale after a switch.
  revalidatePath("/", "layout");
}
