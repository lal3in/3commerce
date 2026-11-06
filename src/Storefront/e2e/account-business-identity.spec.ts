import type { Page } from "@playwright/test";
import { test, expect, pinStorefront } from "./fixtures";
import { capture } from "../e2e-support/screenshots";

/**
 * ADR-0063 P3b — the account's default business identity, in a real browser. A member saves the company they usually
 * buy for on /account: a malformed tax ID is refused by Identity (400 problem+json naming `taxId`) with the same
 * localized message checkout uses, keeping what was typed; the fixed one is stored normalized. Checkout's Business
 * purchase block then opens prefilled from it; the shopper changes it for one order, and that ORDER keeps the change
 * while the profile keeps the saved identity. Clearing it leaves the next checkout's block unticked.
 * The checkout half needs a demo storefront with a published catalog (dev-up --data full); it skips otherwise.
 */
const EU_VAT = "5"; // TaxIdScheme.EuVat — enums cross HTTP as numbers

test.describe("Account default business identity (ADR-0063 P3b)", () => {
  test("a member saves, is refused on a malformed tax ID, fixes it and clears it", async ({ page }) => {
    await registerAndLogIn(page);
    const section = page.getByTestId("business-identity");
    await expect(section).toContainText("No business details saved.");

    await section.getByLabel("Company name").fill("Acme GmbH");
    await section.getByLabel("Tax ID (optional)").fill("DE12345678"); // DE needs 9 digits
    await section.getByLabel("Tax ID type").selectOption(EU_VAT);
    await section.getByRole("button", { name: "Save business details" }).click();

    await expect(page.getByTestId("business-identity-error")).toHaveText(/doesn't match the format of the selected type/);
    await expect(section.getByLabel("Tax ID (optional)")).toHaveAttribute("aria-invalid", "true");
    await expect(section.getByLabel("Company name")).toHaveValue("Acme GmbH");
    await expect(section.getByLabel("Tax ID (optional)")).toHaveValue("DE12345678");
    await expect(section.getByLabel("Tax ID type")).toHaveValue(EU_VAT);
    await capture(page, "account-business-identity-invalid");

    await section.getByLabel("Tax ID (optional)").fill("de 123 456 789");
    await section.getByRole("button", { name: "Save business details" }).click();
    const saved = page.getByTestId("business-identity-saved");
    await expect(saved).toContainText("Acme GmbH");
    await expect(saved).toContainText("EU VAT number: DE123456789");
    await expect(saved).toContainText("Jurisdiction: DE");
    await expect(section.getByRole("status")).toHaveText("Business details saved.");
    await capture(page, "account-business-identity-saved", { docName: "account-business-identity.png" });

    // Survives a reload — it is stored by Identity, not held in the page.
    await page.reload();
    await expect(page.getByTestId("business-identity-saved")).toContainText("DE123456789");

    await page.getByTestId("business-identity-saved").getByRole("button", { name: "Clear business details" }).click();
    await expect(section).toContainText("No business details saved.");
    await expect(page.getByTestId("business-identity-saved")).toHaveCount(0);
  });

  test("checkout prefills the business block from the profile; the order keeps its own change", async ({ page }) => {
    // Two checkout rounds plus payment polling: can pass the 60 s default on a cold dev server.
    test.setTimeout(180_000);
    await registerAndLogIn(page);
    const section = page.getByTestId("business-identity");
    await section.getByLabel("Company name").fill("Acme GmbH");
    await section.getByLabel("Tax ID (optional)").fill("DE123456789");
    await section.getByLabel("Tax ID type").selectOption(EU_VAT);
    await section.getByRole("button", { name: "Save business details" }).click();
    await expect(page.getByTestId("business-identity-saved")).toContainText("DE123456789");
    test.skip(!(await pinStorefront(page)), "no demo storefront with a published catalog (needs --data full)");

    // Prefilled and ticked, with the "from your account" note.
    await addWarehouseProduct(page);
    await fillShipping(page);
    await expect(page.getByLabel("This is a business purchase")).toBeChecked();
    const business = page.getByTestId("business-purchase");
    await expect(business.getByTestId("business-purchase-prefilled")).toBeVisible();
    await expect(business.getByLabel("Company name")).toHaveValue("Acme GmbH");
    await expect(business.getByLabel("Tax ID (optional)")).toHaveValue("DE123456789");
    await expect(business.getByLabel("Tax ID type")).toHaveValue(EU_VAT);
    await capture(page, "checkout-business-prefilled-from-account", { docName: "checkout-business-prefilled.png" });

    // Changed for THIS order only.
    await business.getByLabel("Company name").fill("Acme GmbH Berlin Branch");
    await placeAndPay(page);

    await page.goto("/account");
    await page.getByRole("button", { name: "Order details", exact: true }).first().click();
    await expect(page.getByTestId("order-business-purchase")).toContainText("Acme GmbH Berlin Branch");
    await expect(page.getByTestId("order-business-purchase")).toContainText("DE123456789");
    // … while the profile still holds the saved identity.
    const saved = page.getByTestId("business-identity-saved");
    await expect(saved).toContainText("Acme GmbH");
    await expect(saved).not.toContainText("Berlin Branch");

    // Cleared → the next checkout's block starts unticked.
    await saved.getByRole("button", { name: "Clear business details" }).click();
    await expect(page.getByTestId("business-identity")).toContainText("No business details saved.");
    await page.goto("/");
    await addWarehouseProduct(page);
    await expect(page.getByLabel("This is a business purchase")).not.toBeChecked();
    await expect(page.getByTestId("business-purchase")).toHaveCount(0);
  });
});

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
  const email = `business-profile-${Date.now()}-${Math.floor(Math.random() * 1e6)}@example.com`;
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
