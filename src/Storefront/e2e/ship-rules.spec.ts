import { test, expect, type APIRequestContext } from "@playwright/test";

/**
 * Per-product ship rules (Stage 2): a tenant feature switch makes per-country ship rules mandatory at
 * product create, and a rule can exempt a product from destination tax / mark shipping covered. This
 * drives the admin gate through the gateway (Catalog admin API). Needs an admin login + at least one
 * category; skips where those are absent (CI browser-e2e boots via the importer only). Mutates the
 * tenant catalog setting, so it always restores require=false and deletes the created product.
 */
const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";
const ADMIN_EMAIL = "admin@3commerce.local";
const ADMIN_PASSWORD = "dev-admin-password-1";
const TENANT = "00000000-0000-0000-0000-000000000001";

test.describe("Catalog per-country ship rules", () => {
  test("mandatory switch gates product create; a rule satisfies it", async ({ request }) => {
    const ready = await login(request);
    test.skip(!ready, "admin login unavailable (needs a booted stack)");

    const categoryId = await firstCategoryId(request);
    test.skip(categoryId === null, "no catalog category seeded");

    let createdId: string | null = null;
    try {
      await setRequireShipRules(request, true);

      const slug = `e2e-shiprule-${Date.now()}`;
      const body = (shipRules: unknown) => ({
        slug,
        title: "E2E Ship Rule",
        brand: "E2E",
        categoryId,
        variants: [{ sku: `${slug}-v1`, priceMinor: 1999, currency: "EUR", stockQuantity: 5 }],
        shipRules,
      });

      // No rules → rejected by the mandatory gate.
      const without = await request.post(`${GATEWAY}/api/catalog/admin/products`, { data: body(null) });
      expect(without.status()).toBe(400);

      // A whole-world rule that exempts destination tax → accepted.
      const withRule = await request.post(`${GATEWAY}/api/catalog/admin/products`, {
        data: body([{ countryCode: "*", chargeDestinationTax: false, shippingCovered: true }]),
      });
      expect(withRule.status()).toBe(201);
      const created = (await withRule.json()) as { id: string; shipRules: Array<{ countryCode: string }> };
      createdId = created.id;
      expect(created.shipRules.map((r) => r.countryCode)).toEqual(["*"]);
    } finally {
      await setRequireShipRules(request, false);
      if (createdId) {
        await request.delete(`${GATEWAY}/api/catalog/admin/products/${createdId}`);
      }
    }
  });
});

async function login(request: APIRequestContext): Promise<boolean> {
  const res = await request.post(`${GATEWAY}/api/identity/login`, {
    data: { email: ADMIN_EMAIL, password: ADMIN_PASSWORD },
  });
  return res.ok();
}

async function firstCategoryId(request: APIRequestContext): Promise<string | null> {
  const res = await request.get(`${GATEWAY}/api/catalog/categories`);
  if (!res.ok()) return null;
  const cats = (await res.json()) as Array<{ id: string }>;
  return cats.length > 0 ? cats[0].id : null;
}

async function setRequireShipRules(request: APIRequestContext, required: boolean): Promise<void> {
  const res = await request.put(`${GATEWAY}/api/catalog/admin/settings`, {
    data: { tenantId: TENANT, requireProductShipRules: required },
  });
  expect(res.ok()).toBeTruthy();
}
