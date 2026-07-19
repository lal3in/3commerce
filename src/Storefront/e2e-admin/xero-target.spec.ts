import { test, expect } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";
const TENANT_ID = "00000000-0000-0000-0000-000000000001";

// Xero mapping target dropdown (ux_8): choosing a scope loads the REAL options for that scope from the
// owning services, so the operator selects a target by name rather than pasting a GUID.
test.describe("Xero mapping target dropdown", () => {
  test("selecting a scope populates the target with real options", async ({ page, request }) => {
    // Seed a storefront via the gateway so the Storefront scope has at least one real option. The CI
    // stack seeds none, and storefront-lifecycle.spec archives its own row (archived rows are excluded
    // from the admin list), so without this the dropdown would be legitimately — not wrongly — empty.
    const login = await request.post(`${GATEWAY}/api/identity/login`, {
      data: { email: "admin@3commerce.local", password: "dev-admin-password-1" },
    });
    expect(login.ok()).toBeTruthy();
    const name = `Xero Target E2E ${Date.now()}`;
    const created = await request.post(`${GATEWAY}/api/catalog/admin/storefronts`, {
      data: { tenantId: TENANT_ID, name, visibility: 1, currency: "EUR", taxRegime: 0, taxRateBasisPoints: 0 },
    });
    expect(created.ok()).toBeTruthy();
    const storefrontId = (await created.json()).id as string;

    await loginAsAdmin(page);
    await page.goto("/xero-mappings");
    await expect(page.getByRole("heading", { name: /xero/i }).first()).toBeVisible();

    // Load the tenant's mappings — reveals the create form (retry across the Blazor pre-circuit window).
    await expect(async () => {
      await page.getByRole("button", { name: /^load$/i }).click();
      await expect(page.locator("select").first()).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 20_000 });

    // The scope <select> carries the "Tenant default" option; pick Storefront (numeric 2). Select once —
    // re-selecting the same value fires no change event, so the @bind:after option load wouldn't refire.
    const scope = page.locator("select").filter({ has: page.locator("option", { hasText: /tenant default/i }) }).first();
    await scope.selectOption("2");

    // The target load is async; retry only the assertion until the seeded storefront shows by NAME.
    await expect(async () => {
      const target = page.locator("select").filter({ has: page.locator("option", { hasText: /select an option/i }) }).first();
      expect(await target.locator("option", { hasText: name }).count()).toBe(1);
    }).toPass({ timeout: 15_000 });

    // Archive the seeded row so repeated runs don't accumulate storefronts in the shared stack.
    const archived = await request.post(`${GATEWAY}/api/catalog/admin/storefronts/${storefrontId}/archive`);
    expect(archived.ok()).toBeTruthy();
  });
});
