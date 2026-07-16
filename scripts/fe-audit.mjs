// One-shot frontend audit (fe_audit_1/2): crawls routes, records broken images,
// failed requests, console errors, and a control inventory. Emits JSON to stdout.
//   node scripts/fe-audit.mjs           # storefront only (no auth)
//   node scripts/fe-audit.mjs --admin   # + admin (logs in)
// Run from repo root with the storefront's node_modules on the path.
import pw from "/Users/lehn/Documents/Git-Roots/3commerce/src/Storefront/node_modules/playwright/index.js";
const { chromium } = pw;

const STORE = "http://localhost:3000";
const ADMIN = "http://localhost:5200";

const storefrontRoutes = ["/", "/search?q=speaker", "/cart", "/checkout", "/login", "/register", "/account", "/support"];

async function auditPage(page, url) {
  const failed = [];
  const consoleErrors = [];
  const onResp = (r) => { if (r.status() >= 400) failed.push({ url: r.url().slice(0, 120), status: r.status() }); };
  const onConsole = (m) => { if (m.type() === "error") consoleErrors.push(m.text().slice(0, 200)); };
  page.on("response", onResp);
  page.on("console", onConsole);
  let nav = "ok";
  try { await page.goto(url, { waitUntil: "networkidle", timeout: 20000 }); }
  catch (e) { nav = `NAV_ERROR: ${String(e).slice(0, 120)}`; }
  await page.waitForTimeout(500);
  const imgs = await page.evaluate(() =>
    Array.from(document.images).map((i) => ({ src: i.currentSrc || i.src, ok: i.complete && i.naturalWidth > 0 })));
  const broken = imgs.filter((i) => !i.ok).map((i) => i.src.slice(0, 120));
  const controls = await page.evaluate(() => ({
    buttons: document.querySelectorAll("button").length,
    links: document.querySelectorAll("a[href]").length,
    inputs: document.querySelectorAll("input,select,textarea").length,
  }));
  page.off("response", onResp);
  page.off("console", onConsole);
  return { url, nav, images: imgs.length, brokenImages: broken, failedRequests: failed, consoleErrors, controls };
}

const browser = await chromium.launch();
const ctx = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
const page = await ctx.newPage();
const report = { storefront: [] };
for (const r of storefrontRoutes) report.storefront.push(await auditPage(page, STORE + r));

if (process.argv.includes("--admin")) {
  report.admin = [];
  const ap = await ctx.newPage();
  await ap.goto(ADMIN + "/login", { waitUntil: "networkidle" });
  await ap.fill('input[name="email"]', "admin@3commerce.local");
  await ap.fill('input[name="password"]', "dev-admin-password-1");
  await ap.click('button[type="submit"]');
  await ap.waitForTimeout(1500);
  const adminRoutes = ["/", "/catalog", "/orders", "/rmas", "/entities", "/payment-accounts", "/supplier-payouts", "/mission-control", "/xero-mappings"];
  for (const r of adminRoutes) report.admin.push(await auditPage(ap, ADMIN + r));
}

console.log(JSON.stringify(report, null, 2));
await browser.close();
