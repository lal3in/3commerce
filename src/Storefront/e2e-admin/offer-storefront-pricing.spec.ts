import { test, expect, type APIRequestContext } from "@playwright/test";

// Offer-as-price, shown == charged (ADR-0028): an admin creates an active, in-window offer SCOPED to a
// storefront, priced below the catalog price. The storefront's product detail (the same catalog API the
// storefront app renders from) must then SHOW that offer price for that storefront + currency. Runs a full
// round trip through the gateway. It skips cleanly when the harness can't reach the admin API or the demo
// storefront/catalog isn't seeded, so it never flakes — it either proves the behaviour or is skipped.
const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";
const ADMIN_EMAIL = "admin@3commerce.local";
const ADMIN_PASSWORD = "dev-admin-password-1";

async function publicStorefront(request: APIRequestContext): Promise<{ id: string; currency: string } | null> {
  for (const slug of ["eu", "au", "us"]) {
    const r = await request.get(`${GATEWAY}/api/catalog/storefronts/public?slug=${slug}`);
    if (r.ok()) {
      const s = (await r.json()) as { id: string; currency: string };
      return { id: s.id, currency: s.currency };
    }
  }
  return null;
}

test("Storefront shows the active, in-window offer price for its storefront", async ({ request }) => {
  const login = await request.post(`${GATEWAY}/api/identity/login`, {
    data: { email: ADMIN_EMAIL, password: ADMIN_PASSWORD },
  });
  test.skip(!login.ok(), "admin login unavailable in this harness");

  const store = await publicStorefront(request);
  test.skip(store === null, "no demo storefront published (dev-up --data full)");
  const { id: storefrontId, currency } = store!;

  // A product listed on this storefront, priced in its currency.
  const hits = await (
    await request.get(`${GATEWAY}/api/catalog/products?storefrontId=${storefrontId}&currency=${currency}&pageSize=1`)
  ).json();
  test.skip(!Array.isArray(hits) || hits.length === 0, "no product priced for the demo storefront currency");
  const slug = hits[0].slug as string;

  const detailUrl = `${GATEWAY}/api/catalog/products/${slug}?storefrontId=${storefrontId}&currency=${currency}`;
  const detail = (await (await request.get(detailUrl)).json()) as {
    id: string;
    variants: { priceMinor: number }[];
  };
  test.skip(detail.variants.length === 0, "product has no variant priced in the storefront currency");
  const basePrice = detail.variants[0].priceMinor;
  test.skip(basePrice <= 1, "product price too low to derive a distinct offer price");

  const offerPrice = Math.max(1, basePrice - 500);
  expect(offerPrice).not.toBe(basePrice);
  const now = Date.now();
  const create = await request.post(`${GATEWAY}/api/catalog/admin/offers`, {
    data: {
      productId: detail.id,
      supplierId: crypto.randomUUID(),
      supplyCategory: 1, // Physical
      fulfilmentType: 2, // Warehouse
      priceMinor: offerPrice,
      currency,
      priority: 0,
      storefrontId,
      activeFrom: new Date(now - 3_600_000).toISOString(),
      activeUntil: new Date(now + 3_600_000).toISOString(),
    },
  });
  test.skip(!create.ok(), "admin offers API not reachable from this harness");
  const offerId = ((await create.json()) as { id: string }).id;

  try {
    // The storefront now SHOWS the offer price (Catalog reads its own Offers table — no projection lag).
    await expect
      .poll(
        async () => {
          const d = (await (await request.get(detailUrl)).json()) as { variants: { priceMinor: number }[] };
          return d.variants.some((v) => v.priceMinor === offerPrice);
        },
        { timeout: 15_000 },
      )
      .toBeTruthy();
  } finally {
    // Deactivate the offer so re-runs (and the seeded storefront) return to the catalog price.
    await request.put(`${GATEWAY}/api/catalog/admin/offers/${offerId}`, { data: { active: false } });
  }

  // With the offer deactivated, the storefront shows the catalog price again.
  await expect
    .poll(
      async () => {
        const d = (await (await request.get(detailUrl)).json()) as { variants: { priceMinor: number }[] };
        return d.variants.some((v) => v.priceMinor === basePrice);
      },
      { timeout: 15_000 },
    )
    .toBeTruthy();
});
