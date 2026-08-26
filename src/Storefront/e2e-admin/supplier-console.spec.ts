import { test, expect, type APIRequestContext, type Page } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

// The Admin Suppliers page as a COMPLETE supplier console: identifiers (view/add/verify), the warehouse
// address, onboarding lifecycle actions, and change-request review — all without leaving to the generic
// Entities page. Runs against the live stack (admin :5200, gateway :8080).
const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";
const TENANT = "00000000-0000-0000-0000-000000000001";
const ADMIN_EMAIL = "admin@3commerce.local";
const ADMIN_PASSWORD = "dev-admin-password-1";
const DEMO_SUPPLIER = "Demo Supplier";
const SHOT_DIR = process.env.SHOT_DIR ?? "test-results";

/** Admin-authenticated: create a supplier entity with onboarding started (Draft). */
async function newDraftSupplier(request: APIRequestContext, legalName: string): Promise<string> {
  const login = await request.post(`${GATEWAY}/api/identity/login`, { data: { email: ADMIN_EMAIL, password: ADMIN_PASSWORD } });
  expect(login.ok()).toBeTruthy();
  const created = await request.post(`${GATEWAY}/api/entity/entities`, { data: { tenantId: TENANT, type: 2, legalName } });
  expect(created.ok()).toBeTruthy();
  const entityId = ((await created.json()) as { id: string }).id;
  expect((await request.post(`${GATEWAY}/api/entity/entities/${entityId}/suppliers`, { data: {} })).ok()).toBeTruthy();
  return entityId;
}

async function manage(page: Page, legalName: string) {
  await page.goto("/suppliers");
  await expect(page.getByRole("heading", { name: "Suppliers", exact: true }).first()).toBeVisible();
  const row = page.locator("tr", { hasText: legalName });
  await expect(row).toBeVisible({ timeout: 15_000 });
  await row.getByRole("button", { name: "Manage", exact: true }).click();
}

test("Suppliers console: add + verify an identifier, add a warehouse address, and a refused lifecycle surfaces the reason", async ({ page, request }) => {
  const legalName = `Console E2E ${Date.now()}`;
  await newDraftSupplier(request, legalName);

  await loginAsAdmin(page);
  await manage(page, legalName);

  // Identifiers section: add an ABN, then verify it.
  const idSection = page.locator("section").filter({ has: page.getByRole("heading", { name: /registration identifiers/i }) });
  await expect(idSection).toBeVisible({ timeout: 10_000 });
  await idSection.getByPlaceholder("Identifier value").fill("51824753556");
  await idSection.getByRole("button", { name: "Add", exact: true }).click();
  await expect(idSection.getByText("51824753556")).toBeVisible({ timeout: 10_000 });
  await idSection.getByRole("button", { name: "Verify", exact: true }).click();
  await expect(idSection.getByText(/verified/i)).toBeVisible({ timeout: 10_000 });

  // Addresses section: add a Warehouse address (defaults to Warehouse purpose).
  const addrSection = page.locator("section").filter({ has: page.getByRole("heading", { name: "Addresses", exact: true }) });
  await addrSection.getByPlaceholder("Address line 1").fill("7 Console Way");
  await addrSection.getByPlaceholder("City").fill("Sydney");
  await addrSection.getByPlaceholder("Postcode").fill("2000");
  await addrSection.getByRole("button", { name: "Add", exact: true }).click();
  const addrRow = addrSection.locator("li").filter({ hasText: "7 Console Way" });
  await expect(addrRow).toBeVisible({ timeout: 10_000 });
  await expect(addrRow).toContainText("Warehouse");
  await page.screenshot({ path: `${SHOT_DIR}/admin-suppliers-console.png`, fullPage: true });

  // Lifecycle: suspending a Draft supplier is refused by design — the reason is surfaced, not swallowed.
  await page.getByRole("button", { name: "Suspend", exact: true }).click();
  await expect(page.getByText(/only active suppliers can be suspended/i)).toBeVisible({ timeout: 10_000 });
});

test("Suppliers console: reviews and approves a supplier-raised change request (maker-checker)", async ({ page, request, playwright }) => {
  // Fresh supplier + its own portal login so the change request's requester differs from the approving admin.
  const run = Date.now();
  const email = `pw.console.${run}@example.test`;
  const password = "Pw-supplier-123";
  const legalName = `Console CR ${run}`;
  await request.post(`${GATEWAY}/api/identity/login`, { data: { email: ADMIN_EMAIL, password: ADMIN_PASSWORD } });
  await request.post(`${GATEWAY}/api/identity/register`, { data: { email, password } });
  const entityId = ((await (await request.post(`${GATEWAY}/api/entity/entities`, { data: { tenantId: TENANT, type: 2, legalName } })).json()) as { id: string }).id;
  await request.post(`${GATEWAY}/api/entity/entities/${entityId}/suppliers`, { data: {} });
  const users = await (await request.get(`${GATEWAY}/api/identity/admin/users?tenantId=${TENANT}`)).json();
  const userId = users.find((u: { email: string }) => u.email.toLowerCase() === email.toLowerCase()).id;
  await request.post(`${GATEWAY}/api/identity/admin/users/${userId}/make-supplier?tenantId=${TENANT}`, { data: { supplierEntityId: entityId } });

  // The supplier raises the change request (as itself, in its own context), so maker != checker.
  const summary = `Console rebrand ${run}`;
  const supApi = await playwright.request.newContext();
  await supApi.post(`${GATEWAY}/api/identity/login`, { data: { email, password } });
  const proposed = JSON.stringify({ legalName: `${legalName} (new)`, tradingName: null });
  const raised = await supApi.post(`${GATEWAY}/api/entity/entities/suppliers/me/change-requests`, { data: { type: 4, summary, detail: proposed } });
  expect(raised.ok()).toBeTruthy();
  await supApi.dispose();

  await loginAsAdmin(page);
  await manage(page, legalName);

  const crSection = page.locator("section").filter({ has: page.getByRole("heading", { name: "Change requests", exact: true }) });
  await expect(crSection.getByText(summary)).toBeVisible({ timeout: 10_000 });
  await crSection.getByRole("textbox").first().fill("Verified rebrand");
  await crSection.getByRole("button", { name: "Approve", exact: true }).click();
  await expect(page.getByText(/change request approved/i)).toBeVisible({ timeout: 10_000 });
});

test("Suppliers console: the product-cost section is prominent and counts the supplier's offers", async ({ page }) => {
  await loginAsAdmin(page);
  await page.goto("/suppliers");
  await expect(page.getByRole("heading", { name: "Suppliers", exact: true }).first()).toBeVisible();

  // Open the seeded Demo Supplier that backs the offers. (A repeatedly re-seeded dev DB can hold more
  // than one same-named demo entity; a fresh CI seed holds exactly one. Try each until the offers render.)
  const demoRows = page.locator("tr").filter({ hasText: DEMO_SUPPLIER });
  await expect(demoRows.first()).toBeVisible({ timeout: 15_000 });
  const n = await demoRows.count();
  expect(n).toBeGreaterThan(0);
  for (let i = 0; i < n; i++) {
    await page.locator("tr").filter({ hasText: DEMO_SUPPLIER }).nth(i).getByRole("button", { name: "Manage", exact: true }).click();
    const offers = page.locator("section").filter({ has: page.getByRole("heading", { name: /supplied products & variants/i }) });
    await expect(offers).toBeVisible({ timeout: 10_000 });
    // A count badge makes the section unmistakably the place to manage supplier cost; it renders once the
    // offers have loaded (before that the section shows a loading placeholder), so wait for it.
    await expect(offers.getByText(/\d+ products \/ variants/i)).toBeVisible({ timeout: 10_000 });
    if ((await offers.getByRole("columnheader", { name: "Variant SKU" }).count()) > 0) {
      // The supplier's products (product-level offers show the title, never a blank) render in an
      // editable-cost table.
      await expect(offers.locator("tbody tr").first()).toBeVisible();
      return;
    }
    await page.getByRole("button", { name: "Back", exact: true }).click();
  }
  throw new Error("no Demo Supplier row rendered a supplied-products table");
});
