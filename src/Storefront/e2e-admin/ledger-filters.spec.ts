import { test, expect } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

// The Ledger page's Journal entries: the When column is now a full date/time, and the entries can be
// filtered by start/end date, storefront and currency (server-side). Uses the seeded multi-currency ledger.
test("Journal entries show a full date/time and filter by currency", async ({ page }) => {
  await loginAsAdmin(page);
  await page.goto("/ledger");
  await expect(page.getByRole("heading", { name: /journal entries/i })).toBeVisible();
  await page.waitForTimeout(1500);

  // Scope to the page content — the sidebar has its own "Apply" (the language switcher).
  const main = page.getByRole("main");

  // The entries table is the one with a Reference column; its first column header is "Date/time".
  const entries = page.locator("table").filter({ has: page.getByRole("columnheader", { name: /reference/i }) });
  await expect(entries.getByRole("columnheader", { name: "Date/time" })).toBeVisible();
  // At least one entry renders a full timestamp (yyyy-MM-dd HH:mm:ss), not just a day + hour:minute.
  await expect(entries.getByText(/^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$/).first()).toBeVisible({ timeout: 10_000 });

  // Filter by JPY (a seeded 0-decimal currency): after Apply, the entries table shows JPY and no EUR.
  const currency = page.locator("select").filter({ has: page.locator('option[value="JPY"]') });
  await expect(currency).toBeVisible({ timeout: 10_000 });
  await currency.selectOption("JPY");
  await main.getByRole("button", { name: "Apply", exact: true }).click();
  await page.waitForTimeout(1500);
  await expect(entries.getByRole("cell", { name: "JPY", exact: true }).first()).toBeVisible({ timeout: 10_000 });
  await expect(entries.getByRole("cell", { name: "EUR", exact: true })).toHaveCount(0);

  // Clear restores the unfiltered feed (EUR entries return).
  await main.getByRole("button", { name: "Clear", exact: true }).click();
  await page.waitForTimeout(1500);
  await expect(entries.getByRole("cell", { name: "EUR", exact: true }).first()).toBeVisible({ timeout: 10_000 });
});
