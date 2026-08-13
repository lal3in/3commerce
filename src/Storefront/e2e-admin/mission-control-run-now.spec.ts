import { test, expect } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

// Mission Control → Scheduled jobs: the "Run now" action triggers a job on demand. Verifies the full
// chain (trigger → execute → record → workflow run-history projection → the row's Last run cell) by
// clicking Run now and polling until the job reports a Succeeded run.
test("Scheduled jobs: Run now triggers a job that reports Succeeded", async ({ page }) => {
  await loginAsAdmin(page);
  await page.goto("/mission-control");
  await expect(page.getByRole("heading", { name: /scheduled jobs/i })).toBeVisible({ timeout: 10_000 });
  await page.waitForTimeout(2000); // let the per-service schedules + run history load

  // The scheduled-publish job (marketing) is trivial and fast — a good on-demand target.
  const jobsTable = page.locator("table").filter({ has: page.getByRole("columnheader", { name: /last run/i }) });
  const row = jobsTable.locator("tr", { hasText: "scheduled-publish" });
  await expect(row).toBeVisible({ timeout: 10_000 });

  await row.getByRole("button", { name: "Run now", exact: true }).click();

  // Poll: reload and re-read the row until its Last run cell reports Succeeded (async record + projection).
  await expect(async () => {
    await page.reload();
    await page.waitForTimeout(1500);
    const r = jobsTable.locator("tr", { hasText: "scheduled-publish" });
    await expect(r.locator("td").nth(4)).toContainText("Succeeded");
  }).toPass({ timeout: 30_000, intervals: [2000, 3000, 5000] });
});
