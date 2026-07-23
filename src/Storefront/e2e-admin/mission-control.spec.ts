import { test, expect, request as pwRequest } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

async function reachable(url: string): Promise<boolean> {
  const ctx = await pwRequest.newContext();
  try {
    const res = await ctx.get(url, { timeout: 4000 });
    return res.status() < 500;
  } catch {
    return false;
  } finally {
    await ctx.dispose();
  }
}

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

    // RMA lifecycle is broken out into distinguishable tiles (was one lumped "open" tile): every
    // waiting state plus Denied and Refunds issued render with localized labels.
    for (const label of ["awaiting decision", "awaiting return", "refund pending", "denied", "Refunds issued"]) {
      await expect(page.getByText(label, { exact: false }).first()).toBeVisible();
    }

    // Revenue is per-currency: each Revenue tile's value is prefixed with its ISO currency code
    // (e.g. "EUR 59.05"); with no confirmed orders a single "0.00" placeholder tile renders.
    const revenueTile = page.locator("div[title]", { hasText: "Revenue" }).first();
    await expect(revenueTile).toBeVisible();
    await expect(revenueTile).toContainText(/[A-Z]{3} [\d,]+\.\d\d|0\.00/);

    // No raw resource keys leaked (localization resolved).
    await expect(page.locator("body")).not.toContainText("Mc.Kpi");
    await expect(page.locator("body")).not.toContainText("Mc.Commerce");
    await expect(page.locator("body")).not.toContainText("Mc.Process");
  });

  test("renders the Observability section with all three LGTM backends up", async ({ page }) => {
    // The section itself must always render; the up/down assertion only makes sense when the
    // LGTM containers (compose observability profile, always-on via dev-up) are actually running.
    const lgtmUp = (
      await Promise.all([
        reachable("http://localhost:3100/ready"),
        reachable("http://localhost:3200/ready"),
        reachable("http://localhost:9009/ready"),
      ])
    ).every(Boolean);

    await loginAsAdmin(page);
    await page.goto("/mission-control");
    await expect(page.getByRole("heading", { name: "Observability" })).toBeVisible();

    // The signal-volume KPI tiles render with localized labels (no raw Mc.Obs keys).
    for (const label of ["Log lines (1h)", "Error logs (1h)", "Traces (1h)", "Services reporting"]) {
      await expect(page.getByText(label, { exact: false }).first()).toBeVisible();
    }
    await expect(page.locator("body")).not.toContainText("Mc.Obs");

    // Grafana Explore console deep links (provisioned datasource uids loki/tempo/mimir).
    for (const uid of ["loki", "tempo", "mimir"]) {
      await expect(page.locator(`a[href*="explore"][href*="${uid}"]`).first()).toBeVisible();
    }

    test.skip(!lgtmUp, "LGTM stack not running — backend tiles would legitimately read down");
    // Each per-backend tile (scoped by its distinctive tooltip) reads "up" ("up" is not a
    // substring of "down", so this cannot false-pass on a down backend).
    for (const backend of ["Loki log store", "Tempo trace store", "Mimir metric store"]) {
      await expect(page.locator(`div[title*="${backend}"]`)).toContainText("up", { ignoreCase: true });
    }
  });
});
