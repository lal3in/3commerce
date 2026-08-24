import { type Page, type APIRequestContext, expect } from "@playwright/test";

const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";
const ADMIN_EMAIL = "admin@3commerce.local";
const ADMIN_PASSWORD = "dev-admin-password-1";

/** Logs into the Blazor admin via its real form (handles the antiforgery token). */
export async function loginAsAdmin(page: Page): Promise<void> {
  await page.goto("/login");
  await page.getByLabel("Email").fill(ADMIN_EMAIL);
  await page.getByLabel("Password").fill(ADMIN_PASSWORD);
  await page.getByRole("button", { name: /sign in/i }).click();
  await expect(page.getByRole("heading", { name: /dashboard/i })).toBeVisible();
}

/** Resolve a live demo storefront (au/eu/us) — its id AND currency — so seeded orders belong to a real
 *  storefront (never the synthetic default) and we can pick a product priced in that store's currency.
 *  Returns null when none is published — callers should test.skip in that case. */
export async function demoStorefront(request: APIRequestContext): Promise<{ id: string; currency: string } | null> {
  for (const slug of ["eu", "au", "us"]) {
    const r = await request.get(`${GATEWAY}/api/catalog/storefronts/public?slug=${slug}`);
    if (r.ok()) {
      const s = (await r.json()) as { id: string; currency: string };
      return { id: s.id, currency: s.currency };
    }
  }
  return null;
}

/** Back-compat: just the id (callers that only guard with test.skip on presence). */
export async function demoStorefrontId(request: APIRequestContext): Promise<string | null> {
  return (await demoStorefront(request))?.id ?? null;
}

/**
 * Seeds a confirmed, paid order and an open RMA via the gateway API, returning the orderId.
 * Stands in for the customer journey so the admin UI test can focus on approve → refund.
 * The order is attributed to a demo storefront (every order must belong to one); callers guard with
 * demoStorefrontId(...) + test.skip when no demo store is published (import-only stack).
 */
export async function seedPaidOrder(request: APIRequestContext): Promise<{ orderId: string; gross: number }> {
  const store = await demoStorefront(request);
  expect(store, "a demo storefront must be published to attribute the order (dev-up --data full)").toBeTruthy();
  const { id: storefrontId, currency } = store!;
  // Admin session also satisfies the customer policy (cart/checkout/rma).
  const login = await request.post(`${GATEWAY}/api/identity/login`, {
    data: { email: ADMIN_EMAIL, password: ADMIN_PASSWORD },
  });
  expect(login.ok()).toBeTruthy();

  // Pick a product actually SELLABLE on this storefront: scoped to the store's PUBLISHED catalog in its
  // currency, so it passes the approval-gated availability gate (DECISION A) and has a resolvable price.
  // A bare global products[0] can be an unapproved / unpublished / 0-stock fixture (e.g. the products the
  // approval-gated-availability spec creates, which are newest and Active) — checkout rejects those with a
  // 400, leaving orderId undefined and the whole seed helper throwing.
  const products = await (
    await request.get(`${GATEWAY}/api/catalog/products?storefrontId=${storefrontId}&currency=${currency}&pageSize=1`)
  ).json();
  expect(Array.isArray(products) && products.length > 0, "the demo storefront must publish a sellable product").toBeTruthy();
  const productId = products[0].id as string;

  await request.post(`${GATEWAY}/api/ordering/cart/items`, { data: { productId, quantity: 1 } });
  const checkout = await request.post(`${GATEWAY}/api/ordering/checkout`, {
    data: {
      email: "buyer@example.com",
      storefrontId,
      shippingAddress: { name: "B", line1: "1 St", city: "Berlin", postcode: "10115", country: "DE" },
    },
  });
  const order = await checkout.json();
  const orderId = order.orderId as string;
  const gross = order.grossMinor as number;

  // Let the saga start, then complete the simulated payment.
  await new Promise((r) => setTimeout(r, 3000));
  const intent = `pi_fake_${orderId.replace(/-/g, "")}`;
  await request.post(`${GATEWAY}/api/payments/dev/simulate-payment/${intent}`);

  // Wait for the order to confirm.
  await expect
    .poll(async () => (await (await request.get(`${GATEWAY}/api/ordering/orders/${orderId}/status`)).json()).status, {
      timeout: 30_000,
    })
    .toBe("Confirmed");

  return { orderId, gross };
}

export async function seedPaidOrderWithRma(request: APIRequestContext): Promise<{ orderId: string }> {
  const { orderId, gross } = await seedPaidOrder(request);

  // Customer requests an RMA for the whole order → saga in Requested. NOTE: this claims the FULL order's
  // refundable quantity, so a caller must not then expect to refund the same order again (nothing left).
  //
  // RequestRma 404s until the OrderConfirmed → OrderSnapshot projection lands in Support, which can lag
  // under full-suite load (a prior test's bulk import floods the bus). Firing once and ignoring the status
  // silently dropped the RMA, so the admin-list poll then timed out. Retry the POST until it's accepted.
  await expect
    .poll(
      async () =>
        (
          await request.post(`${GATEWAY}/api/support/rma`, {
            data: { orderId, email: "buyer@example.com", amountMinor: gross, reason: "damaged" },
          })
        ).status(),
      { timeout: 60_000 },
    )
    .toBe(202);
  // Accepted → give the RmaRequested → admin-list projection a moment to surface it.
  await expect
    .poll(async () => {
      const rmas = await (await request.get(`${GATEWAY}/api/support/admin/rmas`)).json();
      return rmas.some((r: { orderId: string }) => r.orderId === orderId);
    }, { timeout: 20_000 })
    .toBeTruthy();

  return { orderId };
}

export async function rmaState(request: APIRequestContext, orderId: string): Promise<string | undefined> {
  const rmas = await (await request.get(`${GATEWAY}/api/support/admin/rmas`)).json();
  return rmas.find((r: { orderId: string }) => r.orderId === orderId)?.state;
}
