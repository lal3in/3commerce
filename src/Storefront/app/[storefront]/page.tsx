import Link from "next/link";
import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { ProductGrid } from "@/components/catalog/ProductGrid";
import { listCategories, searchProducts, getStorefrontConfig, type StorefrontTaxRegime } from "@/lib/gateway";

const RESERVED_ROUTES = new Set(["account", "api", "cart", "checkout", "login", "orders", "products", "register", "search"]);

// Maps the tax regime to a catalog key under `storefrontLanding.tax.*` (i18n_1).
const TAX_KEY: Record<StorefrontTaxRegime, string> = {
  None: "none",
  AuGst: "auGst",
  EuVat: "euVat",
  UsSalesTax: "usSalesTax",
  Other: "other",
};

export default async function LocalStorefrontPage({ params }: { params: Promise<{ storefront: string }> }) {
  const { storefront } = await params;
  const slug = storefront.toLowerCase();
  if (RESERVED_ROUTES.has(slug) || slug.includes(".")) {
    notFound();
  }

  const [t, th] = await Promise.all([getTranslations("storefrontLanding"), getTranslations("home")]);
  const config = await getStorefrontConfig({ slug });
  const [featured, categories] = await Promise.all([
    searchProducts({ pageSize: 8, currency: config?.currency }),
    listCategories(),
  ]);

  const taxSummary = (regime: StorefrontTaxRegime, basisPoints: number): string => {
    const label = t(`tax.${TAX_KEY[regime]}`);
    if (regime === "None") return label;
    const percent = (basisPoints / 100).toFixed(basisPoints % 100 === 0 ? 0 : 2);
    return t("tax.withRate", { label, percent });
  };

  const name = config?.name ?? storefront.replace(/-/g, " ").replace(/\b\w/g, (letter) => letter.toUpperCase());
  const currency = config?.currency ?? t("configuredInAdmin");
  const tax = config ? taxSummary(config.taxRegime, config.taxRateBasisPoints) : t("configuredInAdmin");

  return (
    <div className="space-y-10">
      <section className="rounded-xl bg-neutral-900 text-white px-8 py-12">
        <p className="text-sm uppercase tracking-wide text-neutral-400">{t("eyebrow")}</p>
        <h1 className="mt-2 text-3xl font-bold">{name}</h1>
        <p className="mt-2 text-neutral-300">{t("summary", { currency, tax })}</p>
        <Link
          href="/search"
          title={t("tips.browse")}
          className="mt-4 inline-block rounded-md bg-white text-neutral-900 px-4 py-2 text-sm font-medium"
        >
          {t("browse")}
        </Link>
      </section>

      {categories.length > 0 && (
        <section>
          <h2 className="text-lg font-semibold mb-3">{th("categories")}</h2>
          <div className="flex flex-wrap gap-2">
            {categories.map((category) => (
              <Link
                key={category.id}
                href={`/search?category=${category.slug}`}
                className="rounded-full border border-neutral-300 px-3 py-1 text-sm hover:bg-neutral-100"
              >
                {category.name}
              </Link>
            ))}
          </div>
        </section>
      )}

      <section>
        <h2 className="text-lg font-semibold mb-3">{th("featured")}</h2>
        <ProductGrid products={featured.hits} />
      </section>
    </div>
  );
}
