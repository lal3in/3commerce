import { test, expect, type Page, type APIRequestContext } from "@playwright/test";

/**
 * ADR-0052: entering a coupon code at checkout applies its promotion — the code-gated promotion is
 * invisible until the shopper types the code, then it renders as its own discount row with the item
 * totals adjusted. An unknown code gets a specific, actionable error instead of a blanket "invalid".
 *
 * The promotion is created through the admin API and DEACTIVATED in a finally block, so sibling specs
 * see the demo store's baseline totals — a leftover promotion silently changes every other spec's money.
 * Requires dev-up --data full (demo stores); skips otherwise.
 */
const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";
const TENANT_ID = "00000000-0000-0000-0000-000000000001";
const ADMIN = { email: "admin@3commerce.local", password: "dev-admin-password-1" };

test.describe("Coupon codes at checkout (ADR-0052)", () => {
  test("a code applies its discount, and an unknown code says why", async ({ page, request }) => {
    const store = await resolveDemo(request);
    test.skip(!store, "no demo storefront published (needs dev-up --data full)");
    const { slug, id: storefrontId, currency } = store!;

    await adminLogin(request);

    // A unique code per run so a re-run can never collide with the tenant-unique index, and a 1-unit
    // quantity threshold so ANY cart on this store qualifies — the point of the spec is the coupon gate,
    // not the threshold arithmetic (covered exhaustively by the unit + money-flow tests).
    const code = `E2E${Date.now()}`;
    const promotionName = `E2E coupon ${code}`;
    const promotionId = await createCoupon(request, { name: promotionName, code, currency, storefrontId });

    try {
      await page.goto(`/${slug}`);
      await addFirstInStockProduct(page); // lands on /cart

      // No code entered: the coupon is code-gated, so it must NOT appear on the checkout summary.
      await page.goto("/checkout");
      await expect(page.getByTestId("coupon-box")).toBeVisible();
      await expect(page.getByText(new RegExp(`Promotion: ${escapeRegExp(promotionName)}`))).toHaveCount(0);

      // A code that does not exist gets its OWN reason, not a generic failure.
      await applyCoupon(page, "NOSUCHCODE");
      await expect(page.getByTestId("coupon-error")).toContainText(/don'?t recognise/i);
      await expect(page.getByText(new RegExp(`Promotion: ${escapeRegExp(promotionName)}`))).toHaveCount(0);

      // The real code applies. Reloading is the wait for the Catalog -> Ordering projection: once the
      // applied banner renders, /cart/summary has seen the projected PromotionCopy and priced with it.
      await expect
        .poll(
          async () => {
            await applyCoupon(page, code);
            return page.getByTestId("coupon-applied").count();
          },
          { timeout: 30_000, intervals: [1_000, 2_000, 2_000, 3_000] },
        )
        .toBeGreaterThan(0);

      await expect(page.getByTestId("coupon-applied")).toContainText(code);
      await expect(page.getByTestId("coupon-error")).toHaveCount(0);
      // Shown == charged: the coupon's promotion is now a discount row on the checkout summary.
      const promotionRow = page.getByText(new RegExp(`^Promotion: ${escapeRegExp(promotionName)}`)).first();
      await expect(promotionRow).toBeVisible({ timeout: 10_000 });
      await page.screenshot({ path: "test-results/coupon-checkout.png", fullPage: true });

      // Removing it prices the cart back at full price and re-offers the input.
      await page.getByTestId("coupon-remove").click();
      await expect(page.getByTestId("coupon-input")).toBeVisible();
      await expect(page.getByText(new RegExp(`Promotion: ${escapeRegExp(promotionName)}`))).toHaveCount(0);
    } finally {
      await deactivatePromotion(request, promotionId);
    }
  });
});

/** Types a code into the coupon box and submits it (a GET form to /checkout?coupon=…). */
async function applyCoupon(page: Page, code: string): Promise<void> {
  await page.goto("/checkout");
  await page.getByTestId("coupon-input").fill(code);
  await page.getByTestId("coupon-apply").click();
  await page.waitForURL(/\/checkout\?coupon=/);
}

async function adminLogin(request: APIRequestContext): Promise<void> {
  const login = await request.post(`${GATEWAY}/api/identity/login`, { data: ADMIN });
  expect(login.ok()).toBeTruthy();
}

/** Resolve a live demo storefront (eu/us/au): slug, id and currency. Null when none is published. */
async function resolveDemo(request: APIRequestContext): Promise<{ slug: string; id: string; currency: string } | null> {
  for (const slug of ["eu", "us", "au"]) {
    const r = await request.get(`${GATEWAY}/api/catalog/storefronts/public?slug=${slug}`);
    if (r.ok()) {
      const s = (await r.json()) as { id: string; currency: string };
      return { slug, id: s.id, currency: s.currency };
    }
  }
  return null;
}

async function createCoupon(
  request: APIRequestContext,
  coupon: { name: string; code: string; currency: string; storefrontId: string },
): Promise<string> {
  const r = await request.post(`${GATEWAY}/api/catalog/admin/promotions`, {
    data: {
      tenantId: TENANT_ID,
      name: coupon.name,
      currency: coupon.currency,
      // Enums cross HTTP as numbers: 1 = Storefront scope (the whole cart).
      scope: 1,
      storefrontId: coupon.storefrontId,
      minimumAmountMinor: 0,
      minimumQuantity: 1,
      grantsFreeShipping: false,
      percentOff: 10,
      discountAmountMinor: 0,
      combinable: false,
      // The coupon itself: the promotion applies ONLY when this code is entered.
      code: coupon.code,
    },
  });
  expect(r.ok(), `creating the coupon should succeed: ${await r.text()}`).toBeTruthy();
  return ((await r.json()) as { id: string }).id;
}

async function deactivatePromotion(request: APIRequestContext, promotionId: string): Promise<void> {
  await request.put(`${GATEWAY}/api/catalog/admin/promotions/${promotionId}`, { data: { active: false } });
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

// Walks the store's grid for a buyable product; earlier specs in a full-suite run consume stock, so the
// window is deliberately wide.
async function addFirstInStockProduct(page: Page): Promise<void> {
  const links = page.locator('a[href^="/products/"]');
  const count = Math.min(await links.count(), 20);
  for (let i = 0; i < count; i++) {
    await links.nth(i).click();
    const add = page.getByRole("button", { name: /add to cart/i });
    if (await add.isEnabled().catch(() => false)) {
      await add.click();
      await page.waitForURL(/\/cart/);
      return;
    }
    await page.goBack();
  }
  throw new Error(`No in-stock product found in the first ${count} grid items (stock depleted?).`);
}
