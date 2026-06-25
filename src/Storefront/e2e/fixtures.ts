import { test as base, expect } from "@playwright/test";

// Storefront e2e fixture (mt5_5): pre-seed a consent decision so the fixed, bottom cookie banner does
// not overlay page controls (e.g. the place-order button) during automated runs. The banner still
// appears for real first-time visitors — this only affects the test browser.
export const test = base.extend({
  page: async ({ page }, use) => {
    await page.addInitScript(() => {
      window.localStorage.setItem(
        "3c_consent",
        JSON.stringify({ necessary: true, analytics: false, marketing: false, decidedAt: new Date().toISOString() }),
      );
    });
    await use(page);
  },
});

export { expect };
