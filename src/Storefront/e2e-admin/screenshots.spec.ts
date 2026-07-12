import { test } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

// Captures Blazor admin screenshots for the Operations Wiki. Run against a live stack:
//   GATEWAY_URL=http://localhost:8080 npm run test:e2e -- --project=admin -g screenshots
const OUT = "../../docs/help/assets/screenshots";

async function shot(page: import("@playwright/test").Page, name: string) {
  await page.waitForLoadState("networkidle").catch(() => {});
  await page.waitForTimeout(500);
  await page.screenshot({ path: `${OUT}/admin-${name}.png`, fullPage: true });
}

// Data-backed pages (payment accounts, supplier payouts, offers, mission control, security)
// auto-load in OnInitializedAsync from the pre-filled seed tenant once the Blazor circuit
// connects, so allow extra time for the interactive render before capturing.
async function shotInteractive(page: import("@playwright/test").Page, name: string) {
  await page.waitForLoadState("networkidle").catch(() => {});
  await page.waitForTimeout(2000);
  await page.screenshot({ path: `${OUT}/admin-${name}.png`, fullPage: true });
}

test("admin screenshots", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 });

  await page.goto("/login");
  await shot(page, "login");

  await loginAsAdmin(page);
  await shot(page, "dashboard");

  for (const [href, name] of [
    ["/orders", "orders"], ["/ledger", "ledger"], ["/rmas", "rmas"],
    ["/entities", "entities"], ["/imports", "imports"], ["/xero", "xero"],
    ["/rbac", "rbac"],
    ["/commerce-ops", "commerce-ops"], ["/catalog", "catalog"],
  ] as const) {
    await page.goto(href);
    await shot(page, name);
  }

  // Newer admin surfaces. These render live data through the interactive Blazor circuit
  // (audit timeline + bus stats on mission control; seed-tenant rows on the commerce pages),
  // so they use the longer interactive wait. Tenant ID is pre-filled to the seed tenant, so
  // the pages self-load — no record is created or deleted (read-only).
  for (const [href, name] of [
    ["/security", "security"],
    ["/payment-accounts", "payment-accounts"],
    ["/supplier-payouts", "supplier-payouts"],
    ["/offers", "offers"],
    ["/mission-control", "mission-control"],
  ] as const) {
    await page.goto(href);
    await shotInteractive(page, name);
  }
});
