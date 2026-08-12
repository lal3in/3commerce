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

  // With no ledger activity (import-only CI) the page shows an empty-state and renders NO sections at
  // all — so the "By storefront" section is absent; skip rather than time out waiting for it.
  const byStore = page.locator("section", { hasText: "By storefront" }).last();
  test.skip((await byStore.count()) === 0, "no ledger activity in this environment (needs --data full)");
  await expect(byStore).toBeVisible();

  // The by-storefront table (and its column headers) only render when storefronts exist; skip if empty.
  test.skip((await byStore.locator("tbody tr").count()) === 0, "no storefronts configured (needs --data full)");

  // The dead "Receivable" column is gone; Gross / Net revenue / Shipping / Tax are present.
  for (const col of ["Gross", "Net revenue", "Shipping", "Tax"]) {
    await expect(byStore.getByRole("columnheader", { name: col })).toBeVisible();
  }

  // Columns: Storefront | Currency | Gross | Net revenue | Shipping | Tax | COGS | Margin | ... | Account.
  // StoreGross = net revenue + shipping income + tax (all credit-normal), so the row reconciles across
  // all three — shipping is its own income line since the ADR-0040 shipping split, not part of net revenue.
  const rows = byStore.locator("tbody tr");
  const n = await rows.count();
  for (let i = 0; i < n; i++) {
    const cells = rows.nth(i).locator("td");
    const money = async (idx: number) =>
      parseFloat((await cells.nth(idx).innerText()).replace(/,/g, "")) || 0;
    const gross = await money(2);
    const net = await money(3);
    const shipping = await money(4);
    const tax = await money(5);
    expect(Math.abs(gross - (net + shipping + tax))).toBeLessThan(0.01);
  }
});
