// fe_audit_7: cross-portal flow check. Drives a REAL guest checkout in the storefront browser,
// then confirms the paid order surfaces in the admin API (orders) and the audit trail.
import pw from "/Users/lehn/Documents/Git-Roots/3commerce/src/Storefront/node_modules/playwright/index.js";
const { chromium } = pw;
const STORE = "http://localhost:3000", GW = "http://localhost:8080";

async function adminToken() {
  const r = await fetch(`${GW}/api/identity/login`, { method: "POST", headers: { "content-type": "application/json" },
    body: JSON.stringify({ email: "admin@3commerce.local", password: "dev-admin-password-1" }) });
  const j = await r.json(); return j.token || j.accessToken || j.access_token;
}
async function adminOrders(tok) {
  const r = await fetch(`${GW}/api/ordering/admin/orders`, { headers: { authorization: `Bearer ${tok}` } });
  return r.ok ? await r.json() : [];
}

const tok = await adminToken();
const before = await adminOrders(tok);
console.log("admin orders BEFORE:", before.length);

const browser = await chromium.launch();
const page = await browser.newPage();
const email = `flow-${Date.now()}@example.com`;
const steps = [];
try {
  await page.goto(`${STORE}/search?q=lamp`, { waitUntil: "networkidle" });
  await page.locator('a[href^="/products/"]').first().click();
  await page.getByRole("button", { name: /add to cart/i }).click();
  await page.waitForURL(/\/cart/); steps.push("added to cart");
  await page.getByRole("link", { name: /checkout/i }).click();
  await page.waitForURL(/\/checkout/);
  await page.getByLabel("Email").fill(email);
  const shipping = page.locator("form, section").filter({ hasText: /shipping/i }).first();
  await page.getByLabel("Full name").first().fill("Flow Shopper");
  await page.getByLabel("Address").first().fill("1 Flow Street");
  await page.getByLabel("City").first().fill("Berlin");
  await page.getByLabel("Postcode").first().fill("10115");
  await page.getByLabel(/country/i).first().fill("DE");
  await page.getByRole("button", { name: /get shipping rates/i }).click();
  await page.getByText(/standard/i).first().waitFor({ timeout: 10000 });
  await page.getByRole("button", { name: /authorize & place order/i }).click();
  await page.waitForURL(/confirmation/, { timeout: 15000 }); steps.push("order placed");
  await page.getByRole("button", { name: /complete test payment/i }).click();
  await page.getByRole("heading", { name: /thank you/i }).waitFor({ timeout: 25000 });
  steps.push("payment completed (thank you)");
} catch (e) {
  steps.push("FLOW ERROR: " + String(e).slice(0, 160));
}
await browser.close();
console.log("browser steps:", steps.join(" → "));

// Give the checkout saga a moment to create the Order + emit audit.
await new Promise((r) => setTimeout(r, 4000));
const after = await adminOrders(tok);
console.log("admin orders AFTER:", after.length, "(delta", after.length - before.length + ")");
const mine = after.find((o) => (o.email || "").toLowerCase() === email.toLowerCase());
console.log("my order in admin:", mine ? `${mine.id} status=${mine.status} total=${mine.grossMinor}` : "NOT FOUND");
