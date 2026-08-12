import { test, expect, type Page } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

const CURRENCIES = ["AUD", "CAD", "CNY", "EUR", "GBP", "USD"];

// Verifies every seeded storefront currency surfaces on the money dashboards. Each page renders a
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

test("Mission Control shows a revenue tile for every currency", async ({ page }) => {
  await loginAsAdmin(page);
  await page.goto("/mission-control");
  await expect(page.getByRole("heading", { name: /mission control/i })).toBeVisible();
  await page.waitForTimeout(2000);
  for (const cur of CURRENCIES) {
    // Revenue is per-currency — one tile per currency, code-prefixed, e.g. "CNY 152.30".
    await expect(page.getByText(new RegExp(`${cur} [\\d.,]+`)).first()).toBeVisible({ timeout: 12_000 });
  }
});

// Reads the When + Description of each row from the table identified by a distinctive column header,
// skipping the expandable per-line detail rows (which carry a single colspan cell, not a date).
async function feedRows(page: Page, columnHeader: RegExp): Promise<string[]> {
  const table = page.locator("table").filter({ has: page.getByRole("columnheader", { name: columnHeader }) }).first();
  const rows = table.locator("tbody tr");
  const out: string[] = [];
  for (let i = 0; i < (await rows.count()); i++) {
    const cells = rows.nth(i).locator("td");
    if ((await cells.count()) < 2) continue;
    const when = (await cells.nth(0).innerText()).trim();
    const desc = (await cells.nth(1).innerText()).trim();
    if (/\d{2}:\d{2}/.test(when)) out.push(`${when} | ${desc}`);
  }
  return out;
}

// The dashboard's recent-ledger feed and the full Ledger page read the same /ledger/entries endpoint and
// must present entries in the SAME deterministic newest-first order (CreatedAt, then the v7 Id tiebreaker),
// so an operator glancing at the dashboard sees the same top of the ledger as the Ledger page.
test("Dashboard ledger feed matches the Ledger page order", async ({ page }) => {
  await loginAsAdmin(page);
  await page.waitForTimeout(2500);
  const dash = await feedRows(page, /^entry$/i); // dashboard feed columns: When | Entry | Amount

  await page.goto("/ledger");
  await expect(page.getByRole("heading", { name: /journal entries/i })).toBeVisible();
  await page.waitForTimeout(1500);
  const ledger = await feedRows(page, /reference/i); // ledger entries: When | Description | Reference | ...

  expect(dash.length).toBeGreaterThan(0);
  expect(ledger.length).toBeGreaterThanOrEqual(dash.length);
  // The dashboard shows the newest N; they must be exactly the first N of the Ledger page, in order.
  expect(ledger.slice(0, dash.length)).toEqual(dash);
});
