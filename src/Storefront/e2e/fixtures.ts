import { test as base, expect, type Page } from "@playwright/test";

// rev_5/F5: the bare storefront root lists nothing until a storefront is pinned — locally that happens
// by entering a /{slug} demo landing (middleware sets the 3c_storefront cookie), in production by Host.
// Enter each known demo store in turn and return true once the home grid has a published catalog. Returns
// false when no demo store is browsable (e.g. CI seeded via the importer only, no --data full storefronts)
// so specs can test.skip instead of failing on an empty root.
export async function pinStorefront(page: Page): Promise<boolean> {
  for (const slug of ["eu", "au", "us"]) {
    await page.goto(`/${slug}`); // middleware pins the 3c_storefront cookie for the session
    await page.goto("/"); // home is now scoped to that store's published catalog
    if ((await page.locator('a[href^="/products/"]').count()) > 0) {
      return true;
    }
  }
  return false;
}

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
