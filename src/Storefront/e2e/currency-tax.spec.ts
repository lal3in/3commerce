import { test, expect, type Page } from "@playwright/test";

/**
 * Storefront currency + tax journey (review remediation rev_12, ADR-0038): entering /au pins the
 * AU storefront (middleware cookie), main routes price in A$, and checkout shows the contained-GST
 * line (tax-INCLUSIVE — the shelf price is the charge). Requires dev-up --data full (AU/EU/US
 * demo storefronts + per-currency seeded prices); skips where that seed is absent (CI's
 * browser-e2e boots via the importer only — no demo storefronts).
 */
test.describe("Storefront currency + tax (ADR-0038)", () => {
  test("entering /au pins the storefront and main routes price in A$", async ({ page }) => {
    test.skip(!(await auStorefrontSeeded(page)), "AU demo storefront not seeded (needs --data full)");
    await expect(page.getByText(/AU GST \(10%\)/)).toBeVisible(); // hero renders the real config

    // The cookie now scopes the MAIN routes: home grid prices render in A$ (en-US formatMoney).
    await page.goto("/");
    const firstPrice = page.locator("p.font-semibold", { hasText: /A\$/ }).first();
    await expect(firstPrice).toBeVisible();
  });

  test("AU checkout shows the contained-GST line (price includes tax)", async ({ page }) => {
    test.skip(!(await auStorefrontSeeded(page)), "AU demo storefront not seeded (needs --data full)");
    await addFirstInStockProduct(page);
    await page.goto("/checkout");

    // Inclusive regime: informational line, not an addition (ADR-0038).
    await expect(page.getByText(/Includes tax \(10%\)/)).toBeVisible();
    await expect(page.getByText(/^Tax \(added\)$/)).toHaveCount(0);
  });
});

/** Lands on /au and reports whether the AU demo storefront (--data full seed) is configured. */
async function auStorefrontSeeded(page: Page): Promise<boolean> {
  await page.goto("/au");
  try {
    await page.getByText(/AU GST \(10%\)/).waitFor({ timeout: 5_000 });
    return true;
  } catch {
    return false;
  }
}

async function addFirstInStockProduct(page: Page): Promise<void> {
  // The grid can contain out-of-stock items (seeded stock is randomized) — try a few PDPs.
  const links = page.locator('a[href^="/products/"]');
  const count = Math.min(await links.count(), 5);
  for (let i = 0; i < count; i++) {
    await links.nth(i).click();
    const add = page.getByRole("button", { name: /add to cart/i });
    if (await add.isEnabled()) {
      await add.click();
      await page.waitForURL(/\/cart/);
      return;
    }

    await page.goBack();
  }

  throw new Error("No in-stock product found in the first five grid items.");
}
