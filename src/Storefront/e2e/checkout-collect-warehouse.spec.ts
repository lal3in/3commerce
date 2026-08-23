import { test, expect, pinStorefront } from "./fixtures";

// "Collect at warehouse" in a real browser: a warehouse-fulfilled product can be collected from the
// supplier's warehouse instead of shipped — the shipping row shows Free, no carrier rate is fetched,
// and the order still confirms. Needs the demo warehouse product + a projected warehouse address
// (dev-up --data full seeds the demo supplier's Warehouse address, which projects into Ordering).
test.describe("Collect at warehouse checkout", () => {
  test("choose collect at warehouse and pay no shipping", async ({ page }) => {
    test.skip(!(await pinStorefront(page)), "no demo storefront with a published catalog (needs --data full)");

    await page.locator('a[href*="physical-warehouse"]').first().click();
    await page.getByRole("button", { name: /add to cart/i }).click();
    await expect(page).toHaveURL(/\/cart/);

    await page.getByRole("link", { name: /checkout/i }).click();
    await expect(page).toHaveURL(/\/checkout/);

    await page.getByLabel("Email").fill(`collect-${Date.now()}@example.com`);
    const shipping = page.locator("section").filter({ has: page.getByRole("heading", { name: "Shipping address" }) });
    await shipping.getByLabel("Full name").fill("Collect Shopper");
    await shipping.getByLabel("Address").fill("1 Test Street");
    await shipping.getByLabel("City").fill("Berlin");
    await shipping.getByLabel("Postcode").fill("10115");
    await shipping.getByLabel(/country/i).fill("DE");

    // Opt into collect-at-warehouse: the carrier-rate button disappears and shipping shows Free.
    await page.getByText(/collect at warehouse \(free\)/i).click();
    await expect(page.getByText(/free \(collect at warehouse\)/i)).toBeVisible();
    await expect(page.getByRole("button", { name: /get shipping rates/i })).toBeHidden();

    await page.getByRole("button", { name: /authorize & place order/i }).click();

    await expect(page).toHaveURL(/\/checkout\/confirmation/);
    await page.getByRole("button", { name: /complete test payment/i }).click();
    await expect(page.getByRole("heading", { name: /thank you/i })).toBeVisible({ timeout: 25_000 });
    await expect(page.getByText(/your order is confirmed/i)).toBeVisible();
    await page.screenshot({ path: `${process.env.SHOT_DIR ?? "test-results"}/checkout-collect-warehouse.png`, fullPage: true });
  });
});
