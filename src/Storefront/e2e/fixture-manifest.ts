import fs from "node:fs";
import path from "node:path";

export type ScenarioCode =
  | "physical-warehouse-flat"
  | "physical-dropship-flat"
  | "physical-multi-variant-tiered"
  | "bundle-mixed-physical"
  | "digital-download-onetime"
  | "subscription-monthly-flat"
  | "subscription-yearly-tiered"
  | "usage-api-meter"
  | "manual-service-onetime"
  | "out-of-stock-hold"
  | "inactive-unpublished-private";

type FixtureProduct = {
  code?: string;
  name?: string;
  id?: string;
  slug?: string;
  variantId?: string;
};

type FixtureManifest = {
  scenarioCodes?: ScenarioCode[];
  products?: Partial<Record<ScenarioCode, FixtureProduct>>;
};

const manifestPath = path.resolve(process.cwd(), "../../.run/dev-dummy-data/fixtures.json");

export function loadFixtureManifest() {
  if (!fs.existsSync(manifestPath)) {
    return { manifest: null, manifestPath };
  }

  const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8")) as FixtureManifest;
  return { manifest, manifestPath };
}

export function requireScenario(manifest: FixtureManifest | null, code: ScenarioCode) {
  const product = manifest?.products?.[code];
  if (!product?.slug) {
    return null;
  }

  return product;
}
