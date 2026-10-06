import { test, expect, type Page } from "@playwright/test";
import { capture } from "../e2e-support/screenshots";

// Drives the SupplierPortal "Orders" → "mark delivered" action against the running stack. Uses the
// seeded demo supplier login (bound to the demo supplier entity), which fulfils the seeded orders
// (dev-up --data full: the scenario checkouts plus every demo store's orders, whose products carry the
// demo supplier's costed offer). The supplier sees the orders it fulfils and transitions one
// Confirmed → Delivered.
const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";
const SUPPLIER_EMAIL = process.env.SUPPLIER_EMAIL ?? "supplier@3commerce.local";
const SUPPLIER_PASSWORD = process.env.SUPPLIER_PASSWORD ?? "Supplier-password-123";

type SupplierOrder = { id: string; publicOrderNumber: number; status: string };

async function supplierSignIn(page: Page) {
  await page.goto("/login");
  await page.getByLabel("Email").fill(SUPPLIER_EMAIL);
  await page.getByLabel("Password").fill(SUPPLIER_PASSWORD);
  await page.getByRole("button", { name: /sign in/i }).click();
  await expect(page.getByRole("heading", { name: /supplier overview/i })).toBeVisible();
}

test("the fulfilling supplier sees its orders and marks one delivered", async ({ page, request }) => {
  // Precondition from the API, not the first paint: the Orders page shows "Loading your orders…" until
  // the InteractiveServer circuit has fetched them, so counting buttons right after the heading rendered
  // read 0 and skipped even with confirmed orders seeded. Skip only when the seed genuinely isn't there.
  const login = await request.post(`${GATEWAY}/api/identity/login`, {
    data: { email: SUPPLIER_EMAIL, password: SUPPLIER_PASSWORD },
  });
  test.skip(!login.ok(), "demo supplier login not provisioned (needs --data full seed)");
  const list = await request.get(`${GATEWAY}/api/ordering/orders/supplier/me`);
  expect(list.ok(), `supplier orders → ${list.status()}: ${await list.text()}`).toBeTruthy();
  const target = ((await list.json()) as SupplierOrder[]).find((o) => o.status === "Confirmed");
  test.skip(!target, "no confirmed order to deliver (needs --data full seed)");

  await supplierSignIn(page);
  await page.goto("/orders");
  await expect(page.getByRole("heading", { name: /^orders$/i })).toBeVisible();

  // The target order's row (first cell is "#<public order number>"), once the list has loaded.
  const row = page
    .locator("tbody tr")
    .filter({ has: page.locator("td", { hasText: new RegExp(`^#${target!.publicOrderNumber}$`) }) });
  const markButton = row.getByRole("button", { name: /^mark delivered$/i });
  await expect(markButton).toBeVisible({ timeout: 15_000 });

  // A click that lands before the circuit is live is dropped, so retry until the confirmation shows.
  // Safe to repeat: mark-delivered is idempotent and scoped to this one order's row.
  await expect(async () => {
    if (await markButton.isVisible()) await markButton.click({ timeout: 2_000 });
    await expect(page.getByText(/order marked delivered/i)).toBeVisible({ timeout: 3_000 });
  }).toPass({ timeout: 20_000 });

  await expect(row.getByText(/^delivered$/i)).toBeVisible();
  await capture(page, "supplier-orders-delivered");
});
