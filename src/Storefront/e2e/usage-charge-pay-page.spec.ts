import fs from "node:fs";
import path from "node:path";
import { test, expect } from "../e2e-support/fixtures";
import { capture } from "../e2e-support/screenshots";

/**
 * usage_charge_pay_page: an unpaid usage charge has a storefront "complete payment" page, linked from the usage dunning
 * emails and the account page (mirroring the subscription pay page, #315). This spec records a usage overage for the demo
 * customer through the Development-only endpoint with the LocalMock "3-D Secure required" scenario, so it is past due;
 * then the account lists it under "Usage payments due" with a "Complete payment" link, the page names the reason and the
 * amount, paying shows ONLY the confirmation (never the stale amount due), and the account no longer lists it.
 * Requires dev-up --data full (the demo customer); skips otherwise.
 */
const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";
const REQUIRES_3DS = 5; // MockScenario.Requires3ds — enums cross HTTP as numbers
const OVERAGE = 1; // UsageChargeKind.Overage
const TOKEN = 1; // MeterType.Token

type Manifest = { customers?: { demo?: { email?: string; password?: string } } };

function demoCustomer(): { email: string; password: string } | null {
  const file = path.resolve(process.cwd(), "../../.run/dev-dummy-data/fixtures.json");
  if (!fs.existsSync(file)) return null;
  const manifest = JSON.parse(fs.readFileSync(file, "utf8")) as Manifest;
  const email = manifest.customers?.demo?.email;
  const password = manifest.customers?.demo?.password;
  return email && password ? { email, password } : null;
}

test.describe("Unpaid usage charge — complete payment page (usage_charge_pay_page)", () => {
  test("a usage charge that needs 3-D Secure is paid from the account's complete-payment page", async ({ page, request }) => {
    const demo = demoCustomer();
    test.skip(!demo, "demo customer not seeded (needs dev-up --data full)");
    const { email, password } = demo!;

    // A usage overage whose charge comes back "requires 3-D Secure" (LocalMock, Development only): past due.
    const charged = await request.post(`${GATEWAY}/api/payments/dev/usage-charges`, {
      data: { customerEmail: email, kind: OVERAGE, meter: TOKEN, quantity: 120, amountMinor: 2_400, currency: "AUD", scenario: REQUIRES_3DS },
    });
    expect(charged.ok(), `dev usage charge should succeed: ${charged.status()} ${await charged.text()}`).toBeTruthy();
    const charge = (await charged.json()) as { reference: string; status: string; amountDueMinor: number | null };
    expect(charge.status).toBe("PastDue");
    expect(charge.amountDueMinor).toBeGreaterThan(0);

    await page.goto("/login");
    await page.getByLabel("Email", { exact: true }).fill(email);
    await page.getByLabel("Password", { exact: true }).fill(password);
    await page.getByRole("button", { name: /log in/i }).click();
    await expect(page).toHaveURL(/\/account/);

    const list = page.getByTestId("account-usage-charges");
    await expect(page.getByRole("heading", { name: "Usage payments due" })).toBeVisible();
    await expect(list).toContainText("Payment due");
    const link = list.locator(`a[href="/account/usage-charges/${charge.reference}/pay"]`);
    await expect(link).toHaveText("Complete payment");
    await capture(page, "usage-charge-account-due");
    await link.click();
    await expect(page).toHaveURL(new RegExp(`/account/usage-charges/${charge.reference}/pay`));

    await expect(page.getByTestId("pay-usage-reason")).toContainText("Your bank needs you to confirm this payment.");
    await expect(page.getByTestId("pay-usage-item")).toContainText("Usage beyond your plan (tokens)");
    await expect(page.getByTestId("pay-usage-item")).toContainText("120 units");
    await expect(page.getByTestId("pay-usage-amount")).toHaveText(/\S/);
    await capture(page, "usage-charge-pay-page");

    await page.getByTestId("pay-usage-submit").click();
    await expect(page.getByTestId("pay-usage-paid")).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId("pay-usage-amount")).toHaveCount(0); // once paid, never the stale amount due
    await expect(page.getByTestId("pay-usage-submit")).toHaveCount(0);
    await capture(page, "usage-charge-pay-page-paid");

    // A second visit finds nothing to pay; the account no longer lists the charge.
    await page.goto(`/account/usage-charges/${charge.reference}/pay`);
    await expect(page.getByTestId("pay-usage-not-due")).toContainText("Paid");
    await page.goto("/account");
    await expect(page.locator(`a[href="/account/usage-charges/${charge.reference}/pay"]`)).toHaveCount(0);
  });
});
