import { test, expect } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

// ledger_sf_4: the Financials page surfaces each storefront's OWN chart of accounts. Selecting a store
// lists every account a movement for it posts to — all storefront-scoped (…store-{id}), never a shared
// platform-default code. This validates the per-storefront ledger-attribution invariant through the UI.
test("Financials per-storefront chart lists only store-scoped accounts", async ({ page }) => {
  await loginAsAdmin(page);
  await page.goto("/financials");
  await expect(page.getByRole("heading", { name: /financials/i })).toBeVisible();
  await page.waitForTimeout(2000); // let the Blazor circuit load balances/storefronts

  // The chart section only renders once there is ledger activity (same as the by-storefront table);
  // an import-only stack shows the empty-state with no sections — skip rather than fail.
  const chart = page.locator("section", { hasText: "Chart of accounts" });
  test.skip((await chart.count()) === 0, "no ledger activity in this environment (needs --data full)");
  await expect(chart).toBeVisible();

  // Pick the first real storefront in the selector (index 0 is the "—" placeholder).
  const select = chart.locator("select");
  const options = await select.locator("option").count();
  test.skip(options < 2, "no storefronts configured (needs --data full)");
  await select.selectOption({ index: 1 });

  // The chart table lists the store's account codes; each <code> cell must be storefront-scoped.
  const codes = chart.locator("tbody tr td code");
  await expect(codes.first()).toBeVisible({ timeout: 10_000 });
  const n = await codes.count();
  expect(n).toBeGreaterThan(0);
  for (let i = 0; i < n; i++) {
    const code = (await codes.nth(i).innerText()).trim();
    expect(code, `account "${code}" must be storefront-scoped`).toContain(".store-");
  }

  // And explicitly: not one shared/default account appears in a store's chart.
  const shared = ["revenue.sales", "liability.supplier_payable", "liability.carrier_payable", "cash.stripe", "expense.cogs"];
  const allCodes: string[] = [];
  for (let i = 0; i < n; i++) allCodes.push((await codes.nth(i).innerText()).trim());
  for (const s of shared) {
    expect(allCodes, `shared account "${s}" must not appear in a store chart`).not.toContain(s);
  }
});
