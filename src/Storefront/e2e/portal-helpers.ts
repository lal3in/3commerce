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

/** Drive a real guest checkout through the gateway (mirrors scripts/dev-dummy-data.sh) so the bus,
 *  DBs and telemetry backends carry fresh traffic from THIS run. Returns false when the stack/seed
 *  is absent. */
export async function driveCheckout(): Promise<boolean> {
  const ctx = await pwRequest.newContext(); // cookie jar carries the anonymous cart session
  try {
    const search = await ctx.get(`${GATEWAY}/api/catalog/products?pageSize=1`);
    if (!search.ok()) return false;
    const hits = (await search.json()) as Array<{ slug: string }>;
    if (!hits.length) return false;
    const detail = await ctx.get(`${GATEWAY}/api/catalog/products/${hits[0].slug}`);
    if (!detail.ok()) return false;
    const product = (await detail.json()) as { id: string; variants: Array<{ id: string }> };
    if (!product.variants?.length) return false;

    const add = await ctx.post(`${GATEWAY}/api/ordering/cart/items`, {
      data: { productId: product.id, variantId: product.variants[0].id, quantity: 1 },
    });
    if (!add.ok()) return false;

    const checkout = await ctx.post(`${GATEWAY}/api/ordering/checkout`, {
      data: {
        email: `portal-check-${Date.now()}@example.test`,
        shippingAddress: { name: "Portal Check", line1: "42 Example Street", city: "Melbourne", postcode: "3000", country: "AU" },
        selectedShippingService: "Fake Ground",
        selectedShippingAmountMinor: 499,
        selectedShippingExpiresAt: "2999-01-01T00:00:00Z",
      },
    });
    if (!checkout.ok()) return false;
    const order = (await checkout.json()) as { orderId: string; clientSecret?: string };
    const intent = order.clientSecret?.replace(/_secret_test$/, "") ?? `pi_fake_${order.orderId.replaceAll("-", "")}`;
    await ctx.post(`${GATEWAY}/api/payments/dev/simulate-payment/${intent}`); // best-effort
    return true;
  } catch {
    return false;
  } finally {
    await ctx.dispose();
  }
}
