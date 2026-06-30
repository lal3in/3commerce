import { test, expect } from "./fixtures";
import { loadFixtureManifest, requireScenario, type ScenarioCode } from "./fixture-manifest";

const visibleScenarios: ScenarioCode[] = [
  "physical-warehouse-flat",
  "physical-dropship-flat",
  "physical-multi-variant-tiered",
  "digital-download-onetime",
  "subscription-monthly-flat",
  "usage-api-meter",
  "manual-service-onetime",
];

test.describe("Catalog scenario products", () => {
  const { manifest, manifestPath } = loadFixtureManifest();

  test.skip(!manifest, `Fixture manifest not found at ${manifestPath}; run scripts/dev-dummy-data.sh --profile full first.`);

  for (const code of visibleScenarios) {
    test(`${code} product detail renders from fixture manifest`, async ({ page }) => {
      const product = requireScenario(manifest, code);
      if (!product) {
        test.skip(true, `Scenario ${code} missing from ${manifestPath}`);
        return;
      }

      await page.goto(`/products/${product.slug}`);
      await expect(page.getByRole("heading", { level: 1 })).toContainText(new RegExp(code, "i"));
      await expect(page.getByText(/3commerce QA/i)).toBeVisible();
      await expect(page.getByRole("button", { name: /add to cart/i })).toBeEnabled();
    });
  }

  test("private or unpublished fixture is not discoverable in public search", async ({ page }) => {
    const product = requireScenario(manifest, "inactive-unpublished-private");
    if (!product) {
      test.skip(true, `Scenario inactive-unpublished-private missing from ${manifestPath}`);
      return;
    }

    await page.goto(`/search?q=${encodeURIComponent(product.slug ?? "inactive-unpublished-private")}`);
    await expect(page.getByRole("heading", { name: /results/i })).toBeVisible();
    await expect(page.getByRole("link", { name: /inactive-unpublished-private/i })).toHaveCount(0);
  });
});
