import { test, expect, pinStorefront } from "./fixtures";

// Browsing flows — SSR pages rendering real catalog data through the gateway. rev_5: the catalog is
// only visible inside a pinned storefront, and per-store publications are small/curated, so search
// terms are derived from the store's OWN catalog rather than hard-coded (speaker/headphones may not be
// published). Skips when no demo store with a catalog is available (e.g. import-only CI).

// A concrete, matchable query taken from the pinned store's own catalog: the longest word of the first
// product's title (the ProductCard exposes it as the anchor's `title` attribute).
async function storeSearchTerm(page: import("@playwright/test").Page): Promise<string> {
  await page.goto("/search"); // the store's full published catalog (no query)
  const link = page.locator('a[href^="/products/"]').first();
  await expect(link).toBeVisible();
  const title = (await link.getAttribute("title")) ?? "";
  const words = title.split(/\s+/).filter((w) => /^[A-Za-z]{4,}$/.test(w)).sort((a, b) => b.length - a.length);
  return words[0] ?? "the";
}

test.describe("Browsing", () => {
  test.beforeEach(async ({ page }) => {
    test.skip(!(await pinStorefront(page)), "no demo storefront with a published catalog (needs --data full)");
  });

  test("home page shows featured products and categories", async ({ page }) => {
    await page.goto("/");
    await expect(page.getByRole("heading", { name: /everything, sourced for you/i })).toBeVisible();
    await expect(page.getByText("Categories")).toBeVisible();
    await expect(page.locator('a[href^="/products/"]').first()).toBeVisible();
  });

  test("search returns results for a term in the store's catalog", async ({ page }) => {
    const term = await storeSearchTerm(page);
    await page.goto(`/search?q=${encodeURIComponent(term)}`);
    await expect(page.getByRole("heading", { name: /results for/i })).toBeVisible();
    await expect(page.locator('a[href^="/products/"]').first()).toBeVisible();
    await expect(page.getByText(/items$/)).toBeVisible();
  });

  test("header search navigates to results", async ({ page }) => {
    const term = await storeSearchTerm(page);
    await page.goto("/");
    await page.getByPlaceholder("Search products…").fill(term);
    await page.getByPlaceholder("Search products…").press("Enter");
    await expect(page).toHaveURL(new RegExp(`/search\\?q=${term}`, "i"));
    await expect(page.locator('a[href^="/products/"]').first()).toBeVisible();
  });

  test("product detail page renders price, variants and add-to-cart", async ({ page }) => {
    await page.goto("/");
    await page.locator('a[href^="/products/"]').first().click();
    await expect(page).toHaveURL(/\/products\//);
    await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
    await expect(page.getByText("Options")).toBeVisible();
    await expect(page.getByRole("button", { name: /add to cart/i })).toBeEnabled();
  });
});
