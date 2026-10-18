import { test, expect, type APIRequestContext, type APIResponse, type Page } from "@playwright/test";
import { capture } from "../e2e-support/screenshots";

/**
 * Owner decision 2026-10-15 (renewal_usage_tax_exclusive_decision, ADR-0057 amendment): on a tax-EXCLUSIVE store
 * (US sales tax, ADR-0038) a subscription renewal is charged its price PLUS tax, exactly as checkout adds tax to the
 * first period — so the "Renews monthly at" preview must say "+ tax" rather than show a bare figure the card will
 * not be charged. The demo seed sells no subscription on the US store, so this spec turns one of the US store's own
 * products into a monthly subscription with a store-scoped offer through the admin API, checks the cart and the
 * checkout preview, then DEACTIVATES the offer in a finally block so sibling specs see the baseline. The offer
 * belongs to a supplier this spec creates and approves itself — never the demo supplier, whose cost table
 * (e2e-admin/suppliers.spec.ts) must list each product exactly once. Requires dev-up --data full (the US demo
 * store); skips otherwise.
 */
const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";
const TENANT_ID = "00000000-0000-0000-0000-000000000001";
const ADMIN = { email: "admin@3commerce.local", password: "dev-admin-password-1" };
const MONTHLY = 2; // BillingPeriod.Monthly — enums cross HTTP as numbers
const RENEWAL_PRICE_MINOR = 1000;

test.describe("Renewal preview on an exclusive-tax store (owner decision 2026-10-15)", () => {
  test("cart and checkout say the renewal is charged + tax", async ({ page, request }) => {
    const store = await resolveUsStore(request);
    test.skip(!store, "US demo storefront not seeded or not tax-exclusive (needs dev-up --data full)");
    const { id: storefrontId, currency } = store!;

    await page.goto("/us");
    const productId = await addFirstInStockStoreProduct(page, request, storefrontId, currency);

    await adminLogin(request);
    const offerId = await makeMonthlySubscription(request, productId, storefrontId, currency);
    try {
      // OfferChanged → Ordering's OfferCopy is asynchronous and the cart page is server-rendered (no-store):
      // reload until the renewal row renders — that IS the wait for the projection.
      const cartRow = page.getByTestId(`renewal-${MONTHLY}`);
      await expect
        .poll(
          async () => {
            await page.goto("/cart");
            return cartRow.count();
          },
          { timeout: 30_000, intervals: [1_000, 2_000, 2_000, 3_000] },
        )
        .toBeGreaterThan(0);
      await expect(cartRow).toContainText("Renews monthly at");
      await expect(cartRow).toContainText(/\$10\.00 \+ tax/);
      await capture(page, "renewal-plus-tax-cart");

      // Same wording at the moment of commitment, beside checkout's own "Tax (added)" line.
      await page.goto("/checkout");
      const checkoutRow = page.getByTestId(`renewal-${MONTHLY}`);
      await expect(checkoutRow).toContainText(/\$10\.00 \+ tax/, { timeout: 10_000 });
      await expect(page.getByText(/^Tax \(added\)$/)).toBeVisible();
      await capture(page, "renewal-plus-tax-checkout");
    } finally {
      await request.put(`${GATEWAY}/api/catalog/admin/offers/${offerId}`, { data: { active: false } });
    }
  });
});

async function adminLogin(request: APIRequestContext): Promise<void> {
  const login = await request.post(`${GATEWAY}/api/identity/login`, { data: ADMIN });
  expect(login.ok()).toBeTruthy();
}

/** The US demo store, only when it is tax-EXCLUSIVE with a non-zero rate (the case under test). */
async function resolveUsStore(request: APIRequestContext): Promise<{ id: string; currency: string } | null> {
  const r = await request.get(`${GATEWAY}/api/catalog/storefronts/public?slug=us`);
  if (!r.ok()) return null;
  const s = (await r.json()) as { id: string; currency: string; taxRegime: number | string; taxRateBasisPoints: number };
  const inclusive = s.taxRegime === 1 || s.taxRegime === 2 || s.taxRegime === "AuGst" || s.taxRegime === "EuVat";
  return !inclusive && s.taxRateBasisPoints > 0 ? { id: s.id, currency: s.currency } : null;
}

/** Adds the first buyable product of the store's own catalogue to the cart; returns its id. */
async function addFirstInStockStoreProduct(page: Page, request: APIRequestContext, storefrontId: string, currency: string): Promise<string> {
  const r = await request.get(`${GATEWAY}/api/catalog/products?storefrontId=${storefrontId}&currency=${currency}&pageSize=20`);
  expect(r.ok(), `listing the store's products should succeed: ${r.status()}`).toBeTruthy();
  const hits = ((await r.json()) as { id: string; slug: string }[]).filter((h) => h.slug && !h.slug.includes("e2e"));
  for (const hit of hits) {
    await page.goto(`/products/${hit.slug}`);
    const add = page.getByRole("button", { name: /add to cart/i });
    if (await add.isEnabled().catch(() => false)) {
      await add.click();
      await page.waitForURL(/\/cart/);
      return hit.id;
    }
  }
  throw new Error(`No in-stock product among the US store's first ${hits.length} products (stock depleted?).`);
}

/**
 * A store-scoped, top-priority monthly subscription offer for the product from a supplier created and approved
 * for this run (DECISION A, ADR-0048: only an approved supplier's offer counts). A fresh supplier per run keeps the
 * offer key (tenant × product × variant × supplier × storefront) unique and leaves the demo supplier's offers —
 * and its admin cost table — untouched.
 */
async function makeMonthlySubscription(request: APIRequestContext, productId: string, storefrontId: string, currency: string): Promise<string> {
  const supplierId = await createApprovedSupplier(request, `Renewal tax E2E ${Date.now()}`);
  const created = await request.post(`${GATEWAY}/api/catalog/admin/offers`, {
    data: {
      tenantId: TENANT_ID,
      productId,
      variantId: null,
      supplierId,
      supplyCategory: 2, // Digital
      fulfilmentType: 4, // Subscription
      currency,
      storefrontId,
      priceMinor: RENEWAL_PRICE_MINOR,
      priority: 0,
      pricingModel: 2, // Subscription
      billingPeriod: MONTHLY,
    },
  });
  expect(created.ok(), `creating the subscription offer should succeed: ${await created.text()}`).toBeTruthy();
  return ((await created.json()) as { id: string }).id;
}

/** A supplier driven through onboarding to Active via the gateway (the same steps as e2e-admin/suppliers.spec.ts). */
async function createApprovedSupplier(request: APIRequestContext, legalName: string): Promise<string> {
  const ok = async (call: Promise<APIResponse>, step: string) => {
    const r = await call;
    expect(r.ok(), `${step}: ${r.status()} ${await r.text()}`).toBeTruthy();
    return r;
  };
  const created = await ok(request.post(`${GATEWAY}/api/entity/entities`, { data: { tenantId: TENANT_ID, type: 2, legalName } }), "create entity");
  const entityId = ((await created.json()) as { id: string }).id;
  const base = `${GATEWAY}/api/entity/entities/${entityId}`;
  const withAbn = await ok(request.post(`${base}/identifiers`, { data: { type: 1, value: "51824753556" } }), "add ABN");
  const abnId = ((await withAbn.json()) as { identifiers: { id: string; type: number }[] }).identifiers.find((i) => i.type === 1)!.id;
  await ok(request.post(`${base}/identifiers/${abnId}/verify`, { data: {} }), "verify ABN");
  await ok(request.post(`${base}/contacts`, { data: { purpose: 1, kind: 1, value: "ops@e2e.example" } }), "add contact");
  await ok(request.post(`${base}/addresses`, { data: { purpose: 1, line1: "1 Test St", city: "Sydney", postcode: "2000", countryCode: "AU" } }), "add address");
  await ok(request.post(`${base}/suppliers`, { data: {} }), "start onboarding");
  await ok(request.post(`${base}/suppliers/submit-verification`, { data: {} }), "submit verification");
  await ok(request.post(`${base}/suppliers/verification-complete`, { data: {} }), "verification complete");
  await ok(request.post(`${base}/suppliers/activate`, { data: {} }), "approve (activate)");
  return entityId;
}
