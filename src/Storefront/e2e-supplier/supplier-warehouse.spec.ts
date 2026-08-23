import { test, expect, type Locator, type Page } from "@playwright/test";

// Drives the SupplierPortal "Warehouse" approval lock end-to-end against the running stack
// (supplier portal :5300, admin :5200, gateway :8080). A fresh supplier + login is provisioned per
// run via the admin/identity API so the test does not depend on pre-seeded onboarding state. Mirrors
// the "My details" lock: editable while Draft, then read-only + a maker-checker change request once
// Active, whose approval supersedes the warehouse address.
const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";
const ADMIN_URL = process.env.ADMIN_URL ?? "http://localhost:5200";
const TENANT = "00000000-0000-0000-0000-000000000001";
const ADMIN_EMAIL = "admin@3commerce.local";
const ADMIN_PASSWORD = "dev-admin-password-1";
const SHOT_DIR = process.env.SHOT_DIR ?? "test-results";

const run = Date.now();
const supplier = { email: `pw.warehouse.${run}@example.test`, password: "Pw-warehouse-123", entityId: "" };

async function setBlazorInput(locator: Locator, value: string) {
  await locator.fill(value);
  await locator.dispatchEvent("change"); // nudge Blazor @bind once the InteractiveServer circuit is live
}

async function supplierSignIn(page: Page) {
  await page.goto("/login");
  await page.getByLabel("Email").fill(supplier.email);
  await page.getByLabel("Password").fill(supplier.password);
  await page.getByRole("button", { name: /sign in/i }).click();
  await expect(page.getByRole("heading", { name: /supplier overview/i })).toBeVisible();
}

// Fill the six address inputs of the warehouse form (line1, line2, city, region, postcode, country)
// scoped to a section by its heading, in field order.
async function fillWarehouse(section: Locator, line1: string, city: string, postcode: string, country: string) {
  const boxes = section.getByRole("textbox");
  await setBlazorInput(boxes.nth(0), line1);
  await setBlazorInput(boxes.nth(2), city);
  await setBlazorInput(boxes.nth(4), postcode);
  await setBlazorInput(boxes.nth(5), country);
}

test.describe.configure({ mode: "serial" });

test.describe("Supplier portal — Warehouse approval lock", () => {
  test.beforeAll(async ({ playwright }) => {
    const api = await playwright.request.newContext();
    await api.post(`${GATEWAY}/api/identity/login`, { data: { email: ADMIN_EMAIL, password: ADMIN_PASSWORD } });

    await api.post(`${GATEWAY}/api/identity/register`, { data: { email: supplier.email, password: supplier.password } });
    const entity = await (await api.post(`${GATEWAY}/api/entity/entities`, {
      data: { tenantId: TENANT, type: 2, legalName: `PW Warehouse Supplier ${run}` },
    })).json();
    supplier.entityId = entity.id;
    await api.post(`${GATEWAY}/api/entity/entities/${supplier.entityId}/suppliers`);

    const users = await (await api.get(`${GATEWAY}/api/identity/admin/users?tenantId=${TENANT}`)).json();
    const userId = users.find((u: { email: string }) => u.email.toLowerCase() === supplier.email.toLowerCase()).id;
    await api.post(`${GATEWAY}/api/identity/admin/users/${userId}/make-supplier?tenantId=${TENANT}`, {
      data: { supplierEntityId: supplier.entityId },
    });
    await api.dispose();
  });

  test("a not-yet-approved supplier sets its warehouse address directly", async ({ page }) => {
    await supplierSignIn(page);
    await page.goto("/warehouse");

    await expect(page.getByRole("heading", { name: /^warehouse$/i })).toBeVisible();
    const editSection = page.locator("section").filter({ has: page.getByRole("heading", { name: /^set warehouse address$/i }) });
    await expect(editSection).toBeVisible();

    await expect(async () => {
      await fillWarehouse(editSection, "10 Draft Way", "Sydney", "2000", "AU");
      await page.getByRole("button", { name: /^save warehouse address$/i }).click();
      await expect(page.getByText(/warehouse address saved/i)).toBeVisible({ timeout: 3_000 });
    }).toPass({ timeout: 25_000 });

    await expect(page.getByText(/10 Draft Way/i)).toBeVisible();
    await page.screenshot({ path: `${SHOT_DIR}/supplier-warehouse-editable.png`, fullPage: true });
  });

  test("once approved, the warehouse is read-only and changes go through an approved change request", async ({ page, playwright }) => {
    // Approve the supplier via the admin API: make it verification-ready (it already has a warehouse
    // address from the previous test), then activate it.
    const api = await playwright.request.newContext();
    await api.post(`${GATEWAY}/api/identity/login`, { data: { email: ADMIN_EMAIL, password: ADMIN_PASSWORD } });
    const base = `${GATEWAY}/api/entity/entities/${supplier.entityId}`;
    await api.post(`${base}/identifiers`, { data: { type: 1, value: "53004085616" } });
    const detail = await (await api.get(base)).json();
    const abn = detail.identifiers.find((i: { type: number; id: string }) => i.type === 1);
    await api.post(`${base}/identifiers/${abn.id}/verify`);
    await api.post(`${base}/contacts`, { data: { purpose: 1, kind: 1, value: `ops.${run}@example.test` } });
    await api.post(`${base}/suppliers/submit-verification`);
    await api.post(`${base}/suppliers/verification-complete`);
    await api.post(`${base}/suppliers/activate`);
    await api.dispose();

    const summary = `Relocating warehouse ${run}`;

    await supplierSignIn(page);
    await page.goto("/warehouse");
    const requestSection = page.locator("section").filter({ has: page.getByRole("heading", { name: /request a warehouse address change/i }) });
    await expect(requestSection).toBeVisible();
    await expect(page.getByText(/your account is approved/i)).toBeVisible();

    await expect(async () => {
      await fillWarehouse(requestSection, "22 Approved Road", "Melbourne", "3000", "AU");
      await setBlazorInput(requestSection.locator("textarea"), summary);
      await page.getByRole("button", { name: /^submit change request$/i }).click();
      await expect(page.getByText(/change request submitted for review/i)).toBeVisible({ timeout: 3_000 });
    }).toPass({ timeout: 25_000 });
    await page.screenshot({ path: `${SHOT_DIR}/supplier-warehouse-locked.png`, fullPage: true });

    // Admin approves the pending change request in the Entities console.
    await page.goto(`${ADMIN_URL}/login`);
    await page.getByLabel("Email").fill(ADMIN_EMAIL);
    await page.getByLabel("Password").fill(ADMIN_PASSWORD);
    await page.getByRole("button", { name: /sign in/i }).click();

    await page.goto(`${ADMIN_URL}/entities`);
    await expect(page.getByRole("heading", { name: /entities & suppliers/i })).toBeVisible();
    await expect(async () => {
      await setBlazorInput(page.getByRole("textbox").first(), TENANT);
      await page.getByRole("button", { name: /^load$/i }).click();
      await expect(page.getByText(summary)).toBeVisible({ timeout: 3_000 });
    }).toPass({ timeout: 25_000 });

    const card = page.locator("div").filter({ hasText: summary }).filter({ has: page.getByRole("button", { name: /^approve$/i }) }).last();
    await page.getByRole("textbox", { name: /decision reason/i }).fill("Verified relocation");
    await card.getByRole("button", { name: /^approve$/i }).click();
    await expect(page.getByText(/approved/i).first()).toBeVisible({ timeout: 10_000 });

    // The approved change superseded the warehouse address.
    const verify = await playwright.request.newContext();
    await verify.post(`${GATEWAY}/api/identity/login`, { data: { email: supplier.email, password: supplier.password } });
    const after = await (await verify.get(`${GATEWAY}/api/entity/entities/suppliers/me/warehouse`)).json();
    expect(after.warehouse.line1).toBe("22 Approved Road");
    expect(after.canEditDirectly).toBe(false);
    await verify.dispose();
  });
});
