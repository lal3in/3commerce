import { test, expect } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

// Mission Control monitors (mc1 + mc_proc): the commerce activity + per-process monitor sections must
// render live KPIs, localized (no raw resource keys). Guards the "full monitor" work.
test.describe("Mission Control monitors", () => {
  test("renders live commerce + per-process monitors with localized labels", async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto("/mission-control");
    await expect(page.getByRole("heading", { name: /mission control/i })).toBeVisible();

    // Both new monitor sections are present.
    await expect(page.getByText("Commerce activity")).toBeVisible();
    await expect(page.getByText("Process monitors")).toBeVisible();

    // Representative KPI labels from each section render (distinctive to the monitors).
    for (const label of ["Revenue", "Refunds issued", "Checkouts in-flight", "Subscriptions active", "Dropship open", "Notifications sent"]) {
      await expect(page.getByText(label, { exact: false }).first()).toBeVisible();
    }

    // No raw resource keys leaked (localization resolved).
    await expect(page.locator("body")).not.toContainText("Mc.Kpi");
    await expect(page.locator("body")).not.toContainText("Mc.Commerce");
    await expect(page.locator("body")).not.toContainText("Mc.Process");
  });
});
