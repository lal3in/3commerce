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

    // The table (with its header row) always renders once the Blazor circuit connects — the tbody is
    // simply empty when there are no subscriptions. Wait for the thead across the pre-circuit window.
    await expect(page.locator("table thead")).toBeVisible({ timeout: 30_000 });

    // Localized column headers are present regardless of row count (real coverage even on an empty seed).
    // Storefront sits between Subscriber and Product, so a subscription's owning store is visible.
    for (const header of [/subscriber/i, /storefront/i, /product/i, /status/i, /billing cycle/i, /current period/i]) {
      await expect(page.getByRole("columnheader", { name: header })).toBeVisible();
    }

    // No rows in this environment (e.g. an import-only CI seed) — the row-level assertions below don't
    // apply, so stop here after verifying the page + headers render.
    test.skip((await page.locator("table tbody tr").count()) === 0, "no subscriptions in this environment");

    // At least one row shows one of the known (localized-EN) statuses.
    const firstRow = page.locator("table tbody tr").first();
    await expect(firstRow).toContainText(/Active|Trialing|Past due|Cancelled/);

    // Expanding a row (the toggle in its first cell) lazily loads the detail + renewal-history timeline.
    await firstRow.locator("td").first().locator("button").click();
    await expect(page.getByRole("heading", { name: /renewal history/i })).toBeVisible({ timeout: 10_000 });
  });
});
