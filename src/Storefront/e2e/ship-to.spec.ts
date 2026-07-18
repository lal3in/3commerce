import { test, expect, type APIRequestContext, type Page } from "@playwright/test";

/**
 * Ship-to-country allowlist (Stage 1): a storefront can restrict which countries it ships to. When the
 * AU demo store's allowlist is non-empty, the checkout country picker collapses to exactly those
 * countries (and Ordering rejects anything outside the list — covered by the MoneyFlow integration
 * test). Needs dev-up --data full (AU demo storefront); skips where that seed is absent (CI browser-e2e
 * boots via the importer only). Mutates the shared AU store, so it always restores worldwide afterwards.
 */
const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";
const ADMIN_EMAIL = "admin@3commerce.local";
const ADMIN_PASSWORD = "dev-admin-password-1";
const TENANT = "00000000-0000-0000-0000-000000000001";

test.describe("Storefront ship-to allowlist", () => {
  test("non-empty allowlist limits the checkout country picker to served countries", async ({ page, request }) => {
    const store = await auStore(request);
    test.skip(store === null, "AU demo storefront not seeded (needs --data full)");

    try {
      await setShipTo(request, store!, ["AU", "NZ"]);

      await page.goto("/au"); // pin the AU storefront (middleware cookie)
      await addFirstInStockProduct(page);
      await page.goto("/checkout");

      const country = page.locator('select[name="shippingCountry"]').first();
      await expect(country).toBeVisible();
      const options = await country.locator("option").evaluateAll((os) =>
        (os as HTMLOptionElement[]).map((o) => o.value).filter(Boolean),
      );
      expect(options).toEqual(["AU", "NZ"]);

      // The region label still adapts to the selected country (AU → State).
      const regionLabel = await page.evaluate(() => {
        const input = document.querySelector('input[name="shippingRegion"]');
        return input?.closest("div")?.querySelector("label")?.textContent?.trim() ?? "";
      });
      expect(regionLabel).toBe("State");
    } finally {
      await setShipTo(request, store!, []); // restore worldwide
    }
  });
});

/** Logs in as admin (also satisfies the customer policy) and returns the AU demo store, or null. */
async function auStore(request: APIRequestContext): Promise<Record<string, unknown> | null> {
  const login = await request.post(`${GATEWAY}/api/identity/login`, {
    data: { email: ADMIN_EMAIL, password: ADMIN_PASSWORD },
  });
  if (!login.ok()) return null;
  const list = await request.get(`${GATEWAY}/api/catalog/admin/storefronts?tenantId=${TENANT}`);
  if (!list.ok()) return null;
  const stores = (await list.json()) as Array<Record<string, unknown>>;
  return stores.find((s) => String(s.publicUrl ?? "").endsWith("/au")) ?? null;
}

/** PUTs the storefront back with a new ship-to allowlist, preserving its other settings. */
async function setShipTo(request: APIRequestContext, store: Record<string, unknown>, countries: string[]): Promise<void> {
  const response = await request.put(`${GATEWAY}/api/catalog/admin/storefronts/${store.id}`, {
    data: {
      name: store.name,
      visibility: store.visibility,
      publicUrl: store.publicUrl,
      currency: store.currency,
      taxRegime: store.taxRegime,
      taxRateBasisPoints: store.taxRateBasisPoints,
      shipToCountries: countries,
    },
  });
  expect(response.ok()).toBeTruthy();
}

async function addFirstInStockProduct(page: Page): Promise<void> {
  await page.goto("/search?q=lamp");
  const links = page.locator('a[href^="/products/"]');
  const count = Math.min(await links.count(), 5);
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
  throw new Error("No in-stock product found in the first five results.");
}
