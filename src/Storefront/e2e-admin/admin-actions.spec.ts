import { test, expect } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

// Guards PR1 admin correctness: action buttons must succeed and always surface feedback
// (no silent no-ops). expect.toPass absorbs the Blazor pre-circuit window until PR2 lands.
test.describe("Admin actions give feedback", () => {
  test("creating a payment account succeeds (numeric mode) and shows a status message", async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto("/payment-accounts");
    await expect(page.getByRole("heading", { name: /payment accounts/i })).toBeVisible();

    // Load the tenant's accounts — reveals the create form.
    await expect(async () => {
      await page.getByRole("button", { name: /^load$/i }).click();
      await expect(page.getByRole("button", { name: /create \(draft\)/i })).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 20_000 });

    const section = page.locator("section", { hasText: "New payment account" });
    const name = `E2E account ${Date.now()}`;

    // Fill + submit as one retrying unit so @bind binds once the circuit is live (pre-circuit
    // race — the real fix is PR2). Mode defaults to Test (numeric 1); before the fix this 500'd.
    await expect(async () => {
      await section.getByLabel("Name").fill(name);
      await page.getByRole("button", { name: /create \(draft\)/i }).click();
      await expect(page.getByText(/account created \(draft\)/i)).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 20_000 });
  });

  test("running the sample importer surfaces feedback (not a silent no-op)", async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto("/imports");
    await expect(page.getByRole("heading", { name: /catalog imports/i })).toBeVisible();

    await expect(async () => {
      await page.getByRole("button", { name: /run sample importer/i }).click();
      await expect(page.getByText(/import run started|import failed/i)).toBeVisible({ timeout: 3_000 });
    }).toPass({ timeout: 20_000 });
  });

  test("price-entry fields carry the ADR-0038 tax-convention note (per-currency editor)", async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto("/catalog");
    await expect(page.getByRole("heading", { name: /^catalog$/i })).toBeVisible();

    await expect(async () => {
      await page.getByRole("button", { name: /new product/i }).click();
      await expect(page.getByRole("heading", { name: /new product/i })).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 20_000 });

    // The operator-visibility contract (ADR-0038): the per-currency note is on-screen, and the
    // base-price input's tooltip states the inclusive/exclusive convention.
    await expect(page.getByText(/on AU GST \/ EU VAT storefronts the price INCLUDES tax/i)).toBeVisible();
    const basePrice = page.locator('input[title*="ADR-0038"]').first();
    await expect(basePrice).toBeVisible();
    await expect(page.getByRole("button", { name: /\+ currency price/i })).toBeVisible();
  });
});
