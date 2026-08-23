import { test, expect } from "@playwright/test";
import { pinStorefront } from "./fixtures";

// Scope guard for approval-gated availability (DECISION A): the seeded demo storefront must STILL list
// products after a full seed. The demo supplier is activated by scripts/dev-dummy-data.sh, so every seeded
// offer resolves to an approved supplier and the catalog stays visible — the gate hides a product only when
// its ONLY covering offer is unapproved. Captures a screenshot for the PR evidence. Skips when the demo
// storefronts aren't seeded (e.g. importer-only CI), like the sibling storefront specs.
test("Demo storefront still lists products after seed (approval scope guard)", async ({ page }, testInfo) => {
  const pinned = await pinStorefront(page);
  test.skip(!pinned, "demo storefronts not seeded (needs dev-up --data full)");

  const response = await page.goto("/eu");
  expect(response?.status()).toBe(200);
  await expect(page.getByRole("heading", { name: "Featured" })).toBeVisible();
  const firstProduct = page.locator('a[href^="/products/"]').first();
  await expect(firstProduct).toBeVisible();

  const dir = process.env.SCREENSHOT_DIR;
  if (dir) {
    await page.screenshot({ path: `${dir}/storefront-eu-listing.png`, fullPage: true });
  }
  await testInfo.attach("storefront-eu-listing", { body: await page.screenshot({ fullPage: true }), contentType: "image/png" });
});
