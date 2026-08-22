import { test, expect } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

// Offer-as-price (ADR-0028): the Offers & pricing page exposes a per-offer STOREFRONT scope and an ACTIVE
// WINDOW. The table carries a Storefront column and an Active window column, and the edit modal carries a
// Storefront picker (defaulting to "All storefronts") plus Active from / Active until date inputs.
test("Offers: storefront scope + active window in the table and the modal", async ({ page }) => {
  await loginAsAdmin(page);
  await page.goto("/offers");
  await expect(page.getByRole("heading", { name: /offers/i }).first()).toBeVisible();
  await page.waitForTimeout(1500); // let the Blazor circuit load the offers list

  // The table has the new Storefront + Active window columns.
  const table = page.locator("table").first();
  const headers = (await table.locator("thead th").allInnerTexts()).map((h) => h.trim());
  expect(headers).toContain("Storefront");
  expect(headers).toContain("Active window");

  // The New offer modal exposes the storefront picker + the active-window date inputs.
  await page.getByRole("button", { name: /new offer/i }).click();
  const modal = page.locator("section").filter({ has: page.getByRole("heading", { name: "New offer" }) });
  await expect(modal).toBeVisible();

  // The Storefront & active window fieldset: a storefront <select> defaulting to "All storefronts",
  // and two date inputs (Active from / Active until).
  await expect(modal.getByText(/storefront & active window/i)).toBeVisible();
  const storefrontSelect = modal.locator("select").filter({ has: page.getByRole("option", { name: /all storefronts/i }) });
  await expect(storefrontSelect).toBeVisible();
  await expect(storefrontSelect.locator("option").first()).toHaveText(/all storefronts/i);
  await expect(modal.locator('input[type="date"]')).toHaveCount(2);

  await page.getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(page.getByRole("heading", { name: "New offer" })).toHaveCount(0);
});
