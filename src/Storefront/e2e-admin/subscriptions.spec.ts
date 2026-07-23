import { test, expect } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

// Subscriptions management (mt7_3): the operator page lists recurring subscriptions with their status
// and a per-row renewal history. This asserts the page renders with its localized table headers, and —
// when the environment has seeded subscriptions — that a color-coded status shows.
test.describe("Subscriptions", () => {
  test("page renders the table with localized headers and a status", async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto("/subscriptions");
    await expect(page.getByRole("heading", { name: /subscriptions/i })).toBeVisible();

    // The list is fetched after the Blazor circuit connects: wait for either the table (subscriptions
    // exist) or the empty-state paragraph. Retry across the pre-circuit window like the other specs.
    await expect(async () => {
      const ready = await page
        .locator("table thead, p:has-text('No subscriptions yet')")
        .first()
        .isVisible();
      expect(ready).toBeTruthy();
    }).toPass({ timeout: 20_000 });

    // No rows in this environment (the CI stack may seed none) — headers aren't rendered, so stop here,
    // exactly as the orders spec skips when there are no orders.
    test.skip((await page.locator("table").count()) === 0, "no subscriptions in this environment");

    // Localized column headers are present.
    for (const header of [/subscriber/i, /product/i, /status/i, /billing cycle/i, /current period/i]) {
      await expect(page.getByRole("columnheader", { name: header })).toBeVisible();
    }

    // At least one row shows one of the known (localized-EN) statuses.
    const firstRow = page.locator("table tbody tr").first();
    await expect(firstRow).toContainText(/Active|Trialing|Past due|Cancelled/);

    // Expanding a row (the toggle in its first cell) lazily loads the detail + renewal-history timeline.
    await firstRow.locator("td").first().locator("button").click();
    await expect(page.getByRole("heading", { name: /renewal history/i })).toBeVisible({ timeout: 10_000 });
  });
});
