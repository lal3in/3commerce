import { test, expect } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";
const TENANT_ID = "00000000-0000-0000-0000-000000000001";

// rev_disc: the storefront-wide discount is set on Commerce ops as a percent (converted to basis points),
// persists on the storefront, and renders back in the Discount column. Seeds a fresh storefront via the
// gateway so the test owns its row and doesn't perturb the demo stores.
test("commerce ops: set a storefront-wide discount percent and see it persist", async ({ page, request }) => {
  const login = await request.post(`${GATEWAY}/api/identity/login`, {
    data: { email: "admin@3commerce.local", password: "dev-admin-password-1" },
  });
  expect(login.ok()).toBeTruthy();

  const name = `Discount E2E ${Date.now()}`;
  const created = await request.post(`${GATEWAY}/api/catalog/admin/storefronts`, {
    data: { tenantId: TENANT_ID, name, visibility: 4, publicUrl: `http://localhost:3000/disc-${Date.now()}`, currency: "EUR", taxRegime: 2, taxRateBasisPoints: 1000 },
  });
  expect(created.ok()).toBeTruthy();
  const storefrontId = ((await created.json()) as { id: string }).id;

  await loginAsAdmin(page);
  await page.goto("/commerce-ops");
  await page.getByRole("button", { name: /load storefronts/i }).click();

  const row = page.locator("tr", { hasText: name });
  await expect(row).toBeVisible({ timeout: 10_000 });
  // Before saving a discount, the column reads "—" (no discount).
  await expect(row).toContainText("—");
  await page.screenshot({ path: "test-results/discount-admin-before.png", fullPage: true });

  await row.getByRole("button", { name: "Manage", exact: true }).click();

  // The Manage form carries a "Discount %" field (the create form above has one too, so scope to the
  // manage form — the second occurrence). Enter 15% and save.
  const discountField = page.getByLabel("Discount %", { exact: true }).nth(1);
  await expect(discountField).toBeVisible({ timeout: 10_000 });
  await discountField.fill("15");
  // Blazor @bind commits on the change event — Tab blurs the field so the value reaches the circuit
  // before the save click (a real user clicking away does the same).
  await discountField.press("Tab");
  await page.getByRole("button", { name: /save storefront settings/i }).first().click();

  // The list reloads; the storefront's Discount column now shows 15%.
  const savedRow = page.locator("tr", { hasText: name });
  await expect(savedRow).toBeVisible({ timeout: 10_000 });
  await expect(savedRow).toContainText("15%");
  await page.screenshot({ path: "test-results/discount-admin-after.png", fullPage: true });

  // The API round-trips the basis points (15% = 1500 bps).
  const list = await request.get(`${GATEWAY}/api/catalog/admin/storefronts?tenantId=${TENANT_ID}`);
  const stores = (await list.json()) as Array<{ id: string; discountBasisPoints: number }>;
  const mine = stores.find((s) => s.id === storefrontId);
  expect(mine?.discountBasisPoints).toBe(1500);
});
