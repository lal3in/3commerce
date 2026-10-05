import { test, expect, type Page, type APIRequestContext } from "@playwright/test";
import { capture } from "../e2e-support/screenshots";

/**
 * rev_disc: a storefront-wide percentage discount, deducted from the ITEMS' subtotal only, is shown as its
 * own line in the cart (shown == charged). Sets the discount on a demo storefront via the admin API, shops
 * it in the browser, and asserts the discount line + discounted items total appear. Resets the discount
 * afterwards so sibling specs see the baseline. Requires dev-up --data full (demo stores); skips otherwise.
 *
 * The cart's discount line is priced by ORDERING (/cart/summary reads its StorefrontDiscountCopy, projected
 * from Catalog's StorefrontConfigChanged over the bus), not by Catalog's public config — so Catalog showing
 * the new value says nothing about Ordering yet. Every wait on the discount therefore reloads the cart until
 * Ordering's view matches (the E2E seed projection race, docs/reference/engineering-gotchas.md).
 */
const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";
const TENANT_ID = "00000000-0000-0000-0000-000000000001";
const ADMIN = { email: "admin@3commerce.local", password: "dev-admin-password-1" };

interface AdminStore {
  id: string;
  name: string;
  visibility: number;
  publicUrl: string;
  currency: string;
  taxRegime: number;
  taxRateBasisPoints: number;
  discountBasisPoints: number;
}

test.describe("Storefront-wide discount (rev_disc)", () => {
  test("cart shows the discount line and the discounted items total", async ({ page, request }) => {
    const store = await resolveDemo(request);
    test.skip(!store, "no demo storefront published (needs dev-up --data full)");
    const { slug, id } = store!;

    await adminLogin(request);
    const original = await getAdminStore(request, id);
    test.skip(!original, "demo storefront not present in the admin list");

    let cartFilled = false;
    let bodyFailed = false;
    try {
      // Baseline: no discount. Shop the store and capture the cart with no discount line.
      await putDiscount(request, original!, 0);
      await expect.poll(async () => (await publicConfig(request, slug))?.discountBasisPoints, { timeout: 15_000 }).toBe(0);
      await page.goto(`/${slug}`);
      await addFirstInStockProduct(page); // lands on /cart
      cartFilled = true;
      // Ordering may still hold a discount from an earlier run — reload until its view is the baseline too.
      await reloadCartUntilDiscount(page, false);
      await capture(page, "discount-cart-before");

      // Set a 10% storefront-wide discount and wait for the public config to reflect it.
      await putDiscount(request, original!, 1000);
      await expect.poll(async () => (await publicConfig(request, slug))?.discountBasisPoints, { timeout: 15_000 }).toBe(1000);

      // Reload the cart (config + summary are fetched no-store per request) until Ordering has projected
      // the discount → the discount line and items total appear.
      await reloadCartUntilDiscount(page, true);
      const discountRow = page.locator("div", { hasText: /^Discount \(10%\)/ }).last();
      await expect(discountRow).toBeVisible();
      await expect(discountRow).toContainText("−"); // shown as a deduction
      await expect(page.getByText(/^Items total$/)).toBeVisible();
      await capture(page, "discount-cart-after");
    } catch (error) {
      bodyFailed = true;
      throw error;
    } finally {
      // Reset the demo store's discount so sibling specs see the baseline (0 = none) — and wait until
      // ORDERING has it too: storefront-promotions runs next on the same store and sums the cart's rows,
      // so a lingering projected discount would break its arithmetic. A wait failure never masks the
      // body's own error.
      await putDiscount(request, original!, 0);
      if (cartFilled) {
        await reloadCartUntilDiscount(page, false).catch((error: unknown) => {
          if (!bodyFailed) throw error;
        });
      }
    }
  });
});

async function adminLogin(request: APIRequestContext): Promise<void> {
  const login = await request.post(`${GATEWAY}/api/identity/login`, { data: ADMIN });
  expect(login.ok()).toBeTruthy();
}

/** Resolve a live demo storefront (au/eu/us): its slug and id. Null when none is published. */
async function resolveDemo(request: APIRequestContext): Promise<{ slug: string; id: string } | null> {
  for (const slug of ["eu", "us", "au"]) {
    const r = await request.get(`${GATEWAY}/api/catalog/storefronts/public?slug=${slug}`);
    if (r.ok()) {
      return { slug, id: ((await r.json()) as { id: string }).id };
    }
  }
  return null;
}

async function getAdminStore(request: APIRequestContext, id: string): Promise<AdminStore | null> {
  const r = await request.get(`${GATEWAY}/api/catalog/admin/storefronts?tenantId=${TENANT_ID}`);
  if (!r.ok()) return null;
  const stores = (await r.json()) as AdminStore[];
  return stores.find((s) => s.id === id) ?? null;
}

async function putDiscount(request: APIRequestContext, store: AdminStore, discountBasisPoints: number): Promise<void> {
  const r = await request.put(`${GATEWAY}/api/catalog/admin/storefronts/${store.id}`, {
    data: {
      name: store.name,
      visibility: store.visibility,
      publicUrl: store.publicUrl,
      currency: store.currency,
      taxRegime: store.taxRegime,
      taxRateBasisPoints: store.taxRateBasisPoints,
      discountBasisPoints,
    },
  });
  expect(r.ok()).toBeTruthy();
}

async function publicConfig(request: APIRequestContext, slug: string): Promise<{ discountBasisPoints?: number } | null> {
  const r = await request.get(`${GATEWAY}/api/catalog/storefronts/public?slug=${slug}`);
  return r.ok() ? ((await r.json()) as { discountBasisPoints?: number }) : null;
}

/**
 * Reloads the cart until Ordering's projected storefront discount shows (present) or is gone (absent).
 * The reload loop IS the wait for the Catalog -> Ordering StorefrontConfigChanged projection: the cart
 * page is server-rendered no-store, and its discount line only renders once /cart/summary prices one.
 */
async function reloadCartUntilDiscount(page: Page, present: boolean): Promise<void> {
  await expect(async () => {
    await page.reload();
    if (present) {
      await expect(page.locator("div", { hasText: /^Discount \(10%\)/ }).last()).toBeVisible({ timeout: 2_000 });
    } else {
      await expect(page.getByText(/^Discount \(/)).toHaveCount(0, { timeout: 2_000 });
    }
  }).toPass({ timeout: 30_000, intervals: [1_000, 2_000, 2_000, 3_000] });
}

async function addFirstInStockProduct(page: Page): Promise<void> {
  const links = page.locator('a[href^="/products/"]');
  const count = Math.min(await links.count(), 5);
  for (let i = 0; i < count; i++) {
    await links.nth(i).click();
    const add = page.getByRole("button", { name: /add to cart/i });
    if (await add.isEnabled()) {
      await add.click();
      await page.waitForURL(/\/cart/);
      return;
    }
    await page.goBack();
  }
  throw new Error("No in-stock product found in the first five grid items.");
}
