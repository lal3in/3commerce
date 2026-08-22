import { test, expect } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

// The Offers & pricing page shows a real product Title column (between Product and Variant), and the
// edit form is a modal dialog opened by "＋ New offer" (create) or a row's Edit button (update) — not a
// permanently-visible section at the bottom of the page.
test("Offers: Title column + create/edit open a modal dialog", async ({ page }) => {
  await loginAsAdmin(page);
  await page.goto("/offers");
  await expect(page.getByRole("heading", { name: /offers/i }).first()).toBeVisible();
  await page.waitForTimeout(1500); // let the Blazor circuit load the offers list

  const table = page.locator("table").first();
  const headers = (await table.locator("thead th").allInnerTexts()).map((h) => h.trim());
  expect(headers).toContain("Title");
  // Title sits immediately after Product and before Variant.
  expect(headers.indexOf("Product")).toBeLessThan(headers.indexOf("Title"));
  expect(headers.indexOf("Title")).toBeLessThan(headers.indexOf("Variant"));

  // At least one row shows a non-empty product title (the 2nd column).
  const firstTitle = (await table.locator("tbody tr").first().locator("td").nth(1).innerText()).trim();
  expect(firstTitle.length).toBeGreaterThan(0);

  // The edit form is NOT rendered until the modal is opened.
  await expect(page.getByRole("heading", { name: "New offer" })).toHaveCount(0);
  await expect(page.getByRole("heading", { name: "Edit offer" })).toHaveCount(0);

  // "＋ New offer" opens the create modal.
  await page.getByRole("button", { name: /new offer/i }).click();
  await expect(page.getByRole("heading", { name: "New offer" })).toBeVisible();
  // Cancel closes it.
  await page.getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(page.getByRole("heading", { name: "New offer" })).toHaveCount(0);

  // A row's Edit button opens the edit modal, which shows the product title.
  await table.locator("tbody tr").first().getByRole("button", { name: "Edit", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Edit offer" })).toBeVisible();
  await expect(page.getByText(firstTitle, { exact: false }).first()).toBeVisible();
  // Cancel dismisses the modal.
  await page.getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Edit offer" })).toHaveCount(0);
});
