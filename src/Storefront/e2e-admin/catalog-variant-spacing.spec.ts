import { test, expect } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

// FIX: in the Admin Catalog variants table the SKU input used to run full-bleed (width:100%) with no
// cell padding, so the SKU column visually touched the Price (minor) column. The header/body cells now
// carry horizontal padding and the SKU input is capped, so there is a clear gap between the columns.
const OUT = process.env.SHOT_DIR ?? "/tmp";

test("Catalog variants table: SKU and Price columns are visibly separated", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 });
  await loginAsAdmin(page);
  await page.goto("/catalog");
  await page.getByRole("button", { name: /new product/i }).click();

  const fieldset = page
    .locator("fieldset", { has: page.locator("legend", { hasText: "Variants" }) })
    .first();
  await expect(fieldset).toBeVisible();
  await fieldset.scrollIntoViewIfNeeded();

  // The variant row exists with the SKU + Price inputs to type into.
  const skuInput = fieldset.locator('input[title*="Stock-keeping"]').first();
  const priceInput = fieldset.locator('input[title*="Base price in minor units"]').first();
  await expect(skuInput).toBeVisible();
  await expect(priceInput).toBeVisible();

  // Header cells carry horizontal padding (they had the browser default ~1px before).
  const skuTh = fieldset.locator("thead th").first();
  const thPadLeft = await skuTh.evaluate((el) => parseFloat(getComputedStyle(el).paddingLeft));
  const thPadRight = await skuTh.evaluate((el) => parseFloat(getComputedStyle(el).paddingRight));
  expect(thPadLeft).toBeGreaterThan(2);
  expect(thPadRight).toBeGreaterThan(2);

  // Body SKU cell carries horizontal padding too.
  const skuTd = skuInput.locator("xpath=ancestor::td[1]");
  const tdPadRight = await skuTd.evaluate((el) => parseFloat(getComputedStyle(el).paddingRight));
  expect(tdPadRight).toBeGreaterThan(2);

  // The SKU input is no longer full-bleed: there is a real horizontal gap between the right edge of the
  // SKU input and the left edge of the Price input.
  const skuBox = await skuInput.boundingBox();
  const priceBox = await priceInput.boundingBox();
  expect(skuBox).not.toBeNull();
  expect(priceBox).not.toBeNull();
  const gap = priceBox!.x - (skuBox!.x + skuBox!.width);
  expect(gap).toBeGreaterThan(8);

  await fieldset.screenshot({ path: `${OUT}/after-variants.png` });
  await page.screenshot({ path: `${OUT}/after-fullform.png`, fullPage: true });
});
