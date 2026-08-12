import { test, expect } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

// Currency registry admin page (currency_1): lists the managed currencies and adds a new one, which the
// registry accepts (variable decimals) and then displays — the surface an operator uses to add/retire the
// codes the platform prices/sells/settles in.
test("Currencies page lists the seeded set and can add a currency", async ({ page }) => {
  await loginAsAdmin(page);
  await page.goto("/currencies");
  await expect(page.getByRole("heading", { name: /currencies/i })).toBeVisible();
  await page.waitForTimeout(1500); // let the Blazor circuit load the registry

  // The seeded currencies render as rows with their ISO codes.
  const table = page.locator("table").first();
  for (const code of ["AUD", "CAD", "CNY", "EUR", "GBP", "USD"]) {
    await expect(table.getByText(code, { exact: true }).first()).toBeVisible({ timeout: 10_000 });
  }

  // Add a fresh currency (unique per run) with a non-2-decimal value, and see it appear in the list.
  const code = `Z${String.fromCharCode(65 + Math.floor(Math.random() * 26))}${String.fromCharCode(65 + Math.floor(Math.random() * 26))}`;
  await page.getByPlaceholder("JPY").fill(code);
  await page.getByPlaceholder("Japanese Yen").fill("Test Currency");
  await page.getByPlaceholder("¥").fill("₸");
  await page.getByRole("button", { name: "Add", exact: true }).click();

  await expect(page.locator("table").first().getByText(code, { exact: true }).first()).toBeVisible({ timeout: 10_000 });
});
