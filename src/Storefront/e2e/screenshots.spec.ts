import { test } from "./fixtures";
import { pinStorefront } from "./fixtures";
import { capture } from "../e2e-support/screenshots";

// Captures storefront screenshots for the Operations Wiki into the run archive (never straight into the
// wiki). Run against a live stack, then promote the run once the images look right:
//   GATEWAY_URL=http://localhost:8080 npm run test:e2e -- --project=storefront -g screenshots
//   node ../../scripts/screenshots/shots.cjs promote <runId>

async function shot(page: import("@playwright/test").Page, name: string) {
  await page.waitForLoadState("networkidle").catch(() => {});
  await page.waitForTimeout(400);
  await capture(page, name, { docName: `storefront-${name}.png` });
}

test.describe("storefront screenshots", () => {
  test("capture pages", async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 1000 });

    // Pin a demo storefront so the catalog renders (rev_5); skip when none is browsable.
    test.skip(!(await pinStorefront(page)), "no demo storefront with a published catalog (needs --data full)");

    await page.goto("/");
    await shot(page, "home");

    // The store's full catalog (no query term, so it never depends on a specific product being published).
    await page.goto("/search");
    await shot(page, "search");

    await page.goto("/");
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
