import fs from "node:fs";
import path from "node:path";
import { test, expect, type APIRequestContext, type Locator } from "@playwright/test";
import { loginAsAdmin } from "./helpers";
import { capture } from "../e2e-support/screenshots";

/**
 * admin_recurring_charge_refund_action (ADR-0041 amendment 2026-10-28, renewal_usage_refund_path): each settled renewal
 * of a subscription is a payment of its own, and the Subscriptions page refunds it from the renewal history — in part,
 * then the rest — through POST /admin/refunds with its chargeReference. The spec renews a seeded subscription (the
 * LocalMock rail settles it), then on the page: period 1 reads "Order" (the order paid it), the new period shows its
 * charge "Not refunded"; Refund opens a form prefilled with the full amount; an amount above what is left is refused
 * in the form; half the charge with a reason → the confirm step names the amount → "Partially refunded" with the
 * refunded amount; the rest → "Refunded" and no Refund button. The API agrees at each step (refundedMinor, payment
 * status). Requires dev-up --data full (seeded subscriptions); skips otherwise.
 */
const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";
const TENANT_ID = "00000000-0000-0000-0000-000000000001";
const PAYMENT_REFUNDED = 4; // PaymentStatus.Refunded — enums cross HTTP as numbers

type Subscription = { id: string; status: string };
type Renewal = { sequence: number; amountMinor: number; currency: string; chargeReference?: string | null; refundedMinor?: number | null; paymentStatus?: number | null };
type Detail = { id: string; renewals: Renewal[] };
type Manifest = { subscriptions?: Record<string, { id?: string }> };

function manifestSubscriptions(): Record<string, { id?: string }> {
  const file = path.resolve(process.cwd(), "../../.run/dev-dummy-data/fixtures.json");
  if (!fs.existsSync(file)) return {};
  return (JSON.parse(fs.readFileSync(file, "utf8")) as Manifest).subscriptions ?? {};
}

// The currency's minor-unit digits (USD 2, JPY 0), to type a major-unit amount the way the operator would.
function digits(currency: string): number {
  try {
    return new Intl.NumberFormat("en", { style: "currency", currency }).resolvedOptions().maximumFractionDigits ?? 2;
  } catch {
    return 2; // a test-only code Intl does not know — the admin defaults to 2 as well
  }
}

const major = (minor: number, currency: string) => (minor / 10 ** digits(currency)).toFixed(digits(currency));

// How the admin renders an amount (Money.WithCode: grouped, the currency's decimals, then the code) — "1,083.00 USD".
const shown = (minor: number, currency: string) =>
  `${(minor / 10 ** digits(currency)).toLocaleString("en-US", { minimumFractionDigits: digits(currency), maximumFractionDigits: digits(currency) })} ${currency}`;

async function detail(request: APIRequestContext, id: string): Promise<Detail> {
  const response = await request.get(`${GATEWAY}/api/payments/admin/subscriptions/${id}?tenantId=${TENANT_ID}`);
  expect(response.ok(), `subscription detail: ${response.status()}`).toBeTruthy();
  return (await response.json()) as Detail;
}

// An Active subscription to renew: the seeded yearly one first, never the monthly one (the storefront
// complete-payment spec puts that past due), else any other active subscription.
async function activeSubscription(request: APIRequestContext): Promise<string | null> {
  const seeded = manifestSubscriptions();
  const avoid = seeded["subscription-monthly-flat"]?.id;
  const list = (await (await request.get(`${GATEWAY}/api/payments/admin/subscriptions/?tenantId=${TENANT_ID}`)).json()) as Subscription[];
  const active = list.filter((s) => s.status === "Active" && s.id !== avoid);
  const preferred = seeded["subscription-yearly-tiered"]?.id;
  return active.find((s) => s.id === preferred)?.id ?? active[0]?.id ?? null;
}

// The amount field holds a major-unit number (10.8 or 10.80 alike) — compare it as a number of minor units.
async function expectAmount(input: Locator, minor: number, currency: string) {
  await expect.poll(async () => Math.round(Number(await input.inputValue()) * 10 ** digits(currency))).toBe(minor);
}

async function submitRefund(form: Locator, amount: string, reason: string, confirmLabel: RegExp) {
  await form.getByLabel(/amount/i).fill(amount);
  await form.getByLabel(/reason/i).fill(reason);
  // Blazor @bind commits on change — Tab blurs the field so the value reaches the circuit before the click.
  await form.getByLabel(/reason/i).press("Tab");
  await form.getByRole("button", { name: /^refund$/i }).click();
  const confirm = form.getByRole("button", { name: confirmLabel });
  await expect(confirm).toBeVisible();
  return confirm;
}

test.describe("Subscriptions — refund a renewal's charge (admin_recurring_charge_refund_action)", () => {
  test("a settled renewal is refunded in part, then in full, from the renewal history", async ({ page, request }) => {
    const login = await request.post(`${GATEWAY}/api/identity/login`, {
      data: { email: "admin@3commerce.local", password: "dev-admin-password-1" },
    });
    expect(login.ok()).toBeTruthy();

    const subscriptionId = await activeSubscription(request);
    test.skip(!subscriptionId, "no active subscription seeded (needs dev-up --data full)");

    // Renew it now: the LocalMock rail settles the charge, which records the renewal's own payment (its chargeReference).
    const renewed = await request.post(`${GATEWAY}/api/payments/admin/subscriptions/${subscriptionId}/renew?tenantId=${TENANT_ID}`, {
      headers: { "Idempotency-Key": `e2e-charge-refund-renew-${Date.now()}` },
    });
    expect(renewed.ok(), `renew: ${renewed.status()} ${await renewed.text()}`).toBeTruthy();
    expect(((await renewed.json()) as Subscription).status).toBe("Active");

    const before = await detail(request, subscriptionId!);
    const charge = before.renewals.filter((r) => r.chargeReference).sort((a, b) => b.sequence - a.sequence)[0];
    expect(charge, "the renewal just settled carries its chargeReference").toBeTruthy();
    expect(charge.refundedMinor ?? 0).toBe(0);
    const reference = charge.chargeReference!;
    const half = Math.floor(charge.amountMinor / 2);
    const rest = charge.amountMinor - half;

    await loginAsAdmin(page);
    await page.goto("/subscriptions");
    const subRow = page.locator(`tr[data-subscription="${subscriptionId}"]`);
    await expect(subRow).toBeVisible({ timeout: 30_000 });
    await subRow.locator("td").first().locator("button").click();
    await expect(page.getByRole("heading", { name: /renewal history/i })).toBeVisible({ timeout: 10_000 });

    // Period 1 has no charge of its own — the order paid it, so it is refunded from Orders (tooltip says so).
    const history = page.locator("section", { has: page.getByRole("heading", { name: /renewal history/i }) });
    const period1 = history.locator("tbody tr").first(); // the history is ordered by period
    await expect(period1.locator("td").first()).toHaveText("#1");
    await expect(period1).toContainText("Order");
    await expect(period1.getByRole("button", { name: /^refund$/i })).toHaveCount(0);

    const row = history.locator(`tr[data-charge="${reference}"]`);
    await expect(row).toContainText("Not refunded");
    await expect(row.getByRole("button", { name: /^refund$/i })).toHaveAttribute("title", /in full or in part/i);

    // Refund opens the form, prefilled with a full refund of what is left.
    await row.getByRole("button", { name: /^refund$/i }).click();
    const form = history.locator(".charge-refund-form");
    await expect(form).toBeVisible();
    await expectAmount(form.getByLabel(/amount/i), charge.amountMinor, charge.currency);
    await expect(form.getByLabel(/amount/i)).toHaveAttribute("title", /at most .*tax included/i);
    await expect(form.getByLabel(/reason/i)).toHaveAttribute("title", /audit/i);

    // More than the charge is refused in the form — nothing is sent.
    await form.getByLabel(/amount/i).fill(major(charge.amountMinor + 100, charge.currency));
    await form.getByLabel(/reason/i).fill("too much");
    await form.getByLabel(/reason/i).press("Tab");
    await form.getByRole("button", { name: /^refund$/i }).click();
    await expect(form).toContainText(/at most/i);

    // Half of it: the first click arms the confirm step, which names the amount.
    let confirm = await submitRefund(form, major(half, charge.currency), "E2E partial renewal refund", /confirm refund of/i);
    await expect(confirm).toContainText(shown(half, charge.currency));
    await capture(page, "renewal-refund-confirm");
    await confirm.click();

    await expect(row).toContainText("Partially refunded", { timeout: 15_000 });
    await expect(row).toContainText(`${shown(half, charge.currency)} refunded`);
    await capture(page, "renewal-charge-partially-refunded", { docName: "admin-subscription-charge-refund.png" });
    const partial = (await detail(request, subscriptionId!)).renewals.find((r) => r.chargeReference === reference)!;
    expect(partial.refundedMinor).toBe(half);

    // The rest: the form now defaults to what is left, and the charge ends fully refunded with no Refund button.
    await row.getByRole("button", { name: /^refund$/i }).click();
    await expectAmount(form.getByLabel(/amount/i), rest, charge.currency);
    confirm = await submitRefund(form, major(rest, charge.currency), "E2E refund the rest", /confirm refund of/i);
    await confirm.click();

    await expect(row).toContainText("Refunded", { timeout: 15_000 });
    await expect(row).not.toContainText("Partially refunded");
    await expect(row.getByRole("button", { name: /^refund$/i })).toHaveCount(0);
    await capture(page, "renewal-charge-refunded");
    const full = (await detail(request, subscriptionId!)).renewals.find((r) => r.chargeReference === reference)!;
    expect(full.refundedMinor).toBe(charge.amountMinor);
    expect(full.paymentStatus).toBe(PAYMENT_REFUNDED);
  });
});
