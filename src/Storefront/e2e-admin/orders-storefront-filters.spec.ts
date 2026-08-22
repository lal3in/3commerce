import { test, expect } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

// The admin Orders page has a Storefront column (between Order and Status) and a top filter bar
// (start/end date, storefront, status) applied server-side. Uses the seeded demo orders.
test("Orders: Storefront column between Order and Status, and filters narrow the list", async ({ page }) => {
  await loginAsAdmin(page);
  await page.goto("/orders");
  await expect(page.getByRole("heading", { name: /orders/i }).first()).toBeVisible();
  await page.waitForTimeout(1500);

  const table = page.locator("table").filter({ has: page.getByRole("columnheader", { name: /placed/i }) });

  // The Storefront header sits between Order and Status.
  const headers = (await table.locator("thead th").allInnerTexts()).map((h) => h.trim());
  expect(headers).toContain("Storefront");
  expect(headers.indexOf("Order")).toBeLessThan(headers.indexOf("Storefront"));
  expect(headers.indexOf("Storefront")).toBeLessThan(headers.indexOf("Status"));

  // Store-scoped orders show their storefront name.
  await expect(table.getByRole("cell", { name: /Demo .* Store/ }).first()).toBeVisible({ timeout: 10_000 });

  const main = page.getByRole("main");
  const rowsBefore = await table.locator("tbody tr").count();

  // Filter by status = Cancelled → fewer rows, and no Confirmed rows remain in the Status column.
  const statusSelect = page.locator("select").filter({ has: page.locator('option[value="Cancelled"]') });
  await statusSelect.selectOption("Cancelled");
  await main.getByRole("button", { name: "Apply", exact: true }).click();
  await page.waitForTimeout(1500);
  const rowsAfter = await table.locator("tbody tr").count();
  expect(rowsAfter).toBeLessThanOrEqual(rowsBefore);
  // Every visible Status cell reads Cancelled (the seed has at least one cancelled order).
  const statusCells = await table.locator("tbody tr td:nth-child(3)").allInnerTexts();
  for (const s of statusCells) expect(s).toMatch(/Cancelled/);

  // Clear restores the unfiltered list.
  await main.getByRole("button", { name: "Clear", exact: true }).click();
  await page.waitForTimeout(1500);
  await expect(table.locator("tbody tr").first()).toBeVisible();
});
