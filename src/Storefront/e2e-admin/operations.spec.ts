import { test, expect } from "@playwright/test";
import { loginAsAdmin, seedPaidOrderWithRma } from "./helpers";

const operationalPages = [
  { href: "/catalog", heading: /catalog/i, copy: /product/i },
  { href: "/offers", heading: /offers & pricing/i, copy: /new offer/i },
  { href: "/orders", heading: /orders/i, copy: /status/i },
  { href: "/commerce-ops", heading: /storefront commerce operations/i, copy: /pricing, payment, payout, and xero policy surfaces/i },
  { href: "/payment-accounts", heading: /payment accounts/i, copy: /eligible at checkout/i },
  { href: "/supplier-payouts", heading: /supplier payouts/i, copy: /payout instructions/i },
  { href: "/xero-mappings", heading: /xero mappings/i, copy: /map ledger account codes/i },
] as const;

test.describe("Admin operations surfaces", () => {
  test.beforeEach(async ({ page }) => {
    await loginAsAdmin(page);
  });

  for (const { href, heading, copy } of operationalPages) {
    test(`${href} renders its operator surface`, async ({ page }) => {
      await page.goto(href);
      await expect(page.getByRole("heading", { name: heading })).toBeVisible();
      await expect(page.getByText(copy).first()).toBeVisible();
    });
  }

  test("RMA queue exposes both immediate-refund and require-return actions for requested RMAs", async ({ page, request }) => {
    const { orderId } = await seedPaidOrderWithRma(request);

    await page.goto("/rmas");
    const row = page.locator("tr", { hasText: orderId });
    await expect(row).toBeVisible();
    await expect(row.getByRole("button", { name: /approve & refund/i })).toBeVisible();
    await expect(row.getByRole("button", { name: /approve \(require return\)/i })).toBeVisible();
    await expect(row.getByRole("button", { name: /deny/i })).toBeVisible();
  });
});
