import { test, expect, type APIRequestContext } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";
const TENANT_ID = "00000000-0000-0000-0000-000000000001";
const DEMO_SUPPLIER = "Demo Supplier"; // seeded supplier that backs the demo offers

// Locates the "Supplied products & variants" table by its distinctive Variant SKU header.
function offersTable(page: import("@playwright/test").Page) {
  return page.locator("table").filter({ has: page.getByRole("columnheader", { name: "Variant SKU" }) });
}

test("Suppliers: lists a supplier, shows its supplied products, and edits a supplier cost that persists", async ({ page }) => {
  await loginAsAdmin(page);
  await page.goto("/suppliers");
  await expect(page.getByRole("heading", { name: "Suppliers", exact: true }).first()).toBeVisible();

  // The seeded Demo Supplier appears in the list.
  const supplierRow = page.locator("tr", { hasText: DEMO_SUPPLIER });
  await expect(supplierRow).toBeVisible({ timeout: 15_000 });

  // Open it → the supplied products/variants table renders with rows.
  await supplierRow.getByRole("button", { name: "Manage", exact: true }).click();
  const table = offersTable(page);
  await expect(table).toBeVisible({ timeout: 10_000 });
  const firstRow = table.locator("tbody tr").first();
  await expect(firstRow).toBeVisible();
  const productTitle = (await firstRow.locator("td").first().innerText()).trim();
  expect(productTitle.length).toBeGreaterThan(0);

  // Edit the first row's supplier cost to a fresh value and save it.
  const newCost = String(10000 + (Date.now() % 90000));
  const costInput = firstRow.locator('input[type="number"]');
  await costInput.fill(newCost);
  await firstRow.getByRole("button", { name: "Save", exact: true }).click();
  await expect(page.getByText(/Supplier cost saved/i)).toBeVisible({ timeout: 10_000 });

  // Persistence: reload the page (fresh circuit), re-open the supplier, and the value is still there.
  await page.reload();
  await expect(page.getByRole("heading", { name: "Suppliers", exact: true }).first()).toBeVisible();
  const reopenedRow = page.locator("tr", { hasText: DEMO_SUPPLIER });
  await expect(reopenedRow).toBeVisible({ timeout: 15_000 });
  await reopenedRow.getByRole("button", { name: "Manage", exact: true }).click();
  const table2 = offersTable(page);
  await expect(table2).toBeVisible({ timeout: 10_000 });
  const firstRow2 = table2.locator("tbody tr").first();
  // Offers are ordered by priority, so the first row is stable across reloads.
  expect((await firstRow2.locator("td").first().innerText()).trim()).toBe(productTitle);
  await expect(firstRow2.locator('input[type="number"]')).toHaveValue(newCost);
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
