import type { Locator, Page } from "@playwright/test";
import { test, expect, pinStorefront } from "./fixtures";
import { capture } from "../e2e-support/screenshots";

/**
 * mem_2 — a billing address per saved card, in a real browser. A member saves two cards on /account and gives the
 * Visa its own billing address (the Mastercard has none). At checkout the Visa (the default card) fills the billing
 * section from its address; the shopper edits it for this order and ticks "Save to my account for next time": the
 * order is billed to the edited address, the address book gains it, and the card keeps its own. Choosing the
 * Mastercard falls back to billing to the shipping address. The amount is never touched — the summary is the same
 * whichever card or address is chosen. The checkout half needs a demo storefront (dev-up --data full).
 */
test.describe("Saved card billing address (mem_2)", () => {
  test("each card keeps its own billing address; checkout fills billing from the chosen card", async ({ page }) => {
    test.setTimeout(180_000);
    await registerAndLogIn(page);

    await addCard(page, "4242424242424242", "12/29");
    await addCard(page, "5555555555554444", "11/30");
    const visa = cardRow(page, /VISA ending 4242/);
    const mastercard = cardRow(page, /MASTERCARD ending 4444/);
    await expect(visa).toContainText("No billing address on this card yet.");

    // Give the Visa its billing address.
    await visa.getByText("Add billing address").click();
    await visa.getByLabel("Full name").fill("Ada Buyer");
    await visa.getByLabel("Address", { exact: true }).fill("9 Ledger Road");
    await visa.getByLabel("City").fill("Munich");
    await visa.getByLabel("Postcode").fill("80331");
    await visa.getByLabel(/country/i).selectOption("DE");
    await visa.getByRole("button", { name: "Save billing address" }).click();
    await expect(page.getByText("Card billing address saved.")).toBeVisible();
    await expect(cardRow(page, /VISA ending 4242/).getByTestId("card-billing-address")).toContainText("9 Ledger Road, Munich, 80331, DE");
    await expect(mastercard).toContainText("No billing address on this card yet.");
    await capture(page, "account-card-billing-address", { docName: "account-card-billing-address.png" });

    test.skip(!(await pinStorefront(page)), "no demo storefront with a published catalog (needs --data full)");
    await addWarehouseProduct(page);
    await fillShipping(page);
    const summary = page.locator("section").filter({ has: page.getByRole("heading", { name: "Review items" }) });
    const before = await summary.innerText();

    // The default card (the Visa) fills billing from its own address.
    const billing = page.locator("section").filter({ has: page.getByRole("heading", { name: "Billing address" }) });
    await expect(page.getByLabel("Saved card")).toHaveValue(await selectedCardValue(page, /VISA/));
    await expect(billing.getByTestId("card-billing-note")).toBeVisible();
    await expect(billing.getByLabel("Full name")).toHaveValue("Ada Buyer");
    await expect(billing.getByLabel("City")).toHaveValue("Munich");
    await capture(page, "checkout-billing-from-saved-card", { docName: "checkout-billing-from-card.png" });

    // The Mastercard has none → billed to the shipping address; back to the Visa → its address again.
    await page.getByLabel("Saved card").selectOption({ label: await optionLabel(page, /MASTERCARD/) });
    await expect(billing.getByText(/Using the shipping address for billing/)).toBeVisible();
    await page.getByLabel("Saved card").selectOption({ label: await optionLabel(page, /VISA/) });
    await expect(billing.getByLabel("City")).toHaveValue("Munich");
    expect(await summary.innerText()).toBe(before);

    // Edit for this order and save it to the account.
    await billing.getByLabel("Address", { exact: true }).fill("11 Ledger Road");
    await billing.getByLabel("Save to my account for next time").check();
    await placeAndPay(page);

    await page.goto("/account");
    await page.getByRole("button", { name: "Order details", exact: true }).first().click();
    await expect(page.getByTestId("order-billing-address")).toContainText("11 Ledger Road");
    // The address book gained it; the card keeps its own.
    await expect(page.getByText(/11 Ledger Road/).first()).toBeVisible();
    await expect(cardRow(page, /VISA ending 4242/).getByTestId("card-billing-address")).toContainText("9 Ledger Road");

    // Removing the card's address.
    const visaAgain = cardRow(page, /VISA ending 4242/);
    await visaAgain.getByText("Edit billing address").click();
    await visaAgain.getByRole("button", { name: "Remove billing address" }).click();
    await expect(page.getByText("Card billing address removed.")).toBeVisible();
    await expect(cardRow(page, /VISA ending 4242/)).toContainText("No billing address on this card yet.");
  });
});

function cardRow(page: Page, name: RegExp): Locator {
  return page.getByTestId("saved-card").filter({ hasText: name });
}

async function optionLabel(page: Page, brand: RegExp): Promise<string> {
  const labels = await page.getByLabel("Saved card").locator("option").allInnerTexts();
  const label = labels.find((text) => brand.test(text));
  expect(label, `no saved-card option matching ${brand}`).toBeTruthy();
  return label!;
}

async function selectedCardValue(page: Page, brand: RegExp): Promise<string> {
  const options = page.getByLabel("Saved card").locator("option");
  const count = await options.count();
  for (let i = 0; i < count; i++) {
    const option = options.nth(i);
    if (brand.test(await option.innerText())) return (await option.getAttribute("value")) ?? "";
  }
  throw new Error(`no saved-card option matching ${brand}`);
}

async function addCard(page: Page, number: string, expiry: string): Promise<void> {
  const form = page.locator("form").filter({ has: page.getByRole("heading", { name: "Add a saved card" }) });
  await form.getByLabel("Card number").fill(number);
  await form.getByLabel("Expiry").fill(expiry);
  await form.getByLabel("CVV").fill("123");
  // "Make default card" starts ticked; leave it off so the FIRST card saved (the Visa) stays the default.
  await form.getByLabel("Make default card").uncheck();
  await form.getByRole("button", { name: "Save card" }).click();
  await expect(page.getByText("Card saved.")).toBeVisible();
}

async function addWarehouseProduct(page: Page): Promise<void> {
  await page.locator('a[href*="physical-warehouse"]').first().click();
  await page.getByRole("button", { name: /add to cart/i }).click();
  await expect(page).toHaveURL(/\/cart/);
  await page.getByRole("link", { name: /checkout/i }).click();
  await expect(page).toHaveURL(/\/checkout/);
}

async function fillShipping(page: Page): Promise<void> {
  const shipping = page.locator("section").filter({ has: page.getByRole("heading", { name: "Shipping address" }) });
  await shipping.getByLabel("Address").fill("1 Test Street");
  await shipping.getByLabel("City").fill("Berlin");
  await shipping.getByLabel("Postcode").fill("10115");
  await shipping.getByLabel(/country/i).selectOption("DE");
  await page.getByRole("button", { name: /get shipping rates/i }).click();
  await expect(page.getByText(/standard/i)).toBeVisible();
}

async function placeAndPay(page: Page): Promise<void> {
  await page.getByRole("button", { name: /authorize & place order/i }).click();
  await expect(page).toHaveURL(/\/checkout\/confirmation/);
  await page.getByRole("button", { name: /complete test payment/i }).click();
  await expect(page.getByRole("heading", { name: /thank you/i })).toBeVisible({ timeout: 25_000 });
}

async function registerAndLogIn(page: Page): Promise<void> {
  const email = `card-billing-${Date.now()}-${Math.floor(Math.random() * 1e6)}@example.com`;
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
