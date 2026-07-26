import { test, expect } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

// Financials (fin_tax): the per-storefront table exposes Gross / Net revenue / Tax, and each row must
// reconcile — Gross = Net revenue + Tax. Tax is now a real ledger figure (the sale books net revenue +
// a tax liability), so the tax column is no longer structurally zero. Assertions are arithmetic
// relationships rather than seed-specific amounts, so this is stable across environments.
test("Financials by-storefront table reconciles: gross = net revenue + tax", async ({ page }) => {
  await loginAsAdmin(page);
  await page.goto("/financials");
  await expect(page.getByRole("heading", { name: /financials/i })).toBeVisible();
  await page.waitForTimeout(2000); // let the Blazor circuit load balances/storefronts

  const byStore = page.locator("section", { hasText: "By storefront" }).last();
  await expect(byStore).toBeVisible();

  // The dead "Receivable" column is gone; Gross / Net revenue / Tax are present.
  for (const col of ["Gross", "Net revenue", "Tax"]) {
    await expect(byStore.getByRole("columnheader", { name: col })).toBeVisible();
  }

  // Columns: Storefront | Currency | Gross | Net revenue | Tax | Revenue account.
  const rows = byStore.locator("tbody tr");
  const n = await rows.count();
  for (let i = 0; i < n; i++) {
    const cells = rows.nth(i).locator("td");
    const money = async (idx: number) =>
      parseFloat((await cells.nth(idx).innerText()).replace(/,/g, "")) || 0;
    const gross = await money(2);
    const net = await money(3);
    const tax = await money(4);
    expect(Math.abs(gross - (net + tax))).toBeLessThan(0.01);
  }
});
