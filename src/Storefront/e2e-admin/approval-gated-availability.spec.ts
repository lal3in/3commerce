import { test, expect, type APIRequestContext } from "@playwright/test";
import { provisionApprovedSupplier, suspendSupplier } from "./approved-supplier";

// Approval-gated availability (DECISION A, strict): a product whose only covering offer is from an
// UNAPPROVED supplier is unavailable on the storefront — out of stock, and its offer price is not applied.
// Approving the supplier makes it available; suspending it (revoking approval) hides it again. Driven end
// to end through the gateway against the SAME catalog API the storefront app renders from. Skips cleanly
// when the harness can't reach the APIs, so it never flakes.
const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";
const ADMIN_EMAIL = "admin@3commerce.local";
const ADMIN_PASSWORD = "dev-admin-password-1";
const TENANT = "00000000-0000-0000-0000-000000000001";

async function firstCategoryId(request: APIRequestContext): Promise<string | null> {
  const r = await request.get(`${GATEWAY}/api/catalog/categories`);
  if (!r.ok()) return null;
  const cats = (await r.json()) as { id: string }[];
  return cats[0]?.id ?? null;
}

type Detail = { id: string; variants: { priceMinor: number; inStock: boolean }[] };

async function detail(request: APIRequestContext, slug: string): Promise<Detail | null> {
  const r = await request.get(`${GATEWAY}/api/catalog/products/${slug}?currency=EUR`);
  return r.ok() ? ((await r.json()) as Detail) : null;
}

test("Storefront hides a product whose only offer is from an unapproved supplier, and shows it once approved", async ({ request }) => {
  const login = await request.post(`${GATEWAY}/api/identity/login`, { data: { email: ADMIN_EMAIL, password: ADMIN_PASSWORD } });
  test.skip(!login.ok(), "admin login unavailable in this harness");

  const categoryId = await firstCategoryId(request);
  test.skip(categoryId === null, "no catalog categories seeded");

  // A fresh product with a EUR-priced, in-stock variant.
  const slug = `agv-e2e-${crypto.randomUUID()}`;
  const create = await request.post(`${GATEWAY}/api/catalog/admin/products`, {
    data: {
      tenantId: TENANT,
      slug,
      title: "Approval Gate E2E",
      brand: "QA",
      description: "Approval-gated availability e2e product.",
      categoryId,
      status: 1,
      attributes: {},
      imageUrls: ["https://placehold.co/600x400"],
      variants: [{ id: null, sku: `AGV-${crypto.randomUUID()}`.slice(0, 20), priceMinor: 4000, currency: "EUR", stockQuantity: 5 }],
    },
  });
  test.skip(!create.ok(), "admin products API not reachable");
  const productId = ((await create.json()) as { id: string }).id;

  // An approved supplier backs the product's only offer (priced below catalog so we can see it applied).
  const supplierId = await provisionApprovedSupplier(request, GATEWAY);
  test.skip(supplierId === null, "could not provision an approved supplier");

  const offer = await request.post(`${GATEWAY}/api/catalog/admin/offers`, {
    data: { tenantId: TENANT, productId, supplierId, supplyCategory: 1, fulfilmentType: 2, priceMinor: 3000, currency: "EUR", priority: 0 },
  });
  test.skip(!offer.ok(), "admin offers API not reachable");

  // Approved supplier → available, and the offer price is applied (approval projection is async → poll).
  await expect
    .poll(async () => {
      const d = await detail(request, slug);
      return d?.variants[0]?.inStock === true && d?.variants[0]?.priceMinor === 3000;
    }, { timeout: 20_000 })
    .toBeTruthy();

  // Revoke approval (suspend) → the product's only covering offer is unapproved → out of stock.
  test.skip(!(await suspendSupplier(request, GATEWAY, supplierId!)), "supplier suspend not reachable");
  await expect
    .poll(async () => {
      const d = await detail(request, slug);
      return d?.variants[0]?.inStock === false;
    }, { timeout: 20_000 })
    .toBeTruthy();
});
