import { test, expect } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

// Xero mapping target dropdown (ux_8): choosing a scope loads the REAL options for that scope from the
// owning services, so the operator selects a target by name rather than pasting a GUID.
test.describe("Xero mapping target dropdown", () => {
  test("selecting a scope populates the target with real options", async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto("/xero-mappings");
    await expect(page.getByRole("heading", { name: /xero/i }).first()).toBeVisible();

    // Load the tenant's mappings — reveals the create form (retry across the Blazor pre-circuit window).
    await expect(async () => {
      await page.getByRole("button", { name: /^load$/i }).click();
      await expect(page.locator("select").first()).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 20_000 });

    // The scope <select> carries the "Tenant default" option; pick Storefront (numeric 2). Select once —
    // re-selecting the same value fires no change event, so the @bind:after option load wouldn't refire.
    const scope = page.locator("select").filter({ has: page.locator("option", { hasText: /tenant default/i }) }).first();
    await scope.selectOption("2");

    // The target load is async; retry only the assertion until the real options arrive.
    await expect(async () => {
      const target = page.locator("select").filter({ has: page.locator("option", { hasText: /select an option/i }) }).first();
      expect(await target.locator("option").count()).toBeGreaterThan(1);
    }).toPass({ timeout: 15_000 });
  });
});
