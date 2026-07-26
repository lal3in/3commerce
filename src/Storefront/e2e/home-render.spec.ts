import { test, expect, type Page } from "@playwright/test";

/**
 * Home + storefront-scoped listing render (fix/storefront-home-eu-500).
 *
 * Regression: the --data full demo seed gives E2E-scenario products a `placehold.co` image URL.
 * Those products surface in the default (no-cookie) and EUR listings but are filtered out of the
 * AUD/USD storefronts by per-currency pricing. next.config only allow-listed `picsum.photos`, so
 * next/image threw "hostname placehold.co is not configured" at render — 500ing `/` and `/eu`
 * while `/au` and `/us` stayed green. This guards that every listing renders with its product grid
 * regardless of image host.
 *
 * Needs `dev-up --data full` (AU/EU/US demo storefronts). Skips where that seed is absent — CI's
 * browser-e2e boots via the importer only (no demo storefronts), exactly like currency-tax.spec.
 */
test.describe("Storefront home + listing render (placehold.co image host)", () => {
  test("default home `/` renders 200 with the page shell (empty grid until a store is pinned)", async ({ page }) => {
    test.skip(!(await demoStorefrontsSeeded(page)), "AU/EU/US demo storefronts not seeded (needs --data full)");

    // The seed probe visits /au, which pins the AU storefront cookie. Clear it so `/` exercises the
    // no-cookie default path. rev_5: with no storefront pinned the product grid is intentionally EMPTY,
    // but SSR must still render 200 (not the old placehold.co next/image 500). The populated-grid +
    // placehold.co image-host guard lives in the /eu listing below, where those products surface.
    await page.context().clearCookies();
    const response = await page.goto("/");
    expect(response?.status()).toBe(200);
    await expect(page.getByRole("heading", { name: "Featured" })).toBeVisible();
  });

  test("EUR storefront `/eu` renders 200 with a populated product grid", async ({ page }) => {
    test.skip(!(await demoStorefrontsSeeded(page)), "AU/EU/US demo storefronts not seeded (needs --data full)");

    const response = await page.goto("/eu");
    expect(response?.status()).toBe(200);
    await expect(page.getByText(/EU VAT/)).toBeVisible(); // EU storefront pinned by the /eu landing
    await expect(page.getByRole("heading", { name: "Featured" })).toBeVisible();
    await expect(page.locator('a[href^="/products/"]').first()).toBeVisible();
  });
});

/**
 * Lands on /au (which renders even under the regression, since its currency filter hides the
 * placehold.co products) and reports whether the demo storefronts (--data full) are configured.
 * AU/EU/US are seeded together, so this is a bug-independent gate for the /eu assertion.
 */
async function demoStorefrontsSeeded(page: Page): Promise<boolean> {
  await page.goto("/au");
  try {
    await page.getByText(/AU GST \(10%\)/).waitFor({ timeout: 5_000 });
    return true;
  } catch {
    return false;
  }
}
