import { test, expect } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";

// The Offers & pricing product filter searches by product ID OR title. Typing free text (a title
// substring) used to 400 at minimal-API model binding (the param was typed Guid?); it now resolves
// matching product ids and filters the offer list — no error banner. A full product GUID still filters
// to that one product, and clearing the box shows every offer again.
test("Offers: product filter searches by title or id without 400ing", async ({ page }) => {
  await loginAsAdmin(page);
  await page.goto("/offers");
  await expect(page.getByRole("heading", { name: /offers/i }).first()).toBeVisible();
  await page.waitForTimeout(1500); // let the Blazor circuit load the offers list

  const table = page.locator("table").first();
  const errorBanner = page.getByText(/does not indicate success|Bad Request|400/i);
  const productFilter = page.locator('input[title*="product" i]').first();

  // The field is relabelled to make clear it accepts an id OR a title.
  await expect(page.getByText("Product (ID or title)")).toBeVisible();

  const totalRows = await table.locator("tbody tr").count();
  expect(totalRows).toBeGreaterThan(0);

  // 1) Title substring: the seed's products are titled "E2E Scenario ...". Typing "e2e" must return
  //    matching offer rows with NO 400 banner (the bug: this used to fail model binding).
  await productFilter.fill("e2e");
  await page.getByRole("button", { name: /^load$/i }).click();
  await page.waitForTimeout(1500);
  await expect(errorBanner).toHaveCount(0);
  const titleRows = await table.locator("tbody tr").count();
  expect(titleRows).toBeGreaterThan(0);
  // Every returned offer's product title contains the term (2nd column is the product Title).
  const titles = await table.locator("tbody tr td:nth-child(2)").allInnerTexts();
  for (const t of titles) expect(t.toLowerCase()).toContain("e2e");

  // A nonsense term returns zero rows (proves the filter constrains, and still no 400).
  await productFilter.fill("zzz-no-such-product-xyz");
  await page.getByRole("button", { name: /^load$/i }).click();
  await page.waitForTimeout(1500);
  await expect(errorBanner).toHaveCount(0);
  await expect(table.locator("tbody tr")).toHaveCount(0);

  // 2) A full product GUID still filters to that one product. Derive a real product id that has offers:
  //    take the first "e2e" offer row's product short-id (8 chars) and resolve the full GUID from the API.
  await productFilter.fill("e2e");
  await page.getByRole("button", { name: /^load$/i }).click();
  await page.waitForTimeout(1500);
  const shortId = (await table.locator("tbody tr").first().locator("td").first().innerText()).trim();
  const products = (await (await page.request.get(
    `${GATEWAY}/api/catalog/products?pageSize=200`)).json()) as Array<{ id: string }>;
  const fullId = products.find((p) => p.id.startsWith(shortId))?.id;
  expect(fullId, "resolve a full product GUID for the first offer row").toBeTruthy();

  await productFilter.fill(fullId!);
  await page.getByRole("button", { name: /^load$/i }).click();
  await page.waitForTimeout(1500);
  await expect(errorBanner).toHaveCount(0);
  const guidRows = await table.locator("tbody tr").count();
  expect(guidRows).toBeGreaterThan(0);
  // Every row belongs to that product (first column shows the product's 8-char short id).
  const shortIds = await table.locator("tbody tr td:nth-child(1)").allInnerTexts();
  for (const s of shortIds) expect(s.trim()).toBe(shortId);

  await page.screenshot({ path: `${process.env.SHOT_DIR ?? "."}/after-offers-guid-filter.png`, fullPage: true });

  // 3) Clearing the filter shows every offer again (back to the full list).
  await productFilter.fill("");
  await page.getByRole("button", { name: /^load$/i }).click();
  await page.waitForTimeout(1500);
  await expect(errorBanner).toHaveCount(0);
  expect(await table.locator("tbody tr").count()).toBe(totalRows);
});
