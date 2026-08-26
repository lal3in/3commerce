import { test, expect, type APIRequestContext, type Page, type Locator } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";
const TENANT_ID = "00000000-0000-0000-0000-000000000001";
const DEMO_SUPPLIER = "Demo Supplier"; // seeded supplier that backs the demo offers
// Where before/after cost-edit screenshots land. Overridable so a local run can drop them in the
// scratchpad; defaults under test-results so CI keeps them with the run artifacts.
const SHOT_DIR = process.env.SUPPLIER_SHOT_DIR ?? "test-results/suppliers-cost";

// Locates the "Supplied products & variants" table by its distinctive Variant SKU header.
function offersTable(page: Page): Locator {
  return page.locator("table").filter({ has: page.getByRole("columnheader", { name: "Variant SKU" }) });
}

/** Opens the demo supplier from the Suppliers list and returns its supplied-products/cost table. */
async function openDemoSupplierOffers(page: Page): Promise<Locator> {
  await expect(page.getByRole("heading", { name: "Suppliers", exact: true }).first()).toBeVisible();
  const supplierRow = page.locator("tr", { hasText: DEMO_SUPPLIER });
  await expect(supplierRow).toBeVisible({ timeout: 15_000 });
  await supplierRow.getByRole("button", { name: "Manage", exact: true }).click();
  const table = offersTable(page);
  await expect(table).toBeVisible({ timeout: 10_000 });
  await expect(table.locator("tbody tr").first()).toBeVisible();
  return table;
}

/**
 * Finds the first PRODUCT-level row (variant SKU shown as "—") and the first VARIANT-level row
 * (a real SKU) in the offers table. Offers are ordered by priority, so these indices are stable
 * across reloads. The demo supplier seeds both kinds (61 product-level, 22 variant-level).
 */
async function locateRows(table: Locator): Promise<{ productIdx: number; variantIdx: number }> {
  const rows = table.locator("tbody tr");
  const count = await rows.count();
  let productIdx = -1;
  let variantIdx = -1;
  for (let i = 0; i < count; i++) {
    const sku = (await rows.nth(i).locator("td").nth(1).innerText()).trim();
    if (sku === "—") {
      if (productIdx < 0) productIdx = i;
    } else if (sku.length > 0) {
      if (variantIdx < 0) variantIdx = i;
    }
    if (productIdx >= 0 && variantIdx >= 0) break;
  }
  return { productIdx, variantIdx };
}

/** Edits one row's supplier cost, saves, and waits for the confirmation toast. */
async function editCost(page: Page, row: Locator, newCost: string): Promise<void> {
  await row.locator('input[type="number"]').fill(newCost);
  await row.getByRole("button", { name: "Save", exact: true }).click();
  await expect(page.getByText(/Supplier cost saved/i)).toBeVisible({ timeout: 10_000 });
}

test("Suppliers: product-level AND variant-level supplier costs are editable and persist", async ({ page }) => {
  await loginAsAdmin(page);
  await page.goto("/suppliers");

  // Open the demo supplier's supplied-products/cost table and confirm BOTH row kinds render.
  const table = await openDemoSupplierOffers(page);
  const { productIdx, variantIdx } = await locateRows(table);
  expect(productIdx, "a product-level offer row (variant SKU '—') must render").toBeGreaterThanOrEqual(0);
  expect(variantIdx, "a variant-level offer row (real variant SKU) must render").toBeGreaterThanOrEqual(0);

  const productRow = table.locator("tbody tr").nth(productIdx);
  const variantRow = table.locator("tbody tr").nth(variantIdx);

  // Product-level row: a product title in col 1, a "—" SKU in col 2, and an editable cost input.
  const productTitle = (await productRow.locator("td").first().innerText()).trim();
  expect(productTitle.length).toBeGreaterThan(0);
  expect((await productRow.locator("td").nth(1).innerText()).trim()).toBe("—");
  await expect(productRow.locator('input[type="number"]')).toBeEditable();

  // Variant-level row: a real (non-"—") variant SKU and its own editable cost input.
  const variantSku = (await variantRow.locator("td").nth(1).innerText()).trim();
  expect(variantSku.length).toBeGreaterThan(0);
  expect(variantSku).not.toBe("—");
  await expect(variantRow.locator('input[type="number"]')).toBeEditable();

  // BEFORE screenshot: the supplied-products/cost table with both row kinds visible.
  await table.scrollIntoViewIfNeeded();
  await page.screenshot({ path: `${SHOT_DIR}/cost-edit-before.png`, fullPage: true });

  // Edit BOTH a product-level and a variant-level cost to fresh, distinct values and save each.
  const productCost = String(10000 + (Date.now() % 80000));
  const variantCost = String(90000 + (Date.now() % 9000));
  await editCost(page, productRow, productCost);
  await editCost(page, variantRow, variantCost);

  // Persistence: reload (fresh Blazor circuit), re-open the supplier, and BOTH edits are still there.
  await page.reload();
  const table2 = await openDemoSupplierOffers(page);
  const productRow2 = table2.locator("tbody tr").nth(productIdx);
  const variantRow2 = table2.locator("tbody tr").nth(variantIdx);
  // Row order is priority-stable, so the same indices address the same offers after reload.
  expect((await productRow2.locator("td").first().innerText()).trim()).toBe(productTitle);
  expect((await variantRow2.locator("td").nth(1).innerText()).trim()).toBe(variantSku);
  await expect(productRow2.locator('input[type="number"]')).toHaveValue(productCost);
  await expect(variantRow2.locator('input[type="number"]')).toHaveValue(variantCost);

  // AFTER screenshot: the reloaded table showing the persisted product-level and variant-level costs.
  await table2.scrollIntoViewIfNeeded();
  await page.screenshot({ path: `${SHOT_DIR}/cost-edit-after.png`, fullPage: true });
});

test("Suppliers: the Approve action activates a pending supplier", async ({ page, request }) => {
  // Seed a fresh supplier and drive it (via the gateway API) all the way to PendingApproval so the UI
  // Approve button (which calls suppliers/activate) has a valid transition to exercise.
  const legalName = `Approve E2E ${Date.now()}`;
  await driveSupplierToPendingApproval(request, legalName);

  await loginAsAdmin(page);
  await page.goto("/suppliers");
  await expect(page.getByRole("heading", { name: "Suppliers", exact: true }).first()).toBeVisible();

  const row = page.locator("tr", { hasText: legalName });
  await expect(row).toBeVisible({ timeout: 15_000 });
  await expect(row).toContainText("Pending approval");

  await row.getByRole("button", { name: "Manage", exact: true }).click();
  const approve = page.getByRole("button", { name: "Approve", exact: true });
  await expect(approve).toBeEnabled();
  await approve.click();

  await expect(page.getByText(/Supplier approved and activated/i)).toBeVisible({ timeout: 10_000 });
  // Status now reads Active and the Approve button is disabled.
  await expect(page.getByText("Active", { exact: true }).first()).toBeVisible();
  await expect(page.getByRole("button", { name: "Approve", exact: true })).toBeDisabled();
});

/** Admin-authenticated gateway calls that create a supplier and move it to PendingApproval. */
async function driveSupplierToPendingApproval(request: APIRequestContext, legalName: string): Promise<string> {
  const login = await request.post(`${GATEWAY}/api/identity/login`, {
    data: { email: "admin@3commerce.local", password: "dev-admin-password-1" },
  });
  expect(login.ok()).toBeTruthy();

  const created = await request.post(`${GATEWAY}/api/entity/entities`, {
    data: { tenantId: TENANT_ID, type: 2, legalName },
  });
  expect(created.ok()).toBeTruthy();
  const entityId = ((await created.json()) as { id: string }).id;

  // Verified ABN + primary email + registered-office address satisfy onboarding readiness.
  const detailResp = await request.post(`${GATEWAY}/api/entity/entities/${entityId}/identifiers`, {
    data: { type: 1, value: "51824753556" },
  });
  expect(detailResp.ok()).toBeTruthy();
  const detail = (await detailResp.json()) as { identifiers: { id: string; type: number }[] };
  const identifierId = detail.identifiers.find((i) => i.type === 1)!.id;
  expect((await request.post(`${GATEWAY}/api/entity/entities/${entityId}/identifiers/${identifierId}/verify`, { data: {} })).ok()).toBeTruthy();
  expect((await request.post(`${GATEWAY}/api/entity/entities/${entityId}/contacts`, { data: { purpose: 1, kind: 1, value: "ops@e2e.example" } })).ok()).toBeTruthy();
  expect((await request.post(`${GATEWAY}/api/entity/entities/${entityId}/addresses`, { data: { purpose: 1, line1: "1 Test St", city: "Sydney", postcode: "2000", countryCode: "AU" } })).ok()).toBeTruthy();

  // Start onboarding → submit for verification → mark verification complete = PendingApproval.
  expect((await request.post(`${GATEWAY}/api/entity/entities/${entityId}/suppliers`, { data: {} })).ok()).toBeTruthy();
  expect((await request.post(`${GATEWAY}/api/entity/entities/${entityId}/suppliers/submit-verification`, { data: {} })).ok()).toBeTruthy();
  expect((await request.post(`${GATEWAY}/api/entity/entities/${entityId}/suppliers/verification-complete`, { data: {} })).ok()).toBeTruthy();
  return entityId;
}
