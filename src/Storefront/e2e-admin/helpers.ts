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

/**
 * Seeds a confirmed, paid order and an open RMA via the gateway API, returning the orderId.
 * Stands in for the customer journey so the admin UI test can focus on approve → refund.
 */
export async function seedPaidOrderWithRma(request: APIRequestContext): Promise<{ orderId: string }> {
  // Admin session also satisfies the customer policy (cart/checkout/rma).
  const login = await request.post(`${GATEWAY}/api/identity/login`, {
    data: { email: ADMIN_EMAIL, password: ADMIN_PASSWORD },
  });
  expect(login.ok()).toBeTruthy();

  // Pick a product the Ordering projection knows about.
  const products = await (await request.get(`${GATEWAY}/api/catalog/products?pageSize=1`)).json();
  const productId = products[0].id as string;

  await request.post(`${GATEWAY}/api/ordering/cart/items`, { data: { productId, quantity: 1 } });
  const checkout = await request.post(`${GATEWAY}/api/ordering/checkout`, {
    data: {
      email: "buyer@example.com",
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

  // Customer requests an RMA → saga in Requested.
  await request.post(`${GATEWAY}/api/support/rma`, {
    data: { orderId, email: "buyer@example.com", amountMinor: gross, reason: "damaged" },
  });
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
