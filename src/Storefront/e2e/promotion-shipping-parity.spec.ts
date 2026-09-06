import { test, expect, type Page, type APIRequestContext } from "@playwright/test";

/**
 * ADR-0051 preview parity: what the shopper is SHOWN can never contradict what checkout CHARGES, even
 * though a free-shipping promotion is worth exactly the carrier rate and the rate is not known on a
 * fresh cart.
 *
 * Two promotions race here — free shipping, and a cash discount worth less than the real cross-border
 * rate but more than the flat fallback the preview used to guess. That is precisely the pair that used
 * to flip between the cart and the charge.
 *
 *   1. No address anywhere → the cart says free shipping MAY apply and shows the guaranteed floor. It
 *      never asserts a winner it cannot know.
 *   2. An address entered at checkout → the real carrier rate is quoted, the contest settles on it, and
 *      the cart, the checkout page and GET /cart/summary (which the money tests prove equals the charge)
 *      all show the same thing.
 *
 * Both promotions are deactivated in a finally block: a leftover promotion silently changes every other
 * spec's totals. Requires dev-up --data full (demo stores); skips otherwise.
 */
const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";
const TENANT_ID = "00000000-0000-0000-0000-000000000001";
const ADMIN = { email: "admin@3commerce.local", password: "dev-admin-password-1" };

/** Ordering's PromotionBasis. Enums cross HTTP as NUMBERS (platform invariant). */
const BASIS = { settled: 0, quoted: 1, provisional: 2 } as const;

/**
 * A cash discount deliberately between the two rates that matter: bigger than the old flat fallback
 * (499) so the guess would have picked IT, and smaller than the Fake carrier's cross-border rate
 * (500 + 150/500g + 1500 = 2150) so the real quote picks free shipping instead.
 */
const CASH_DISCOUNT_MINOR = 1_000;

type Summary = {
  subtotalMinor: number;
  storefrontDiscountMinor: number;
  promotionDiscountMinor: number;
  itemsTotalMinor: number;
  freeShippingApplied: boolean;
  appliedPromotions: { promotionId: string; name: string; discountMinor: number }[];
  basis: number;
};

test.describe("Free-shipping preview parity (ADR-0051)", () => {
  test("a cart with no address is provisional, and an entered address settles it on the real rate", async ({ page, request }) => {
    const store = await resolveDemo(request);
    test.skip(!store, "no demo storefront published (needs dev-up --data full)");
    const { slug, id: storefrontId, currency } = store!;

    await adminLogin(request);
    const stamp = Date.now();
    const freeShipName = `E2E free ship ${stamp}`;
    const cashName = `E2E cash off ${stamp}`;
    const freeShipId = await createPromotion(request, {
      name: freeShipName, currency, storefrontId, grantsFreeShipping: true, discountAmountMinor: 0,
    });
    const cashId = await createPromotion(request, {
      name: cashName, currency, storefrontId, grantsFreeShipping: false, discountAmountMinor: CASH_DISCOUNT_MINOR,
    });

    try {
      await page.goto(`/${slug}`);
      await addFirstInStockProduct(page); // lands on /cart

      // ---- 1. No address: the cart refuses to commit ------------------------------------------------
      // The reload loop IS the wait for the Catalog → Ordering projection; once the provisional row
      // renders, /cart/summary has seen both PromotionCopies and found them in genuine contention.
      await expect
        .poll(
          async () => {
            await page.reload();
            return page.getByTestId("free-shipping-provisional").count();
          },
          { timeout: 30_000, intervals: [1_000, 2_000, 2_000, 3_000] },
        )
        .toBeGreaterThan(0);

      await expect(page.getByText(/free shipping may apply/i)).toBeVisible();
      await expect(page.getByText(/add your delivery address at checkout to confirm your offer/i)).toBeVisible();
      // …and never as a decided reward: the plain "Free shipping" row must NOT be there.
      await expect(page.getByText(/^Free shipping$/)).toHaveCount(0);
      await page.screenshot({ path: "test-results/promotion-provisional-cart.png", fullPage: true });

      // The API agrees with the pixels: provisional, free shipping undecided, and the goods discount is
      // the FLOOR (the free-shipping branch's 0), never the cash discount the shopper might not get.
      const provisional = await summary(page, storefrontId);
      expect(provisional.basis).toBe(BASIS.provisional);
      expect(provisional.freeShippingApplied).toBe(false);
      expect(provisional.promotionDiscountMinor).toBe(0);

      // ---- 2. An address settles it on the real carrier rate ----------------------------------------
      await page.goto("/checkout");
      await page.getByLabel("Email").fill(`parity-${stamp}@example.com`);
      const shipping = page.locator("section").filter({ has: page.getByRole("heading", { name: "Shipping address" }) });
      await shipping.getByLabel("Full name").fill("Parity Shopper");
      await shipping.getByLabel("Address").fill("1 Parity Street");
      await shipping.getByLabel("City").fill("Berlin");
      await shipping.getByLabel("Postcode").fill("10115");
      await shipping.getByLabel(/country/i).selectOption("DE");
      await page.getByRole("button", { name: /get shipping rates/i }).click();
      await expect(page.getByText(/standard/i)).toBeVisible();

      // The quote re-prices the page: free shipping now WINS on the real rate, so the shipping line reads
      // free, the cash-discount row is gone, and the provisional hedge has disappeared.
      await expect(page.getByText(/free shipping/i).first()).toBeVisible({ timeout: 15_000 });
      await expect(page.getByText(/free shipping may apply/i)).toHaveCount(0);
      await expect(page.getByText(new RegExp(`Promotion: ${escapeRegExp(cashName)}`))).toHaveCount(0);
      await expect(page.getByText(new RegExp(`Promotion: ${escapeRegExp(freeShipName)}`)).first()).toBeVisible();
      await page.screenshot({ path: "test-results/promotion-quoted-checkout.png", fullPage: true });

      // Shown == charged: the same verdict comes back from /cart/summary scored on the quoted rate — the
      // endpoint the integration suite proves equals what POST /checkout charges.
      const quoted = await summary(page, storefrontId, { shippingMinor: 2_150, shipToCountry: "DE" });
      expect(quoted.basis).toBe(BASIS.quoted);
      expect(quoted.freeShippingApplied).toBe(true);
      expect(quoted.promotionDiscountMinor).toBe(0);
      expect(quoted.appliedPromotions.map((p) => p.promotionId)).toEqual([freeShipId]);
      expect(quoted.itemsTotalMinor).toBe(quoted.subtotalMinor - quoted.storefrontDiscountMinor);

      // The CART now settles too, without the shopper re-entering anything: the destination they just
      // quoted with is remembered, so the cart quotes the same rate and shows the decided reward.
      await page.goto("/cart");
      await expect(page.getByText(/^Free shipping$/)).toBeVisible({ timeout: 15_000 });
      await expect(page.getByTestId("free-shipping-provisional")).toHaveCount(0);
      const settled = await summary(page, storefrontId);
      expect(settled.freeShippingApplied).toBe(true);
      expect(settled.basis).not.toBe(BASIS.provisional);
      await page.screenshot({ path: "test-results/promotion-settled-cart.png", fullPage: true });
    } finally {
      await deactivatePromotion(request, freeShipId);
      await deactivatePromotion(request, cashId);
    }
  });
});

/** The cart summary as the PAGE sees it (shares the page's cart + session cookies). */
async function summary(
  page: Page,
  storefrontId: string,
  shipping?: { shippingMinor: number; shipToCountry: string },
): Promise<Summary> {
  const query = new URLSearchParams({ storefrontId });
  if (shipping) {
    query.set("shippingMinor", String(shipping.shippingMinor));
    query.set("shipToCountry", shipping.shipToCountry);
  }

  const response = await page.request.get(`${GATEWAY}/api/ordering/cart/summary?${query.toString()}`);
  expect(response.ok(), `cart summary should succeed: ${await response.text()}`).toBeTruthy();
  return (await response.json()) as Summary;
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

async function createPromotion(
  request: APIRequestContext,
  promotion: {
    name: string;
    currency: string;
    storefrontId: string;
    grantsFreeShipping: boolean;
    discountAmountMinor: number;
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
      // A 1-unit threshold so ANY cart on this store qualifies — the spec is about which reward wins,
      // not about threshold arithmetic (covered exhaustively by the unit + money-flow tests).
      minimumQuantity: 1,
      grantsFreeShipping: promotion.grantsFreeShipping,
      percentOff: 0,
      discountAmountMinor: promotion.discountAmountMinor,
      // Both EXCLUSIVE, so they compete head to head rather than stacking — the contest the fix is about.
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

// Walks the store's grid for a buyable product (earlier specs in a full-suite run consume stock, so the
// first few tiles are often sold out by the time this spec runs).
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
