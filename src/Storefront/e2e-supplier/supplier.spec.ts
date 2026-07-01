import { test, expect, type Locator } from "@playwright/test";

const ADMIN_EMAIL = "admin@3commerce.local";
const ADMIN_PASSWORD = "dev-admin-password-1";
const SUPPLIER_ENTITY_ID = "00000000-0000-0000-0000-000000000001";

async function setBlazorInput(locator: Locator, value: string) {
  await locator.fill(value);
  // Notify Blazor's @bind so the InteractiveServer model updates (no-op until the circuit is live).
  await locator.dispatchEvent("change");
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

    // Stock feed and change-request forms are @rendermode InteractiveServer EditForms:
    // @bind-Value and OnValidSubmit only take effect once the Blazor Server circuit is
    // connected. Drive fill + submit + confirmation as one retrying unit so an interaction
    // that lands before the circuit is live self-corrects instead of flaking the run.
    await page.goto("/stock");
    await expect(page.getByRole("heading", { name: /stock feeds/i })).toBeVisible();
    await expect(async () => {
      const stockInputs = page.getByRole("textbox");
      await setBlazorInput(stockInputs.nth(0), SUPPLIER_ENTITY_ID);
      await setBlazorInput(stockInputs.nth(1), "s3://demo-supplier/stock-feed.csv");
      await setBlazorInput(stockInputs.nth(2), "Playwright supplier stock-feed request");
      await page.getByRole("button", { name: /submit stock feed request/i }).click();
      await expect(page.getByText(/stock feed request captured locally/i)).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 20_000 });

    await page.goto("/requests");
    await expect(page.getByRole("heading", { name: /change requests/i })).toBeVisible();
    await expect(async () => {
      await setBlazorInput(page.getByRole("textbox", { name: /supplier entity id/i }), SUPPLIER_ENTITY_ID);
      await page.getByLabel(/request type/i).selectOption("BankAccount");
      await setBlazorInput(page.getByRole("textbox", { name: "Details" }), "Rotate payout account after approval.");
      await page.getByRole("button", { name: /^submit request$/i }).click();
      await expect(page.getByText(/change request captured locally/i)).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 20_000 });
  });
});
