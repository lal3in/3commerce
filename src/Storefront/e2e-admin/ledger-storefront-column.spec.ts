import { test, expect } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

// The Ledger journal-entries table has a Storefront column, between Description and Reference, derived from
// the store token in each entry's line account codes and mapped to the storefront name.
test("Ledger journal entries show a Storefront column between Description and Reference", async ({ page }) => {
  await loginAsAdmin(page);
  await page.goto("/ledger");
  await expect(page.getByRole("heading", { name: /journal entries/i })).toBeVisible();
  await page.waitForTimeout(1500);

  const entries = page.locator("table").filter({ has: page.getByRole("columnheader", { name: /reference/i }) });
  // The Storefront header exists and sits between Description and Reference.
  const headers = await entries.locator("thead th").allInnerTexts();
  const norm = headers.map((h) => h.trim());
  expect(norm).toContain("Storefront");
  const iDesc = norm.indexOf("Description");
  const iStore = norm.indexOf("Storefront");
  const iRef = norm.indexOf("Reference");
  expect(iDesc).toBeLessThan(iStore);
  expect(iStore).toBeLessThan(iRef);

  // Store-scoped entries (seeded demo orders) show their storefront name.
  await expect(entries.getByText(/Demo .* Store/).first()).toBeVisible({ timeout: 10_000 });
});
