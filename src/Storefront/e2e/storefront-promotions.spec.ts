import { test, expect, type Page, type APIRequestContext } from "@playwright/test";

/**
 * ADR-0051: a threshold promotion on a demo storefront shows up in the cart as its own row with the
 * discounted items total, and (for a free-shipping promotion) as free shipping. The promotion is created
 * through the admin API, then the cart page is reloaded until the row appears — that poll IS the wait for
 * the Catalog -> Ordering projection, end to end through GET /cart/summary. Afterwards the promotion is
 * DEACTIVATED in a finally block so sibling specs see the baseline — a leftover promotion silently changes
 * every other spec's totals. Requires dev-up --data full (demo stores); skips otherwise.
 */
const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";
const TENANT_ID = "00000000-0000-0000-0000-000000000001";
const ADMIN = { email: "admin@3commerce.local", password: "dev-admin-password-1" };

test.describe("Threshold promotions (ADR-0051)", () => {
  test("cart shows the promotion row, the discounted items total and free shipping", async ({ page, request }) => {
    const store = await resolveDemo(request);
    test.skip(!store, "no demo storefront published (needs dev-up --data full)");
    const { slug, id: storefrontId, currency } = store!;

    await adminLogin(request);

    // A 1-unit quantity threshold so ANY cart on this store qualifies — the point of the spec is the
    // display, not the threshold arithmetic (that is covered exhaustively by the unit + money-flow tests).
    const promotionName = `E2E promo ${Date.now()}`;
    const promotionId = await createPromotion(request, {
      name: promotionName,
      currency,
      storefrontId,
      minimumQuantity: 1,
      percentOff: 10,
      grantsFreeShipping: true,
    });

    try {
      await page.goto(`/${slug}`);
      await addFirstInStockProduct(page); // lands on /cart

      // The Catalog -> Ordering projection is asynchronous, and the cart page is server-rendered
      // (no-store), so the wait is a reload loop: once the promotion row renders, /cart/summary has seen
      // the projected PromotionCopy and evaluated it with the same evaluator checkout uses.
      // Anchored at the start only: the row's <div> holds the label span AND the amount span, so its
      // combined text is "Promotion: <name>−€x.xx" and a trailing $ would never match.
      const promotionText = new RegExp(`^Promotion: ${escapeRegExp(promotionName)}`);
      await expect
        .poll(
          async () => {
            await page.reload();
            return page.getByText(promotionText).count();
          },
          { timeout: 30_000, intervals: [1_000, 2_000, 2_000, 3_000] },
        )
        .toBeGreaterThan(0);

      const promotionRow = page.locator("div", { hasText: promotionText }).last();
      await expect(promotionRow).toBeVisible({ timeout: 10_000 });
      await expect(promotionRow).toContainText("−"); // shown as a deduction
      await expect(page.getByText(/^Items total$/)).toBeVisible();
      await expect(page.getByText(/^Free shipping$/)).toBeVisible();
      await page.screenshot({ path: "test-results/promotion-cart.png", fullPage: true });

      // The rows must ADD UP: subtotal − promotion = items total. GET /cart carries the ADD-TIME catalog
      // price while /cart/summary carries the OFFER-RESOLVED price actually charged, so mixing the two
      // bases silently renders a summary that does not reconcile (regression guard).
      // .first() is the OUTERMOST div whose text starts with "Subtotal" — the summary container holding
      // every row (.last() would be the Subtotal row alone). Only currency-prefixed numbers are read, so
      // the digits in the promotion's generated name are ignored.
      const summaryText = (await page.locator("div", { hasText: /^Subtotal/ }).first().textContent()) ?? "";
      const amounts = [...summaryText.matchAll(/[€$£¥]\s?([\d.,]+)/g)].map((m) => Number(m[1].replace(/,/g, "")));
      expect(amounts.length, `expected subtotal/promotion/items-total amounts in "${summaryText}"`).toBeGreaterThanOrEqual(3);
      const [subtotal, promotionOff] = amounts;
      const itemsTotal = amounts[amounts.length - 1];
      expect(Math.abs(subtotal - promotionOff - itemsTotal)).toBeLessThan(0.02);

      // Shown == charged: the checkout summary carries the same promotion row and free shipping.
      await page.goto("/checkout");
      await expect(page.getByText(new RegExp(`Promotion: ${escapeRegExp(promotionName)}`)).first()).toBeVisible({ timeout: 10_000 });
      await page.screenshot({ path: "test-results/promotion-checkout.png", fullPage: true });
    } finally {
      // Reset: deactivate the promotion so sibling specs see the demo store's baseline totals.
      await deactivatePromotion(request, promotionId);
    }
  });
});

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

async function createPromotion(
  request: APIRequestContext,
  promotion: {
    name: string;
    currency: string;
    storefrontId: string;
    minimumQuantity: number;
    percentOff: number;
    grantsFreeShipping: boolean;
  },
): Promise<string> {
  const r = await request.post(`${GATEWAY}/api/catalog/admin/promotions`, {
    data: {
      tenantId: TENANT_ID,
      name: promotion.name,
      currency: promotion.currency,
      // Enums cross HTTP as numbers: 1 = Storefront scope (the whole cart).
      scope: 1,
      storefrontId: promotion.storefrontId,
      minimumAmountMinor: 0,
      minimumQuantity: promotion.minimumQuantity,
      grantsFreeShipping: promotion.grantsFreeShipping,
      percentOff: promotion.percentOff,
      discountAmountMinor: 0,
      combinable: false,
    },
  });
  expect(r.ok(), `creating the promotion should succeed: ${await r.text()}`).toBeTruthy();
  return ((await r.json()) as { id: string }).id;
}

async function deactivatePromotion(request: APIRequestContext, promotionId: string): Promise<void> {
  await request.put(`${GATEWAY}/api/catalog/admin/promotions/${promotionId}`, { data: { active: false } });
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

// Walks the store's grid for a buyable product. The window is deliberately wider than the sibling
// discount spec's five items: earlier specs in a full-suite run consume stock, so the first few tiles
// are often sold out by the time this spec runs.
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
