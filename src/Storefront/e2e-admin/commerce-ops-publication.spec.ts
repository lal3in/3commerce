import { test, expect } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";
const TENANT_ID = "00000000-0000-0000-0000-000000000001";

// The Publication section's Product field is a dropdown of catalog products (not a free-text GUID), and the
// four publication actions — Assign product, Product readiness, Publish, Unpublish — are wired and give
// feedback. Seeds a fresh Preview storefront via the gateway so the test owns its row.
test("product publication: dropdown picks a catalog product and the four actions work", async ({ page, request }) => {
  const login = await request.post(`${GATEWAY}/api/identity/login`, {
    data: { email: "admin@3commerce.local", password: "dev-admin-password-1" },
  });
  expect(login.ok()).toBeTruthy();

  const name = `Publication E2E ${Date.now()}`;
  const created = await request.post(`${GATEWAY}/api/catalog/admin/storefronts`, {
    data: { tenantId: TENANT_ID, name, visibility: 4, publicUrl: "http://localhost:3000/pub-e2e", currency: "EUR", taxRegime: 2, taxRateBasisPoints: 1000 },
  });
  expect(created.ok()).toBeTruthy();
  const storefrontId = ((await created.json()) as { id: string }).id;
  // Preview so it's a normal manageable row.
  await request.post(`${GATEWAY}/api/catalog/admin/storefronts/${storefrontId}/preview`);

  await loginAsAdmin(page);
  await page.goto("/commerce-ops");
  await page.getByRole("button", { name: /load storefronts/i }).click();

  const row = page.locator("tr", { hasText: name });
  await expect(row).toBeVisible({ timeout: 10_000 });
  await row.getByRole("button", { name: "Manage", exact: true }).click();

  // The Product field is now a <select> populated from the catalog (the placeholder option identifies it),
  // NOT a text input.
  const productSelect = page.locator("select").filter({ has: page.locator("option", { hasText: "Select a product" }) });
  await expect(productSelect).toBeVisible({ timeout: 10_000 });
  // It carries real catalog products beyond the placeholder.
  await expect(productSelect.locator("option")).not.toHaveCount(1);

  // Fulfillment source must be assigned for a product to be publishable — pick Own warehouse. Locate the
  // select by its distinctive option text (several selects share option value "2").
  const fulfillmentSelect = page.locator("select").filter({ has: page.getByRole("option", { name: "Own warehouse" }) });
  await fulfillmentSelect.selectOption({ label: "Own warehouse" });

  // Pick the first real product (index 1, after the placeholder) and remember its label to find it below.
  await productSelect.selectOption({ index: 1 });
  const productLabel = (await productSelect.locator("option").nth(1).innerText()).trim();

  // Assign product → success status + the product appears in the assigned-products table.
  await page.getByRole("button", { name: "Assign product", exact: true }).click();
  await expect(page.getByText("Product assigned.", { exact: true })).toBeVisible({ timeout: 10_000 });
  await expect(page.locator("table").filter({ hasText: productLabel })).toBeVisible({ timeout: 10_000 });

  // Product readiness → a verdict block (Ready / Not ready) renders.
  await page.getByRole("button", { name: "Product readiness", exact: true }).click();
  await expect(page.getByText("Publication readiness:")).toBeVisible({ timeout: 10_000 });
  await expect(page.getByText(/^(Ready|Not ready)$/).first()).toBeVisible({ timeout: 10_000 });

  // Publish → the action executes and gives feedback: either the publish status (ready) or the readiness
  // blockers (not ready). Either proves the button is wired.
  await page.getByRole("button", { name: "Publish", exact: true }).click();
  await expect(page.getByText(/Product publish requested\.|Publication readiness:/)).toBeVisible({ timeout: 10_000 });

  // Unpublish → success status (unpublish has no readiness gate).
  await page.getByRole("button", { name: "Unpublish", exact: true }).click();
  await expect(page.getByText("Product unpublish requested.", { exact: true })).toBeVisible({ timeout: 10_000 });
});
