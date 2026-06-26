import { test } from "./fixtures";

// Captures storefront screenshots for the Operations Wiki. Run against a live stack:
//   GATEWAY_URL=http://localhost:8080 npm run test:e2e -- --project=storefront -g screenshots
const OUT = "../../docs/help/assets/screenshots";

async function shot(page: import("@playwright/test").Page, name: string) {
  await page.waitForLoadState("networkidle").catch(() => {});
  await page.waitForTimeout(400);
  await page.screenshot({ path: `${OUT}/storefront-${name}.png`, fullPage: true });
}

test.describe("storefront screenshots", () => {
  test("capture pages", async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 1000 });

    await page.goto("/");
    await shot(page, "home");

    await page.goto("/search?q=speaker");
    await shot(page, "search");

    await page.locator('a[href^="/products/"]').first().click();
    await page.waitForURL(/\/products\//);
    await shot(page, "product");

    await page.getByRole("button", { name: /add to cart/i }).click().catch(() => {});
    await page.waitForTimeout(600);
    await page.goto("/cart");
    await shot(page, "cart");

    await page.goto("/checkout");
    await shot(page, "checkout");

    await page.goto("/login");
    await shot(page, "login");

    await page.goto("/register");
    await shot(page, "register");

    await page.goto("/account");
    await shot(page, "account");
  });
});
