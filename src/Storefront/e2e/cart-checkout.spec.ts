import { test, expect } from "@playwright/test";

// The money journey in a real browser: add to cart → checkout → confirmation.
test.describe("Cart & checkout", () => {
  test("add a product to the cart and see it listed", async ({ page }) => {
    await page.goto("/search?q=keyboard");
    await page.locator('a[href^="/products/"]').first().click();
    await page.getByRole("button", { name: /add to cart/i }).click();

    await expect(page).toHaveURL(/\/cart/);
    await expect(page.getByRole("heading", { name: /your cart/i })).toBeVisible();
    await expect(page.getByText("Subtotal")).toBeVisible();
    await expect(page.getByRole("link", { name: /checkout/i })).toBeVisible();
  });

  test("complete a guest checkout end to end (test payment)", async ({ page }) => {
    await page.goto("/search?q=lamp");
    await page.locator('a[href^="/products/"]').first().click();
    await page.getByRole("button", { name: /add to cart/i }).click();
    await expect(page).toHaveURL(/\/cart/);

    await page.getByRole("link", { name: /checkout/i }).click();
    await expect(page).toHaveURL(/\/checkout/);
    await page.getByLabel("Email").fill(`shopper-${Date.now()}@example.com`);
    await page.getByLabel("Full name").fill("Test Shopper");
    await page.getByLabel("Address").fill("1 Test Street");
    await page.getByLabel("City").fill("Berlin");
    await page.getByLabel("Postcode").fill("10115");
    await page.getByLabel(/country/i).fill("DE");
    await page.getByRole("button", { name: /place order/i }).click();

    // Confirmation page: pending → complete the simulated payment → confirmed.
    await expect(page).toHaveURL(/\/checkout\/confirmation/);
    await page.getByRole("button", { name: /complete test payment/i }).click();
    await expect(page.getByRole("heading", { name: /thank you/i })).toBeVisible({ timeout: 25_000 });
    await expect(page.getByText(/your order is confirmed/i)).toBeVisible();
    // FR-7: confirmation offers guest→account conversion, email pre-filled.
    await expect(page.getByRole("button", { name: /create account/i })).toBeVisible();
  });

  test("empty cart shows the empty state", async ({ page, context }) => {
    await context.clearCookies();
    await page.goto("/cart");
    await expect(page.getByRole("heading", { name: /your cart is empty/i })).toBeVisible();
  });
});
