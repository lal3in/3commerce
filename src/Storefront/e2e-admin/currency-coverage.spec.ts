import { test, expect } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

const CURRENCIES = ["AUD", "CAD", "CNY", "EUR", "GBP", "USD"];

// Verifies every seeded storefront currency surfaces on the money dashboards. Both pages render a
// per-currency section derived from the distinct currencies in the ledger balances, so all six must show.
test("Dashboard shows every currency's money block", async ({ page }) => {
  await loginAsAdmin(page); // lands on the dashboard
  await page.waitForTimeout(2000); // let the Blazor circuit load the ledger balances
  for (const cur of CURRENCIES) {
    // Each currency's card shows money rows like "7,255.85 CNY" — an amount suffixed with the ISO code,
    // distinct from the store-selector option "(CNY)". At least the Revenue row must render per currency.
    await expect(page.getByText(new RegExp(`[\\d.,]+ ${cur}\\b`)).first()).toBeVisible({ timeout: 10_000 });
  }
});

test("Financials shows a P&L section for every currency", async ({ page }) => {
  await loginAsAdmin(page);
  await page.goto("/financials");
  await expect(page.getByRole("heading", { name: /financials/i })).toBeVisible();
  await page.waitForTimeout(2000);
  for (const cur of CURRENCIES) {
    // Each currency renders its own P&L/position section with the ISO code as an <h2>.
    await expect(page.getByRole("heading", { name: cur, exact: true }).first()).toBeVisible({ timeout: 10_000 });
  }
});
