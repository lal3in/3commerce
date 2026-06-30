import { test, expect, type Locator } from "@playwright/test";

const ADMIN_EMAIL = "admin@3commerce.local";
const ADMIN_PASSWORD = "dev-admin-password-1";
const SUPPLIER_ENTITY_ID = "00000000-0000-0000-0000-000000000001";

async function fillBlazorField(locator: Locator, value: string) {
  await locator.fill(value);
  await locator.evaluate((element) => element.dispatchEvent(new Event("change", { bubbles: true })));
}

test.describe("Supplier portal", () => {
  test("unauthenticated users are redirected to supplier sign in", async ({ page }) => {
    await page.goto("/");
    await expect(page).toHaveURL(/\/login/);
    await expect(page.getByRole("heading", { name: /supplier portal sign in/i })).toBeVisible();
  });

  test("supplier can review readiness and submit stock/change requests", async ({ page }) => {
    await page.goto("/login");
    await page.getByLabel("Email").fill(ADMIN_EMAIL);
    await page.getByLabel("Password").fill(ADMIN_PASSWORD);
    await page.getByRole("button", { name: /sign in/i }).click();

    await expect(page.getByRole("heading", { name: /supplier overview/i })).toBeVisible();
    await expect(page.getByText(/view your supplier readiness/i)).toBeVisible();
    await expect(page.getByRole("button", { name: /check readiness/i })).toBeVisible();

    await page.goto("/stock");
    await expect(page.getByRole("heading", { name: /stock feeds/i })).toBeVisible();
    const stockInputs = page.getByRole("textbox");
    await fillBlazorField(stockInputs.nth(0), SUPPLIER_ENTITY_ID);
    await fillBlazorField(stockInputs.nth(1), "s3://demo-supplier/stock-feed.csv");
    await fillBlazorField(stockInputs.nth(2), "Playwright supplier stock-feed request");
    await page.getByRole("button", { name: /submit stock feed request/i }).click();
    await expect(page.getByText(/stock feed request captured locally/i)).toBeVisible();

    await page.goto("/requests");
    await expect(page.getByRole("heading", { name: /change requests/i })).toBeVisible();
    await fillBlazorField(page.getByRole("textbox", { name: /supplier entity id/i }), SUPPLIER_ENTITY_ID);
    await page.getByLabel(/request type/i).selectOption("BankAccount");
    await fillBlazorField(page.getByRole("textbox", { name: "Details" }), "Rotate payout account after approval.");
    await page.getByRole("button", { name: /^submit request$/i }).click();
    await expect(page.getByText(/change request captured locally/i)).toBeVisible();
  });
});
