import Link from "next/link";
import { notFound } from "next/navigation";
import { ProductGrid } from "@/components/catalog/ProductGrid";
import { listCategories, searchProducts, getStorefrontConfig, type StorefrontTaxRegime } from "@/lib/gateway";

const RESERVED_ROUTES = new Set(["account", "api", "cart", "checkout", "login", "orders", "products", "register", "search"]);

const TAX_LABEL: Record<StorefrontTaxRegime, string> = {
  None: "no tax",
  AuGst: "AU GST",
  EuVat: "EU VAT",
  UsSalesTax: "US sales tax",
  Other: "custom tax",
};

function taxSummary(regime: StorefrontTaxRegime, basisPoints: number): string {
  if (regime === "None") return TAX_LABEL.None;
  const pct = (basisPoints / 100).toFixed(basisPoints % 100 === 0 ? 0 : 2);
  return `${TAX_LABEL[regime]} (${pct}%)`;
}

export default async function LocalStorefrontPage({ params }: { params: Promise<{ storefront: string }> }) {
  const { storefront } = await params;
  const slug = storefront.toLowerCase();
  if (RESERVED_ROUTES.has(slug) || slug.includes(".")) {
    notFound();
  }

  const config = await getStorefrontConfig({ slug });
  const [featured, categories] = await Promise.all([
    searchProducts({ pageSize: 8, currency: config?.currency }),
    listCategories(),
  ]);

  const name = config?.name ?? storefront.replace(/-/g, " ").replace(/\b\w/g, (letter) => letter.toUpperCase());
  const currency = config?.currency ?? "configured in Admin";
  const tax = config ? taxSummary(config.taxRegime, config.taxRateBasisPoints) : "configured in Admin";

  return (
    <div className="space-y-10">
      <section className="rounded-xl bg-neutral-900 text-white px-8 py-12">
        <p className="text-sm uppercase tracking-wide text-neutral-400">Local storefront</p>
        <h1 className="mt-2 text-3xl font-bold">{name}</h1>
        <p className="mt-2 text-neutral-300">Currency: {currency} · Tax: {tax}</p>
        <Link
          href="/search"
          className="mt-4 inline-block rounded-md bg-white text-neutral-900 px-4 py-2 text-sm font-medium"
        >
          Browse this storefront catalog
        </Link>
      </section>

      {categories.length > 0 && (
        <section>
          <h2 className="text-lg font-semibold mb-3">Categories</h2>
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
        <h2 className="text-lg font-semibold mb-3">Featured</h2>
        <ProductGrid products={featured.hits} />
      </section>
    </div>
  );
}
