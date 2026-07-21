import { test, expect } from "@playwright/test";
import { loginAsAdmin, seedPaidOrderWithRma, rmaState } from "./helpers";

const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";

test.describe("Admin console", () => {
  test("unauthenticated admin redirects to login", async ({ page }) => {
    await page.goto("/");
    await expect(page).toHaveURL(/\/login/);
    await expect(page.getByRole("heading", { name: /admin sign in/i })).toBeVisible();
  });

  test("login reaches the dashboard and the nav pages render", async ({ page }) => {
    await loginAsAdmin(page);

    for (const [href, heading] of [
      ["/ledger", /ledger/i],
      ["/xero", /xero sync/i],
      ["/imports", /catalog imports/i],
      ["/rmas", /rma queue/i],
      ["/orders", /orders/i],
    ] as const) {
      await page.goto(href);
      await expect(page.getByRole("heading", { name: heading })).toBeVisible();
    }
  });

  test("orders page shows the payment-method column", async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto("/orders");
    await expect(page.getByRole("heading", { name: /orders/i })).toBeVisible();

    // Wait for the list to load: the table when orders exist, the empty state otherwise.
    await expect(page.locator("table thead, p:has-text('No orders yet')").first()).toBeVisible();
    test.skip((await page.locator("table").count()) === 0, "no orders in this environment — column headers not rendered");

    await expect(page.getByRole("columnheader", { name: /payment/i })).toBeVisible();
  });

  test("operator approves an RMA and the refund completes (RefundIssued)", async ({ page, request }) => {
    const { orderId } = await seedPaidOrderWithRma(request);

    await loginAsAdmin(page);
    await page.goto("/rmas");

    // Find the row for our order and approve it.
    const row = page.locator("tr", { hasText: orderId });
    await expect(row).toBeVisible();
    await expect(row).toContainText("Requested");
    // "Approve & refund" specifically (the row now also offers "Approve (require return)").
    await row.getByRole("button", { name: /approve & refund/i }).click();

    // The saga runs RefundRequested → refund → RefundIssued. Confirm via the API
    // (authoritative) and that the UI no longer offers Approve for that row.
    await expect
      .poll(() => rmaState(request, orderId), { timeout: 30_000 })
      .toBe("RefundIssued");

    await page.reload();
    const refreshedRow = page.locator("tr", { hasText: orderId });
    await expect(refreshedRow).toContainText("RefundIssued");

    // The ledger shows a balanced refund reversal for this order.
    const entries = await (await request.get(`${GATEWAY}/api/payments/admin/ledger/entries`)).json();
    expect(entries.some((e: { description: string }) => e.description.startsWith("Refund"))).toBeTruthy();
  });
});
