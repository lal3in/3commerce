import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { ConsentSettings } from "./ConsentSettings";

export const metadata: Metadata = { title: "Privacy & cookie settings" };

// Consent-settings page (def_4 / mt5_5): lets a shopper change their cookie choices at any
// time — the banner only appears until the first decision, this page is the permanent home.
export default async function PrivacyPage() {
  const t = await getTranslations("privacy");
  return (
    <main className="mx-auto max-w-2xl px-4 py-10">
      <h1 className="text-2xl font-semibold">{t("title")}</h1>
      <p className="mt-2 text-sm text-neutral-600">{t("intro")}</p>
      <ConsentSettings />
    </main>
  );
}
