import { test, expect, type Page } from "@playwright/test";

// Drives the SupplierPortal "Orders" → "mark delivered" action against the running stack. Uses the
// seeded demo supplier login (bound to the demo supplier entity), which fulfils the seeded warehouse
// orders (dev-up --data full runs a physical-warehouse checkout scenario). The supplier sees the orders
// it fulfils and transitions one Confirmed → Delivered.
const SUPPLIER_EMAIL = process.env.SUPPLIER_EMAIL ?? "supplier@3commerce.local";
const SUPPLIER_PASSWORD = process.env.SUPPLIER_PASSWORD ?? "Supplier-password-123";
const SHOT_DIR = process.env.SHOT_DIR ?? "test-results";

async function supplierSignIn(page: Page) {
  await page.goto("/login");
  await page.getByLabel("Email").fill(SUPPLIER_EMAIL);
  await page.getByLabel("Password").fill(SUPPLIER_PASSWORD);
  await page.getByRole("button", { name: /sign in/i }).click();
  await expect(page.getByRole("heading", { name: /supplier overview/i })).toBeVisible();
}

test("the fulfilling supplier sees its orders and marks one delivered", async ({ page }) => {
  await supplierSignIn(page);
  await page.goto("/orders");
  await expect(page.getByRole("heading", { name: /^orders$/i })).toBeVisible();

  // The demo supplier fulfils the seeded warehouse order(s). Find a Confirmed order's "Mark delivered"
  // button and click it; the row transitions to Delivered.
  const markButtons = page.getByRole("button", { name: /^mark delivered$/i });
  test.skip((await markButtons.count()) === 0, "no confirmed order to deliver (needs --data full seed)");

  const firstRow = page.locator("tbody tr").filter({ has: page.getByRole("button", { name: /^mark delivered$/i }) }).first();
  await firstRow.getByRole("button", { name: /^mark delivered$/i }).click();

  await expect(page.getByText(/order marked delivered/i)).toBeVisible({ timeout: 10_000 });
  await expect(page.getByText(/^delivered$/i).first()).toBeVisible();
  await page.screenshot({ path: `${SHOT_DIR}/supplier-orders-delivered.png`, fullPage: true });
});
