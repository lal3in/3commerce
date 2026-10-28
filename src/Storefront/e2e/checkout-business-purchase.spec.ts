import type { APIRequestContext, Page } from "@playwright/test";
import { test, expect, pinStorefront } from "./fixtures";
import { capture } from "../e2e-support/screenshots";

/**
 * ADR-0063 P3 — buyer identity at checkout, in a real browser: a guest and a member each check out with and without
 * the optional business-purchase block (company name + tax ID + scheme), with a separate bill-to or billed to the
 * shipping address. A malformed tax ID is refused by Ordering (400 problem+json naming `buyer.taxId`) and the form
 * explains it in the shopper's language; nothing about the price moves when the block is filled in (no reverse
 * charge until phase 2). The guest's order is read back through the admin API; the member reads it on the account
 * page. Requires a demo storefront with a published catalog (dev-up --data full); skips otherwise.
 */
const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";
const ADMIN = { email: "admin@3commerce.local", password: "dev-admin-password-1" };
const EU_VAT = 5; // TaxIdScheme.EuVat — enums cross HTTP as numbers
const ABN = 1; // TaxIdScheme.Abn

test.describe("Checkout bill-to + business purchase (ADR-0063 P3)", () => {
  test("a guest checks out as a business with a separate bill-to; a malformed tax ID is refused naming it", async ({ page, request }) => {
    test.skip(!(await pinStorefront(page)), "no demo storefront with a published catalog (needs --data full)");
    await addWarehouseProduct(page);
    const email = `business-guest-${Date.now()}@example.com`;
    await fillGuestCheckout(page, email);

    // The price summary is the same before and after the business block is filled in.
    const summary = reviewSection(page);
    const before = await summary.innerText();
    const business = await openBusinessBlock(page);
    await business.getByLabel("Company name").fill("Acme GmbH");
    await business.getByLabel("Tax ID (optional)").fill("DE12345678"); // DE needs 9 digits
    await business.getByLabel("Tax ID type").selectOption(String(EU_VAT));
    expect(await summary.innerText()).toBe(before);
    await capture(page, "checkout-business-block-filled");

    // Refused: Ordering names buyer.taxId, the form explains it localized and marks the input; no order is placed.
    await page.getByRole("button", { name: /authorize & place order/i }).click();
    await expect(page.locator('form p[role="alert"]')).toHaveText(/doesn't match the format of the selected type/);
    await expect(business.getByLabel("Tax ID (optional)")).toHaveAttribute("aria-invalid", "true");
    await expect(page).not.toHaveURL(/\/checkout\/confirmation/);
    // The block survives the refusal exactly as typed, ticked box and chosen type included. React resets the form
    // after every action, which once unticked the box in the DOM — and a resubmit then silently dropped the block.
    await expect(page.getByLabel("This is a business purchase")).toBeChecked();
    await expect(business.getByLabel("Company name")).toHaveValue("Acme GmbH");
    await expect(business.getByLabel("Tax ID (optional)")).toHaveValue("DE12345678");
    await expect(business.getByLabel("Tax ID type")).toHaveValue(String(EU_VAT));
    await capture(page, "checkout-business-invalid-tax-id");

    // Fix only the tax ID in place (the reset cleared the other, uncontrolled fields — refill those), add a bill-to.
    await fillGuestCheckout(page, email);
    await page.getByLabel("Billing address").selectOption("new");
    const billing = page.locator("section").filter({ has: page.getByRole("heading", { name: "Billing address" }) });
    await billing.getByLabel("Full name").fill("Acme Accounts");
    await billing.getByLabel("Address", { exact: true }).fill("9 Ledger Road");
    await billing.getByLabel("City").fill("Munich");
    await billing.getByLabel("Postcode").fill("80331");
    await billing.getByLabel(/country/i).selectOption("DE");
    await business.getByLabel("Tax ID (optional)").fill("DE 123 456 789");
    await capture(page, "checkout-business-guest-ready", { docName: "checkout-business-purchase.png" });

    const orderId = await placeAndPay(page);

    // Persisted as entered, normalized: read back as an operator would.
    const order = await adminOrder(request, orderId);
    expect(order.buyer).toEqual({ companyName: "Acme GmbH", taxId: "DE123456789", taxIdScheme: EU_VAT, taxIdJurisdiction: "DE" });
    expect(order.billingAddress).toMatchObject({ name: "Acme Accounts", line1: "9 Ledger Road", city: "Munich", country: "DE" });
  });

  test("a guest without the business block is billed to the shipping address", async ({ page, request }) => {
    test.skip(!(await pinStorefront(page)), "no demo storefront with a published catalog (needs --data full)");
    await addWarehouseProduct(page);
    await fillGuestCheckout(page, `consumer-guest-${Date.now()}@example.com`);

    const orderId = await placeAndPay(page);

    const order = await adminOrder(request, orderId);
    expect(order.buyer).toBeNull();
    expect(order.billingAddress).toMatchObject({ name: "Guest Shopper", line1: "1 Test Street", city: "Berlin", country: "DE" });
  });

  test("a member checks out with and without the business block and sees it on the order", async ({ page }) => {
    await registerAndLogIn(page);
    test.skip(!(await pinStorefront(page)), "no demo storefront with a published catalog (needs --data full)");

    // Without the block first …
    await addWarehouseProduct(page);
    await fillMemberCheckout(page);
    await placeAndPay(page);

    // … then as a business (ABN), billed to the shipping address.
    await page.goto("/");
    await addWarehouseProduct(page);
    await fillMemberCheckout(page);
    const business = await openBusinessBlock(page);
    await business.getByLabel("Company name").fill("Acme Pty Ltd");
    await business.getByLabel("Tax ID (optional)").fill("51 824 753 556");
    await business.getByLabel("Tax ID type").selectOption(String(ABN));
    await placeAndPay(page);

    // The account's order history shows both bill-tos and exactly one business block, normalized.
    await page.goto("/account");
    const toggles = page.getByRole("button", { name: "Order details", exact: true });
    await expect(toggles).toHaveCount(2);
    await toggles.nth(0).click();
    await toggles.nth(1).click();
    await expect(page.getByTestId("order-billing-address")).toHaveCount(2);
    const block = page.getByTestId("order-business-purchase");
    await expect(block).toHaveCount(1);
    await expect(block).toContainText("Acme Pty Ltd");
    await expect(block).toContainText("Australian ABN: 51824753556 (AU)");
    await capture(page, "account-order-business-purchase");
  });
});

function reviewSection(page: Page) {
  return page.locator("section").filter({ has: page.getByRole("heading", { name: "Review items" }) });
}

async function openBusinessBlock(page: Page) {
  await page.getByLabel("This is a business purchase").check();
  const block = page.getByTestId("business-purchase");
  await expect(block).toBeVisible();
  return block;
}

async function addWarehouseProduct(page: Page): Promise<void> {
  await page.locator('a[href*="physical-warehouse"]').first().click();
  await page.getByRole("button", { name: /add to cart/i }).click();
  await expect(page).toHaveURL(/\/cart/);
  await page.getByRole("link", { name: /checkout/i }).click();
  await expect(page).toHaveURL(/\/checkout/);
}

async function fillShipping(page: Page, name?: string): Promise<void> {
  const shipping = page.locator("section").filter({ has: page.getByRole("heading", { name: "Shipping address" }) });
  if (name) await shipping.getByLabel("Full name").fill(name);
  await shipping.getByLabel("Address").fill("1 Test Street");
  await shipping.getByLabel("City").fill("Berlin");
  await shipping.getByLabel("Postcode").fill("10115");
  await shipping.getByLabel(/country/i).selectOption("DE");
  await page.getByRole("button", { name: /get shipping rates/i }).click();
  await expect(page.getByText(/standard/i)).toBeVisible();
}

async function fillGuestCheckout(page: Page, email: string): Promise<void> {
  await page.getByLabel("Email").fill(email);
  await fillShipping(page, "Guest Shopper");
}

async function fillMemberCheckout(page: Page): Promise<void> {
  // A member's name is pre-filled from the profile; the verified account email is used for the order.
  await fillShipping(page);
}

async function placeAndPay(page: Page): Promise<string> {
  await page.getByRole("button", { name: /authorize & place order/i }).click();
  await expect(page).toHaveURL(/\/checkout\/confirmation/);
  const orderId = new URL(page.url()).searchParams.get("order") ?? "";
  expect(orderId).not.toBe("");
  await page.getByRole("button", { name: /complete test payment/i }).click();
  await expect(page.getByRole("heading", { name: /thank you/i })).toBeVisible({ timeout: 25_000 });
  return orderId;
}

type AdminOrder = {
  buyer: { companyName: string; taxId: string | null; taxIdScheme: number | null; taxIdJurisdiction: string | null } | null;
  billingAddress: { name: string; line1: string; city: string; region: string | null; postcode: string; country: string } | null;
};

async function adminOrder(request: APIRequestContext, orderId: string): Promise<AdminOrder> {
  const login = await request.post(`${GATEWAY}/api/identity/login`, { data: ADMIN });
  expect(login.ok(), `admin login: ${login.status()}`).toBeTruthy();
  const r = await request.get(`${GATEWAY}/api/ordering/admin/orders/${orderId}`);
  expect(r.ok(), `admin order ${orderId}: ${r.status()}`).toBeTruthy();
  return (await r.json()) as AdminOrder;
}

async function registerAndLogIn(page: Page): Promise<void> {
  const email = `business-member-${Date.now()}@example.com`;
  const password = "a-strong-password";
  await page.goto("/register");
  await page.getByLabel("Email", { exact: true }).fill(email);
  await page.getByLabel(/password/i).fill(password);
  await page.getByLabel(/first name/i).fill("Ada");
  await page.getByLabel(/last name/i).fill("Buyer");
  await page.getByLabel(/^phone/i).fill("+61400111222");
  await page.getByLabel(/date of birth/i).fill("1990-05-01");
  await page.getByRole("button", { name: /create account/i }).click();
  await expect(page).toHaveURL(/\/login/);
  await page.getByLabel("Email", { exact: true }).fill(email);
  await page.getByLabel("Password", { exact: true }).fill(password);
  await page.getByRole("button", { name: /log in/i }).click();
  await expect(page).toHaveURL(/\/account/);
}
