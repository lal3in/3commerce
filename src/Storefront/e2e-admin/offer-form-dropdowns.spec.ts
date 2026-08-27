import { test, expect, type APIRequestContext } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

// The New-offer modal picks Product / Variant / Supplier from dynamically-populated dropdowns of live
// data instead of free-text GUID inputs. Variant is a dependent dropdown (the selected product's SKUs,
// plus an explicit "(all variants)" option), and all three are read-only when editing an existing offer.

const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";
const TENANT = "00000000-0000-0000-0000-000000000001";
const ADMIN_EMAIL = "admin@3commerce.local";
const ADMIN_PASSWORD = "dev-admin-password-1";

type ProductRow = { id: string; title: string; variantCount: number };
type SupplierRow = { id: string; legalName: string; tradingName: string | null; onboardingState: number | null };

async function apiLogin(request: APIRequestContext): Promise<void> {
  const login = await request.post(`${GATEWAY}/api/identity/login`, {
    data: { email: ADMIN_EMAIL, password: ADMIN_PASSWORD },
  });
  expect(login.ok(), "admin API login should succeed").toBeTruthy();
}

test.describe("Offers: New-offer form dropdowns", () => {
  test("Product/Variant/Supplier are populated dropdowns and an offer can be created through them", async ({
    page,
    request,
  }) => {
    await apiLogin(request);

    // Real data feeding the dropdowns.
    const products = (await (
      await request.get(`${GATEWAY}/api/catalog/admin/products?pageSize=200`)
    ).json()) as ProductRow[];
    const entities = (await (await request.get(`${GATEWAY}/api/entity/entities?tenantId=${TENANT}`)).json()) as SupplierRow[];
    const suppliers = entities.filter((e) => e.onboardingState !== null);
    test.skip(products.length === 0, "no catalog products seeded");
    test.skip(suppliers.length === 0, "no suppliers seeded");

    const supplier = suppliers[0];
    const supplierName = supplier.tradingName?.trim() ? supplier.tradingName : supplier.legalName;

    // Pick a product WITH variants that this supplier does not already have an offer for — so the
    // create passes the (tenant, product, variant, supplier, storefront) uniqueness rule.
    const existing = (await (
      await request.get(`${GATEWAY}/api/catalog/admin/offers?tenantId=${TENANT}&supplierId=${supplier.id}`)
    ).json()) as { productId: string }[];
    const used = new Set(existing.map((o) => o.productId));
    const product = products.find((p) => p.variantCount > 0 && !used.has(p.id)) ?? products.find((p) => !used.has(p.id));
    test.skip(!product, "no product available without an existing offer for this supplier");
    const expectedSkus = (await (
      await request.get(`${GATEWAY}/api/catalog/admin/products/${product!.id}`)
    ).json()) as { variants: { id: string; sku: string }[] };

    // Drive the UI.
    await loginAsAdmin(page);
    await page.goto("/offers");
    await page.waitForTimeout(2000); // let the Blazor circuit load the offers list + dropdown data

    await page.getByRole("button", { name: /new offer/i }).click();
    const modal = page.locator("section").filter({ has: page.getByRole("heading", { name: "New offer" }) });
    await expect(modal.getByRole("heading", { name: "New offer" })).toBeVisible();

    // The three fields are <select> dropdowns (not free-text GUID inputs). Identify each by its options.
    const productSelect = modal.locator("select").filter({ hasText: "Choose a product" });
    const variantSelect = modal.locator("select").filter({ hasText: "(all variants)" });
    const supplierSelect = modal.locator("select").filter({ hasText: "Choose a supplier" });
    await expect(productSelect).toBeVisible();
    await expect(variantSelect).toBeVisible();
    await expect(supplierSelect).toBeVisible();

    // Product dropdown lists real products.
    await expect
      .poll(async () => productSelect.locator("option").count())
      .toBeGreaterThan(1);
    await expect(supplierSelect.locator("option")).toContainText([supplierName]);

    // Selecting a product populates the dependent Variant dropdown with that product's SKUs.
    await productSelect.selectOption(product!.id);
    if (product!.variantCount > 0) {
      const firstSku = expectedSkus.variants[0].sku;
      await expect.poll(async () => variantSelect.locator("option").allInnerTexts()).toContain(firstSku);
    }
    // "(all variants)" is always present (a product-level offer has VariantId = null).
    await expect(variantSelect.locator("option")).toContainText(["(all variants)"]);

    // Complete and save the offer entirely through the dropdowns.
    await supplierSelect.selectOption({ label: supplierName });
    // Price minor is the first number input in the modal grid.
    await modal.locator('input[type="number"]').first().fill("2500");
    await modal.getByRole("button", { name: "Save offer" }).click();

    // Confirmation banner (LoadAsync sets it AFTER reloading the list).
    await expect(page.getByText("Offer saved.")).toBeVisible({ timeout: 15_000 });
  });

  test("Editing an existing offer shows Product/Variant/Supplier read-only (no GUID inputs, no selects)", async ({
    page,
  }) => {
    await loginAsAdmin(page);
    await page.goto("/offers");
    await page.waitForTimeout(2000);

    const rows = page.locator("table tbody tr");
    test.skip((await rows.count()) === 0, "no offers to edit");

    await rows.first().getByRole("button", { name: "Edit", exact: true }).click();
    const modal = page.locator("section").filter({ has: page.getByRole("heading", { name: "Edit offer" }) });
    await expect(modal.getByRole("heading", { name: "Edit offer" })).toBeVisible();

    // In edit mode the three fields are DISABLED inputs showing resolved names — there is no product
    // <select> and no editable GUID text box for these immutable fields.
    await expect(modal.locator("select").filter({ hasText: "Choose a product" })).toHaveCount(0);
    await expect(modal.locator("select").filter({ hasText: "Choose a supplier" })).toHaveCount(0);

    for (const label of ["Product", "Variant", "Supplier"]) {
      const field = modal.getByLabel(label, { exact: true });
      await expect(field).toBeDisabled();
      const value = await field.inputValue();
      expect(value.length, `${label} shows a resolved value`).toBeGreaterThan(0);
    }
  });
});
