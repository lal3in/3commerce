import { test, expect } from "./fixtures";

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
    // The checkout has shipping + billing sections; scope to the shipping section by its HEADING so
    // labels are unambiguous. (hasText: "Shipping address" also matches the billing section, which
    // reads "Using the shipping address for card billing…", pulling in its "Billing address" select.)
    const shipping = page.locator("section").filter({ has: page.getByRole("heading", { name: "Shipping address" }) });
    await shipping.getByLabel("Full name").fill("Test Shopper");
    await shipping.getByLabel("Address").fill("1 Test Street");
    await shipping.getByLabel("City").fill("Berlin");
    await shipping.getByLabel("Postcode").fill("10115");
    await shipping.getByLabel(/country/i).fill("DE");
    await page.getByRole("button", { name: /get shipping rates/i }).click();
    await expect(page.getByText(/standard/i)).toBeVisible();
    await page.getByRole("button", { name: /authorize & place order/i }).click();

    // Confirmation page: pending → complete the simulated payment → confirmed.
    await expect(page).toHaveURL(/\/checkout\/confirmation/);
    await page.getByRole("button", { name: /complete test payment/i }).click();
    await expect(page.getByRole("heading", { name: /thank you/i })).toBeVisible({ timeout: 25_000 });
    await expect(page.getByText(/your order is confirmed/i)).toBeVisible();
    // FR-7: confirmation offers guest→account conversion, email pre-filled.
    await expect(page.getByRole("button", { name: /create account/i })).toBeVisible();
  });

  // pay_6 regression: payment-method tiles are visually-hidden radios styled from React state. A
  // click must (a) visibly select the tile, (b) survive even when the native radio was already
  // checked (pre-hydration click), and (c) carry the chosen option through the checkout POST.
  test("select a non-default payment method and check out with it", async ({ page }) => {
    await page.goto("/search?q=lamp");
    await page.locator('a[href^="/products/"]').first().click();
    await page.getByRole("button", { name: /add to cart/i }).click();
    await expect(page).toHaveURL(/\/cart/);

    await page.getByRole("link", { name: /checkout/i }).click();
    await expect(page).toHaveURL(/\/checkout/);

    // Default state: Credit card selected, card-entry fields shown.
    const group = page.getByRole("radiogroup", { name: "Payment options" });
    await expect(group.getByRole("radio", { name: "Credit card" })).toBeChecked();
    await expect(page.getByLabel("Card number")).toBeVisible();

    // Click the PayPal tile: radio checks, tile shows the selected border, card fields hide.
    const paypalTile = group.locator('label[aria-label="PayPal"]');
    await paypalTile.click();
    await expect(group.getByRole("radio", { name: "PayPal" })).toBeChecked();
    await expect(paypalTile).toHaveAttribute("data-selected", "true");
    await expect(page.getByLabel("Card number")).toBeHidden();

    // Re-clicking the already-checked tile must not desync anything (change doesn't fire; click does).
    await paypalTile.click();
    await expect(paypalTile).toHaveAttribute("data-selected", "true");

    // Capture the checkout POST's server-action payload and finish the order with PayPal selected.
    await page.getByLabel("Email").fill(`shopper-paypal-${Date.now()}@example.com`);
    const shipping = page.locator("section").filter({ has: page.getByRole("heading", { name: "Shipping address" }) });
    await shipping.getByLabel("Full name").fill("Wallet Shopper");
    await shipping.getByLabel("Address").fill("2 Wallet Way");
    await shipping.getByLabel("City").fill("Berlin");
    await shipping.getByLabel("Postcode").fill("10115");
    await shipping.getByLabel(/country/i).fill("DE");
    await page.getByRole("button", { name: /get shipping rates/i }).click();
    await expect(page.getByText(/standard/i)).toBeVisible();

    const checkoutPost = page.waitForRequest((request) =>
      request.method() === "POST" && new URL(request.url()).pathname === "/checkout");
    await page.getByRole("button", { name: /authorize & place order/i }).click();
    // The server action receives the form multipart body; the chosen option must be in it.
    expect((await checkoutPost).postData()).toContain("PayPal");

    await expect(page).toHaveURL(/\/checkout\/confirmation/);
    await page.getByRole("button", { name: /complete test payment/i }).click();
    await expect(page.getByRole("heading", { name: /thank you/i })).toBeVisible({ timeout: 25_000 });
  });

  test("empty cart shows the empty state", async ({ page, context }) => {
    await context.clearCookies();
    await page.goto("/cart");
    await expect(page.getByRole("heading", { name: /your cart is empty/i })).toBeVisible();
  });
});
