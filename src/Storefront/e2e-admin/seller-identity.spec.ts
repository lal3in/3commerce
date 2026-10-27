import { test, expect, type APIRequestContext } from "@playwright/test";
import { loginAsAdmin } from "./helpers";
import { capture } from "../e2e-support/screenshots";

const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";
const TENANT_ID = "00000000-0000-0000-0000-000000000001";
const EST_KEY = "e2e-hq"; // reused across runs: edited when present, so runs don't pile up establishments

type Establishment = { id: string; key: string; label: string; jurisdiction: string };
type Seller = { entityId: string; displayName: string; establishments: Establishment[]; identifiers: Array<{ type: number; value: string }> };

async function apiLogin(request: APIRequestContext): Promise<void> {
  const login = await request.post(`${GATEWAY}/api/identity/login`, {
    data: { email: "admin@3commerce.local", password: "dev-admin-password-1" },
  });
  expect(login.ok()).toBeTruthy();
}

async function currentSeller(request: APIRequestContext): Promise<Seller | null> {
  const res = await request.get(`${GATEWAY}/api/entity/entities/seller?tenantId=${TENANT_ID}`);
  return res.status() === 404 ? null : ((await res.json()) as Seller);
}

// ADR-0063 P2 (tax_p2_seller_identity): the operator designates the tenant's seller of record, gives it a tax ID
// (an invalid one is refused with the per-scheme reason), adds an establishment, then binds a storefront to that
// establishment and a tax jurisdiction in Commerce ops. Every field carries a tooltip (components.md).
test("entities + commerce ops: seller of record, tax ID, establishment and storefront binding", async ({ page, request }) => {
  await apiLogin(request);

  // The seller of record: reuse the tenant's (a tenant has exactly one), else create an entity to designate.
  let seller = await currentSeller(request);
  let sellerName = seller?.displayName;
  if (!seller) {
    sellerName = `E2E Seller ${Date.now()}`;
    const created = await request.post(`${GATEWAY}/api/entity/entities`, {
      data: { tenantId: TENANT_ID, type: 2, legalName: sellerName },
    });
    expect(created.ok()).toBeTruthy();
  }

  const storefrontName = `Seller Binding E2E ${Date.now()}`;
  const sf = await request.post(`${GATEWAY}/api/catalog/admin/storefronts`, {
    data: { tenantId: TENANT_ID, name: storefrontName, visibility: 1, currency: "AUD", taxRegime: 1, taxRateBasisPoints: 1000 },
  });
  expect(sf.ok()).toBeTruthy();
  const storefrontId = ((await sf.json()) as { id: string }).id;

  try {
    await loginAsAdmin(page);
    await page.goto("/entities");
    const row = page.locator("tr", { hasText: sellerName! }).first();
    await expect(row).toBeVisible({ timeout: 15_000 });
    await row.getByRole("button", { name: "Details", exact: true }).click();
    const section = page.locator("#seller-identity");
    await expect(section).toBeVisible({ timeout: 10_000 });

    if (!seller) {
      await expect(page.locator("#seller-designate")).toHaveAttribute("title", /.+/);
      await page.locator("#seller-designate").click();
      // The button disappears only once the designation committed and the details reloaded (the heading already
      // says "Seller of record" before the click, so it is no signal).
      await expect(page.locator("#seller-designate")).toHaveCount(0, { timeout: 10_000 });
      await expect.poll(async () => (await currentSeller(request))?.entityId ?? null, { timeout: 10_000 }).not.toBeNull();
      seller = await currentSeller(request);
    }

    // An invalid EU VAT ID is refused with the per-scheme reason; nothing is stored.
    await page.locator("#ident-type").selectOption("5");
    await expect(page.locator("#ident-jurisdiction")).toHaveAttribute("title", /.+/);
    await page.locator("#ident-value").fill("DE12345");
    await page.locator("#ident-add").click();
    await expect(page.getByText(/does not match the DE format/).first()).toBeVisible({ timeout: 10_000 });

    // A valid one is stored normalized with its jurisdiction (only once — identifiers are permanent).
    if (!seller!.identifiers.some((i) => i.type === 5 && i.value === "DE123456789")) {
      await page.locator("#ident-value").fill("de 123 456 789");
      await page.locator("#ident-add").click();
    }
    await expect(page.getByText("DE123456789").first()).toBeVisible({ timeout: 10_000 });

    // Establishment: add it the first time, edit its label on later runs (the key is immutable).
    const label = `E2E head office ${Date.now()}`;
    const existing = seller!.establishments.find((e) => e.key === EST_KEY);
    if (existing) {
      await section.locator("li", { hasText: EST_KEY }).getByRole("button", { name: "Edit", exact: true }).click();
      await expect(page.locator("#est-key")).toBeDisabled();
    } else {
      await page.locator("#est-key").fill(EST_KEY);
      await page.locator("#est-line1").fill("1 George St");
      await page.locator("#est-city").fill("Sydney");
      await page.locator("#est-postcode").fill("2000");
    }
    for (const id of ["#est-key", "#est-label", "#est-jurisdiction", "#est-save"]) {
      await expect(page.locator(id)).toHaveAttribute("title", /.+/);
    }
    await page.locator("#est-label").fill(label);
    await page.locator("#est-save").click();
    await expect(section.locator("li", { hasText: label })).toBeVisible({ timeout: 10_000 });
    await capture(page, "seller-identity-entity");

    // Commerce ops: bind the storefront to a tax jurisdiction and the establishment.
    await page.goto("/commerce-ops");
    await page.getByRole("button", { name: /load storefronts/i }).click();
    const sfRow = page.locator("tr", { hasText: storefrontName });
    await expect(sfRow).toBeVisible({ timeout: 10_000 });
    await sfRow.getByRole("button", { name: "Manage", exact: true }).click();
    await expect(page.locator("#sf-tax-jurisdiction")).toHaveAttribute("title", /.+/);
    await expect(page.locator("#sf-seller-establishment")).toHaveAttribute("title", /.+/);
    await page.locator("#sf-tax-jurisdiction").fill("au");
    await page.locator("#sf-seller-establishment").selectOption(EST_KEY);
    await page.getByRole("button", { name: "Save storefront settings", exact: true }).click();
    await capture(page, "seller-identity-storefront");

    await expect
      .poll(async () => {
        const list = await request.get(`${GATEWAY}/api/catalog/admin/storefronts?tenantId=${TENANT_ID}`);
        const s = ((await list.json()) as Array<{ id: string; taxJurisdiction: string; sellerEstablishmentKey: string }>).find((x) => x.id === storefrontId);
        return `${s?.taxJurisdiction}|${s?.sellerEstablishmentKey}`;
      }, { timeout: 10_000 })
      .toBe(`AU|${EST_KEY}`);
  } finally {
    await request.post(`${GATEWAY}/api/catalog/admin/storefronts/${storefrontId}/archive`);
  }
});
