import { test, expect, type Locator, type Page } from "@playwright/test";

// Drives the SupplierPortal "My details" approval lock end-to-end against the running stack
// (supplier portal :5300, admin :5200, gateway :8080). A fresh supplier + login is provisioned
// per run via the admin/identity API so the test does not depend on pre-seeded onboarding state.
const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";
const ADMIN_URL = process.env.ADMIN_URL ?? "http://localhost:5200";
const TENANT = "00000000-0000-0000-0000-000000000001";
const ADMIN_EMAIL = "admin@3commerce.local";
const ADMIN_PASSWORD = "dev-admin-password-1";
const SHOT_DIR = process.env.SHOT_DIR ?? "test-results";

const run = Date.now();
const supplier = { email: `pw.details.${run}@example.test`, password: "Pw-supplier-123", entityId: "" };

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

test.describe.configure({ mode: "serial" });

test.describe("Supplier portal — My details approval lock", () => {
  // Provision a fresh Draft supplier bound to its own login (mirrors scripts/dev-dummy-data.sh).
  test.beforeAll(async ({ playwright }) => {
    const api = await playwright.request.newContext();
    await api.post(`${GATEWAY}/api/identity/login`, { data: { email: ADMIN_EMAIL, password: ADMIN_PASSWORD } });

    await api.post(`${GATEWAY}/api/identity/register`, { data: { email: supplier.email, password: supplier.password } });
    const entity = await (await api.post(`${GATEWAY}/api/entity/entities`, {
      data: { tenantId: TENANT, type: 2, legalName: `PW Details Supplier ${run}` },
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

  test("a not-yet-approved supplier can edit and save their details", async ({ page }) => {
    await supplierSignIn(page);
    await page.goto("/details");

    await expect(page.getByRole("heading", { name: /^my details$/i })).toBeVisible();
    await expect(page.getByRole("heading", { name: /^edit details$/i })).toBeVisible();

    await expect(async () => {
      await setBlazorInput(page.getByRole("textbox").nth(1), "Draft Trading Co"); // trading name field
      await page.getByRole("button", { name: /^save$/i }).click();
      await expect(page.getByText(/your details were saved/i)).toBeVisible({ timeout: 3_000 });
    }).toPass({ timeout: 25_000 });

    await page.screenshot({ path: `${SHOT_DIR}/supplier-details-editable.png`, fullPage: true });
  });

  test("once approved, details are read-only, a change request is raised, and an admin approves it", async ({ page, playwright }) => {
    // Approve the supplier via the admin API: make it verification-ready, then activate it.
    const api = await playwright.request.newContext();
    await api.post(`${GATEWAY}/api/identity/login`, { data: { email: ADMIN_EMAIL, password: ADMIN_PASSWORD } });
    const base = `${GATEWAY}/api/entity/entities/${supplier.entityId}`;
    await api.post(`${base}/identifiers`, { data: { type: 1, value: "53004085616" } });
    const detail = await (await api.get(base)).json();
    const abn = detail.identifiers.find((i: { type: number; id: string }) => i.type === 1);
    await api.post(`${base}/identifiers/${abn.id}/verify`);
    await api.post(`${base}/contacts`, { data: { purpose: 1, kind: 1, value: `ops.${run}@example.test` } });
    await api.post(`${base}/addresses`, { data: { purpose: 1, line1: "1 Supplier St", city: "Sydney", region: "NSW", postcode: "2000", countryCode: "AU" } });
    await api.post(`${base}/suppliers/submit-verification`);
    await api.post(`${base}/suppliers/verification-complete`);
    await api.post(`${base}/suppliers/activate`);
    await api.dispose();

    const summary = `Rebrand request ${run}`;
    const newTrading = `Approved Trading ${run}`;

    // Supplier now sees read-only details + a change-request form.
    await supplierSignIn(page);
    await page.goto("/details");
    await expect(page.getByRole("heading", { name: /^request a change$/i })).toBeVisible();
    await expect(page.getByText(/your account is approved/i)).toBeVisible();

    await expect(async () => {
      const boxes = page.getByRole("textbox");
      await setBlazorInput(boxes.nth(1), newTrading); // proposed trading name
      await setBlazorInput(page.locator("textarea"), summary); // reason
      await page.getByRole("button", { name: /^request a change$/i }).click();
      await expect(page.getByText(/your change request was submitted/i)).toBeVisible({ timeout: 3_000 });
    }).toPass({ timeout: 25_000 });
    await page.screenshot({ path: `${SHOT_DIR}/supplier-details-locked.png`, fullPage: true });

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
    await page.getByRole("textbox", { name: /decision reason/i }).fill("Verified rebrand");
    await card.getByRole("button", { name: /^approve$/i }).click();
    await expect(page.getByText(/approved/i).first()).toBeVisible({ timeout: 10_000 });
    await page.screenshot({ path: `${SHOT_DIR}/admin-approve-change-request.png`, fullPage: true });

    // The approved change is applied: the supplier's trading name now reflects the request.
    const verify = await playwright.request.newContext();
    await verify.post(`${GATEWAY}/api/identity/login`, { data: { email: supplier.email, password: supplier.password } });
    const after = await (await verify.get(`${GATEWAY}/api/entity/entities/suppliers/me/detail`)).json();
    expect(after.tradingName).toBe(newTrading);
    expect(after.canEditDirectly).toBe(false);
    await verify.dispose();
  });
});
