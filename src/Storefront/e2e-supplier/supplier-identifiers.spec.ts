import { test, expect, type Locator, type Page } from "@playwright/test";

// Drives the SupplierPortal "My details" identifiers (ABN/ACN) end-to-end against the running stack
// (supplier portal :5300, gateway :8080). A fresh supplier + login is provisioned per run via the
// admin/identity API so the test does not depend on pre-seeded onboarding state. Mirrors the approval
// lock: identifiers are editable/addable while onboarding, read-only once the supplier is approved.
const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";
const TENANT = "00000000-0000-0000-0000-000000000001";
const ADMIN_EMAIL = "admin@3commerce.local";
const ADMIN_PASSWORD = "dev-admin-password-1";
const SHOT_DIR = process.env.SHOT_DIR ?? "test-results";

const run = Date.now();
const supplier = { email: `pw.ident.${run}@example.test`, password: "Pw-supplier-123", entityId: "" };

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

test.describe("Supplier portal — identifiers (ABN/ACN)", () => {
  // Provision a fresh Draft supplier bound to its own login (mirrors scripts/dev-dummy-data.sh).
  test.beforeAll(async ({ playwright }) => {
    const api = await playwright.request.newContext();
    await api.post(`${GATEWAY}/api/identity/login`, { data: { email: ADMIN_EMAIL, password: ADMIN_PASSWORD } });
    await api.post(`${GATEWAY}/api/identity/register`, { data: { email: supplier.email, password: supplier.password } });
    const entity = await (await api.post(`${GATEWAY}/api/entity/entities`, {
      data: { tenantId: TENANT, type: 2, legalName: `PW Ident Supplier ${run}` },
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

  test("a not-yet-approved supplier can add an ABN and it shows up labelled", async ({ page }) => {
    await supplierSignIn(page);
    await page.goto("/details");
    await expect(page.getByRole("heading", { name: /registration identifiers/i })).toBeVisible();

    await expect(async () => {
      await setBlazorInput(page.getByPlaceholder(/identifier number/i), "51824753556");
      await page.getByRole("button", { name: /add identifier/i }).click();
      await expect(page.getByText(/identifier added/i)).toBeVisible({ timeout: 3_000 });
    }).toPass({ timeout: 25_000 });

    // The ABN is now listed with its label (the list item carries both the ABN label and the value).
    const identSection = page.locator("section").filter({ has: page.getByRole("heading", { name: /registration identifiers/i }) });
    const abnRow = identSection.locator("li").filter({ hasText: "51824753556" });
    await expect(abnRow).toBeVisible();
    await expect(abnRow).toContainText("ABN");
    await page.screenshot({ path: `${SHOT_DIR}/supplier-identifiers-editable.png`, fullPage: true });
  });

  test("once approved, identifiers are read-only (no add form)", async ({ page, playwright }) => {
    // Approve the supplier via the admin API: verify the ABN, add contact + address, then activate.
    const api = await playwright.request.newContext();
    await api.post(`${GATEWAY}/api/identity/login`, { data: { email: ADMIN_EMAIL, password: ADMIN_PASSWORD } });
    const base = `${GATEWAY}/api/entity/entities/${supplier.entityId}`;
    const detail = await (await api.get(base)).json();
    const abn = detail.identifiers.find((i: { type: number; id: string }) => i.type === 1);
    await api.post(`${base}/identifiers/${abn.id}/verify`);
    await api.post(`${base}/contacts`, { data: { purpose: 1, kind: 1, value: `ops.${run}@example.test` } });
    await api.post(`${base}/addresses`, { data: { purpose: 1, line1: "1 Supplier St", city: "Sydney", region: "NSW", postcode: "2000", countryCode: "AU" } });
    await api.post(`${base}/suppliers/submit-verification`);
    await api.post(`${base}/suppliers/verification-complete`);
    await api.post(`${base}/suppliers/activate`);
    await api.dispose();

    await supplierSignIn(page);
    await page.goto("/details");
    await expect(page.getByRole("heading", { name: /registration identifiers/i })).toBeVisible();
    // ABN still shown, but the add form is gone and the locked hint appears.
    const identSection = page.locator("section").filter({ has: page.getByRole("heading", { name: /registration identifiers/i }) });
    await expect(identSection.getByText("51824753556")).toBeVisible();
    await expect(page.getByRole("button", { name: /add identifier/i })).toHaveCount(0);
    await expect(identSection.getByText(/identifiers are read-only/i)).toBeVisible();
    await page.screenshot({ path: `${SHOT_DIR}/supplier-identifiers-locked.png`, fullPage: true });

    // Server-side lock: a direct POST to the self identifiers endpoint is refused once approved.
    const supApi = await playwright.request.newContext();
    await supApi.post(`${GATEWAY}/api/identity/login`, { data: { email: supplier.email, password: supplier.password } });
    const locked = await supApi.post(`${GATEWAY}/api/entity/entities/suppliers/me/identifiers`, { data: { type: 2, value: "004085616" } });
    expect(locked.status()).toBe(400);
    await supApi.dispose();
  });
});
