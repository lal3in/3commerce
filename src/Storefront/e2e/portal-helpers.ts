import { request as pwRequest } from "@playwright/test";

/** Shared plumbing for the dev-infra/observability portal specs (CI-safe skip guards +
 *  real-traffic generator). Kept out of the spec files so importing it never re-registers tests. */
export const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";

export async function reachable(url: string): Promise<boolean> {
  const ctx = await pwRequest.newContext();
  try {
    const res = await ctx.get(url, { timeout: 4000 });
    return res.status() < 500;
  } catch {
    return false;
  } finally {
    await ctx.dispose();
  }
}

type DemoStorefront = { id: string; slug: string; currency: string; shipToCountries: string[] };

/** Resolve a live demo storefront (eu/au/us) to attribute a checkout to — every order must belong to a
 *  real storefront — with what the checkout needs: its currency (to pick a product priced + published
 *  there) and its ship-to allowlist (checkout rejects a destination outside it). Returns null when none is
 *  published (import-only stack), so callers skip. */
export async function resolveDemoStorefront(ctx: Awaited<ReturnType<typeof pwRequest.newContext>>): Promise<DemoStorefront | null> {
  for (const slug of ["eu", "au", "us"]) {
    const r = await ctx.get(`${GATEWAY}/api/catalog/storefronts/public?slug=${slug}`);
    if (r.ok()) {
      const s = (await r.json()) as { id: string; currency: string; shipToCountries?: string[] };
      return { id: s.id, slug, currency: s.currency, shipToCountries: s.shipToCountries ?? [] };
    }
  }
  return null;
}

// A plausible address per destination; any other allowlisted country gets the generic one.
const ADDRESSES: Record<string, { city: string; postcode: string }> = {
  AU: { city: "Melbourne", postcode: "3000" },
  DE: { city: "Berlin", postcode: "10115" },
  US: { city: "New York", postcode: "10001" },
};

export type CheckoutDrive = { ok: true; orderId: string; storefront: string } | { ok: false; skip: string };

/** Drive a real guest checkout through the gateway (mirrors e2e-verify L15/L16) so the bus, DBs and
 *  telemetry backends carry fresh traffic from THIS run.
 *
 *  Returns `{ ok: false, skip }` ONLY when the precondition is genuinely absent — no demo storefront is
 *  published (CI's import-only stack; needs `dev-up.sh --data full`). Once a demo store exists, every step
 *  must succeed: a failure THROWS with the step, status and body, instead of quietly turning the caller's
 *  test into a skip (that is how these portal checks skipped on every run while checkout itself worked). */
export async function driveCheckout(): Promise<CheckoutDrive> {
  const ctx = await pwRequest.newContext(); // cookie jar carries the anonymous cart session
  try {
    // Every order must belong to a real storefront (checkout 400s the gateway's synthetic default), so
    // attribute this checkout to a demo store.
    const store = await resolveDemoStorefront(ctx);
    if (!store) {
      return { ok: false, skip: "no demo storefront (eu/au/us) is published — the real checkout needs the `--data full` seed" };
    }

    // A product SELLABLE on this store: scoped to its published catalog in its currency. A bare global
    // products[0] may be unpublished/unapproved here or unpriced in the store's currency (checkout 400s).
    const list = await ctx.get(
      `${GATEWAY}/api/catalog/products?storefrontId=${store.id}&currency=${store.currency}&pageSize=1`,
    );
    if (!list.ok()) throw new Error(`product list for storefront '${store.slug}' → ${list.status()}: ${await list.text()}`);
    const products = (await list.json()) as Array<{ id: string }>;
    if (!products.length) throw new Error(`demo storefront '${store.slug}' publishes no sellable product in ${store.currency}`);

    const add = await ctx.post(`${GATEWAY}/api/ordering/cart/items`, {
      // In the store's currency: checkout refuses a cart in any other currency (ADR-0059).
      data: { productId: products[0].id, quantity: 1, currency: store.currency },
    });
    if (!add.ok()) throw new Error(`add to cart → ${add.status()}: ${await add.text()}`);

    // Ship to a destination the store serves (empty allowlist = worldwide).
    const country = store.shipToCountries[0] ?? "AU";
    const addr = ADDRESSES[country] ?? { city: "Capital", postcode: "1000" };
    const checkout = await ctx.post(`${GATEWAY}/api/ordering/checkout`, {
      data: {
        email: `portal-check-${Date.now()}@example.test`,
        storefrontId: store.id,
        shippingAddress: { name: "Portal Check", line1: "42 Example Street", city: addr.city, postcode: addr.postcode, country },
      },
    });
    if (!checkout.ok()) {
      throw new Error(`checkout on storefront '${store.slug}' (ship to ${country}) → ${checkout.status()}: ${await checkout.text()}`);
    }
    const order = (await checkout.json()) as { orderId: string; clientSecret?: string };
    const intent = order.clientSecret?.replace(/_secret_test$/, "") ?? `pi_fake_${order.orderId.replaceAll("-", "")}`;

    // Complete the simulated payment so the saga runs end to end (the intent is returned by checkout, so the
    // pending payment already exists).
    const paid = await ctx.post(`${GATEWAY}/api/payments/dev/simulate-payment/${intent}`);
    if (!paid.ok()) throw new Error(`simulate-payment ${intent} → ${paid.status()}: ${await paid.text()}`);
    return { ok: true, orderId: order.orderId, storefront: store.slug };
  } finally {
    await ctx.dispose();
  }
}
