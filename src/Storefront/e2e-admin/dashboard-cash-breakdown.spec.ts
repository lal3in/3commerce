import { test, expect } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

// The Dashboard's per-currency "How this adds up" disclosure must reconcile: Net revenue + Shipping income
// + Tax collected − Fees = the Cash on hand shown above it (and the ledger cash.* balance). A regression
// here (e.g. subtracting accrued COGS as if it were a cash fee) makes the total not add up.
const num = (s: string) => Number(s.replace(/[^\d.-]/g, ""));

test("Dashboard: each currency's cash breakdown adds up to Cash on hand", async ({ page }) => {
  await loginAsAdmin(page); // lands on the dashboard
  await page.waitForTimeout(2500); // let the ledger balances load

  // Expand every "How this adds up" disclosure so the total rows render.
  const summaries = page.getByText("How this adds up");
  const count = await summaries.count();
  expect(count).toBeGreaterThan(0);
  for (let i = 0; i < count; i++) await summaries.nth(i).click();
  await page.waitForTimeout(400);

  // One "= Cash on hand" total row per currency. For each, its total must equal the "Cash on hand" row
  // of the same currency (matched by the ISO code the amount cell is suffixed with).
  const totalRows = page.getByRole("row").filter({ hasText: "= Cash on hand" });
  const n = await totalRows.count();
  expect(n).toBeGreaterThan(0);

  for (let i = 0; i < n; i++) {
    const totalText = (await totalRows.nth(i).locator("td").last().innerText()).trim(); // e.g. "7,775.15 AUD"
    const cur = totalText.split(/\s+/).pop()!; // ISO code
    const cashCell = page.getByRole("row")
      .filter({ has: page.locator('td:text-is("Cash on hand")') })
      .filter({ hasText: new RegExp(`\\b${cur}\\b`) })
      .locator("td").last();
    await expect(cashCell).toHaveCount(1);
    const cash = num(await cashCell.innerText());
    const total = num(totalText);
    expect(total, `${cur}: breakdown total ${total} must equal Cash on hand ${cash}`).toBe(cash);
  }
});
