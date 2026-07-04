import type { Metadata } from "next";
import { ConsentSettings } from "./ConsentSettings";

export const metadata: Metadata = { title: "Privacy & cookie settings" };

// Consent-settings page (def_4 / mt5_5): lets a shopper change their cookie choices at any
// time — the banner only appears until the first decision, this page is the permanent home.
export default function PrivacyPage() {
  return (
    <main className="mx-auto max-w-2xl px-4 py-10">
      <h1 className="text-2xl font-semibold">Privacy &amp; cookie settings</h1>
      <p className="mt-2 text-sm text-neutral-600">
        Necessary cookies keep the store working (cart, checkout, sign-in) and are always on. Analytics and
        marketing are optional, off by default, and can be changed here at any time. Withdrawing analytics
        consent also deletes the first-party visitor id from this browser.
      </p>
      <ConsentSettings />
    </main>
  );
}
