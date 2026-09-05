import { test, expect } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";
const TENANT_ID = "00000000-0000-0000-0000-000000000001";

interface AdminPromotion {
  id: string;
  name: string;
  minimumAmountMinor: number;
  minimumQuantity: number;
  percentOff: number;
  combinable: boolean;
  grantsFreeShipping: boolean;
  storefrontId: string | null;
  code: string | null;
  maxRedemptions: number | null;
  maxRedemptionsPerCustomer: number | null;
}

// ADR-0051: a threshold promotion authored through the admin modal persists and renders its threshold
// and reward back in the table. Seeds a fresh storefront via the gateway so the promotion is scoped to a
// row this test owns and the demo stores' totals are never perturbed.
test("promotions: create a threshold promotion through the modal and see it persist", async ({ page, request }) => {
  const login = await request.post(`${GATEWAY}/api/identity/login`, {
    data: { email: "admin@3commerce.local", password: "dev-admin-password-1" },
  });
  expect(login.ok()).toBeTruthy();

  const stamp = Date.now();
  const created = await request.post(`${GATEWAY}/api/catalog/admin/storefronts`, {
    data: {
      tenantId: TENANT_ID,
      name: `Promo E2E ${stamp}`,
      visibility: 4,
      publicUrl: `http://localhost:3000/promo-${stamp}`,
      currency: "EUR",
      taxRegime: 2,
      taxRateBasisPoints: 1000,
    },
  });
  expect(created.ok()).toBeTruthy();
  const storefrontId = ((await created.json()) as { id: string }).id;

  const promotionName = `Spend 100 take 15 ${stamp}`;

  await loginAsAdmin(page);
  await page.goto("/promotions");
  await page.screenshot({ path: "test-results/promotions-admin-before.png", fullPage: true });

  await page.getByRole("button", { name: "New promotion", exact: true }).click();

  // Every fill() is followed by Tab: Blazor @bind commits on the change event, so blurring the field is
  // what gets the value onto the circuit before the save click (a real user clicking away does the same).
  const nameField = page.getByLabel("Name", { exact: true });
  await expect(nameField).toBeVisible({ timeout: 10_000 });
  await nameField.fill(promotionName);
  await nameField.press("Tab");

  const minimumAmount = page.getByLabel("Minimum amount", { exact: true });
  await minimumAmount.fill("100");
  await minimumAmount.press("Tab");

  const percentOff = page.getByLabel("Percent off", { exact: true });
  await percentOff.fill("15");
  await percentOff.press("Tab");

  // Combinable: stacks with other combinable promotions rather than winning alone.
  await page.getByLabel("Combinable", { exact: true }).check();

  // Scope the promotion to the storefront this test created, so nothing else on the stack sees it.
  // getByLabel is unusable for a <select> wrapped in its <label>: the accessible name absorbs the option
  // text, and "Storefront" is also the filter row's label. Scope by the fieldset instead.
  await page
    .locator("fieldset", { hasText: "Storefront scope & active window" })
    .locator("select")
    .selectOption(storefrontId);

  await page.getByRole("button", { name: "Save promotion", exact: true }).click();

  // The list reloads; the new row renders its threshold and its reward.
  const row = page.locator("tr", { hasText: promotionName });
  await expect(row).toBeVisible({ timeout: 10_000 });
  await expect(row).toContainText("100 EUR");
  await expect(row).toContainText("15%");
  await expect(row).toContainText("Stacks");
  await page.screenshot({ path: "test-results/promotions-admin-after.png", fullPage: true });

  // The API round-trips the values in MINOR units (100.00 EUR = 10000).
  const list = await request.get(`${GATEWAY}/api/catalog/admin/promotions?tenantId=${TENANT_ID}`);
  expect(list.ok()).toBeTruthy();
  const promotions = (await list.json()) as AdminPromotion[];
  const mine = promotions.find((p) => p.name === promotionName);
  expect(mine).toBeTruthy();
  expect(mine!.minimumAmountMinor).toBe(10_000);
  expect(mine!.percentOff).toBe(15);
  expect(mine!.combinable).toBe(true);
  expect(mine!.storefrontId).toBe(storefrontId);

  // Editing deactivates it — the promotion stops applying as soon as the projection lands.
  await page.locator("tr", { hasText: promotionName }).getByRole("button", { name: "Edit", exact: true }).click();
  await page.getByLabel("Active", { exact: true }).uncheck();
  await page.getByRole("button", { name: "Save promotion", exact: true }).click();
  await expect(page.locator("tr", { hasText: promotionName })).toContainText("Inactive", { timeout: 10_000 });
});

// ADR-0052: the same modal authors a COUPON — a code-gated promotion with usage limits. The Code and
// Redemptions columns render it, and the API round-trips the code in its canonical UPPERCASE form.
test("promotions: author a coupon code with usage limits through the modal", async ({ page, request }) => {
  const login = await request.post(`${GATEWAY}/api/identity/login`, {
    data: { email: "admin@3commerce.local", password: "dev-admin-password-1" },
  });
  expect(login.ok()).toBeTruthy();

  const stamp = Date.now();
  const created = await request.post(`${GATEWAY}/api/catalog/admin/storefronts`, {
    data: {
      tenantId: TENANT_ID,
      name: `Coupon E2E ${stamp}`,
      visibility: 4,
      publicUrl: `http://localhost:3000/coupon-${stamp}`,
      currency: "EUR",
      taxRegime: 2,
      taxRateBasisPoints: 1000,
    },
  });
  expect(created.ok()).toBeTruthy();
  const storefrontId = ((await created.json()) as { id: string }).id;

  const promotionName = `Coupon ${stamp}`;
  // Typed in lowercase on purpose: Catalog normalizes to UPPERCASE, which is what the table must show.
  const typedCode = `e2e${stamp}`;
  const canonicalCode = typedCode.toUpperCase();

  await loginAsAdmin(page);
  await page.goto("/promotions");
  await page.getByRole("button", { name: "New promotion", exact: true }).click();

  // Blazor @bind commits on change, so every fill() is followed by Tab (see the sibling test).
  const nameField = page.getByLabel("Name", { exact: true });
  await expect(nameField).toBeVisible({ timeout: 10_000 });
  await nameField.fill(promotionName);
  await nameField.press("Tab");

  const codeField = page.getByLabel("Coupon code", { exact: true });
  await codeField.fill(typedCode);
  await codeField.press("Tab");

  // Single-use overall, once per customer.
  const maxRedemptions = page.getByLabel("Max redemptions", { exact: true });
  await maxRedemptions.fill("1");
  await maxRedemptions.press("Tab");

  const maxPerCustomer = page.getByLabel("Max per customer", { exact: true });
  await maxPerCustomer.fill("1");
  await maxPerCustomer.press("Tab");

  const percentOff = page.getByLabel("Percent off", { exact: true });
  await percentOff.fill("20");
  await percentOff.press("Tab");

  await page
    .locator("fieldset", { hasText: "Storefront scope & active window" })
    .locator("select")
    .selectOption(storefrontId);

  await page.getByRole("button", { name: "Save promotion", exact: true }).click();

  // The row shows the canonical code and "0 / 1 · 1 left" — nobody has redeemed it yet.
  const row = page.locator("tr", { hasText: promotionName });
  await expect(row).toBeVisible({ timeout: 10_000 });
  await expect(row).toContainText(canonicalCode);
  await expect(row).toContainText("0 / 1");
  await page.screenshot({ path: "test-results/promotions-admin-coupon.png", fullPage: true });

  const list = await request.get(`${GATEWAY}/api/catalog/admin/promotions?tenantId=${TENANT_ID}`);
  expect(list.ok()).toBeTruthy();
  const mine = ((await list.json()) as AdminPromotion[]).find((p) => p.name === promotionName);
  expect(mine).toBeTruthy();
  expect(mine!.code).toBe(canonicalCode);
  expect(mine!.maxRedemptions).toBe(1);
  expect(mine!.maxRedemptionsPerCustomer).toBe(1);
  // A code-gated promotion needs no threshold — the code IS the gate.
  expect(mine!.minimumAmountMinor).toBe(0);
  expect(mine!.minimumQuantity).toBe(0);

  // The same code cannot be handed to a second promotion in this tenant.
  const duplicate = await request.post(`${GATEWAY}/api/catalog/admin/promotions`, {
    data: {
      tenantId: TENANT_ID,
      name: `${promotionName} clash`,
      currency: "EUR",
      scope: 1,
      storefrontId,
      percentOff: 5,
      code: canonicalCode,
    },
  });
  expect(duplicate.status()).toBe(400);
  expect(await duplicate.text()).toContain("already used");

  // Leave the stack clean: deactivate so sibling specs see the baseline.
  await request.put(`${GATEWAY}/api/catalog/admin/promotions/${mine!.id}`, { data: { active: false } });
});
