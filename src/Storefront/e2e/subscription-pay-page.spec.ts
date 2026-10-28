import fs from "node:fs";
import path from "node:path";
import { test, expect } from "@playwright/test";
import { capture } from "../e2e-support/screenshots";

/**
 * Owner decision 2026-10-27 (subscription_past_due_retry_and_authenticate, ADR-0057 amendment): a past-due renewal
 * has a storefront "complete payment" page, linked from the 3-D Secure / dunning emails and the account page. This
 * spec puts the demo customer's monthly subscription past due through the Development-only renew endpoint with the
 * LocalMock "3-D Secure required" scenario, then pays it from the page: the account lists it as "Payment due" with
 * a "Complete payment" link; the page names the reason and the amount due; paying settles it and the subscription is
 * active again. Requires dev-up --data full (the demo customer and its subscription); skips otherwise.
 */
const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";
const REQUIRES_3DS = 5; // MockScenario.Requires3ds — enums cross HTTP as numbers

type Manifest = {
  customers?: { demo?: { email?: string; password?: string } };
  subscriptions?: Record<string, { id?: string }>;
};

function demoSubscription(): { email: string; password: string; subscriptionId: string } | null {
  const file = path.resolve(process.cwd(), "../../.run/dev-dummy-data/fixtures.json");
  if (!fs.existsSync(file)) return null;
  const manifest = JSON.parse(fs.readFileSync(file, "utf8")) as Manifest;
  const email = manifest.customers?.demo?.email;
  const password = manifest.customers?.demo?.password;
  const subscriptionId = manifest.subscriptions?.["subscription-monthly-flat"]?.id;
  return email && password && subscriptionId ? { email, password, subscriptionId } : null;
}

test.describe("Past-due subscription — complete payment page (owner decision 2026-10-27)", () => {
  test("a renewal that needs 3-D Secure is paid from the account's complete-payment page", async ({ page, request }) => {
    const demo = demoSubscription();
    test.skip(!demo, "demo customer subscription not seeded (needs dev-up --data full)");
    const { email, password, subscriptionId } = demo!;

    // Put the subscription past due: its renewal comes back "requires 3-D Secure" (LocalMock, Development only).
    const renewed = await request.post(`${GATEWAY}/api/payments/dev/subscriptions/${subscriptionId}/renew?scenario=${REQUIRES_3DS}`);
    expect(renewed.ok(), `dev renew should succeed: ${renewed.status()} ${await renewed.text()}`).toBeTruthy();
    expect(((await renewed.json()) as { status: string }).status).toBe("PastDue");

    await page.goto("/login");
    await page.getByLabel("Email", { exact: true }).fill(email);
    await page.getByLabel("Password", { exact: true }).fill(password);
    await page.getByRole("button", { name: /log in/i }).click();
    await expect(page).toHaveURL(/\/account/);

    const list = page.getByTestId("account-subscriptions");
    await expect(list).toContainText("Payment due");
    await capture(page, "subscription-account-past-due");
    await list.getByRole("link", { name: "Complete payment" }).first().click();
    await expect(page).toHaveURL(new RegExp(`/account/subscriptions/${subscriptionId}/pay`));

    await expect(page.getByTestId("pay-renewal-reason")).toContainText("Your bank needs you to confirm this payment.");
    await expect(page.getByTestId("pay-renewal-amount")).toHaveText(/\S/);
    await capture(page, "subscription-pay-page");

    await page.getByTestId("pay-renewal-submit").click();
    await expect(page.getByTestId("pay-renewal-paid")).toBeVisible({ timeout: 15_000 });
    await capture(page, "subscription-pay-page-paid");

    await page.goto("/account");
    await expect(page.getByTestId("account-subscriptions")).not.toContainText("Payment due");
  });
});
