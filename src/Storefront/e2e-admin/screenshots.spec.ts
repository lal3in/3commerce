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

test("admin screenshots", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 });

  await page.goto("/login");
  await shot(page, "login");

  await loginAsAdmin(page);
  await shot(page, "dashboard");

  for (const [href, name] of [
    ["/orders", "orders"], ["/ledger", "ledger"], ["/rmas", "rmas"],
    ["/entities", "entities"], ["/imports", "imports"], ["/xero", "xero"],
    ["/rbac", "rbac"], ["/mission-control", "mission-control"],
    ["/commerce-ops", "commerce-ops"], ["/catalog", "catalog"],
  ] as const) {
    await page.goto(href);
    await shot(page, name);
  }
});
