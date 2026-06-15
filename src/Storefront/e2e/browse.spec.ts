import { test, expect } from "@playwright/test";

// Browsing flows — SSR pages rendering real catalog data through the gateway.
test.describe("Browsing", () => {
  test("home page shows featured products and categories", async ({ page }) => {
    await page.goto("/");
    await expect(page.getByRole("heading", { name: /everything, sourced for you/i })).toBeVisible();
    await expect(page.getByText("Categories")).toBeVisible();
    await expect(page.locator('a[href^="/products/"]').first()).toBeVisible();
  });

  test("search returns relevant results (typo tolerant)", async ({ page }) => {
    await page.goto("/search?q=hedphones");
    await expect(page.getByRole("heading", { name: /results for/i })).toBeVisible();
    // Trigram fallback should still surface headphones.
    await expect(page.locator('a[href^="/products/"]').first()).toBeVisible();
    await expect(page.getByText(/items$/)).toBeVisible();
  });

  test("header search navigates to results", async ({ page }) => {
    await page.goto("/");
    await page.getByPlaceholder("Search products…").fill("speaker");
    await page.getByPlaceholder("Search products…").press("Enter");
    await expect(page).toHaveURL(/\/search\?q=speaker/);
    await expect(page.locator('a[href^="/products/"]').first()).toBeVisible();
  });

  test("product detail page renders price, variants and add-to-cart", async ({ page }) => {
    await page.goto("/search?q=speaker");
    await page.locator('a[href^="/products/"]').first().click();
    await expect(page).toHaveURL(/\/products\//);
    await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
    await expect(page.getByText("Options")).toBeVisible();
    await expect(page.getByRole("button", { name: /add to cart/i })).toBeEnabled();
  });
});
