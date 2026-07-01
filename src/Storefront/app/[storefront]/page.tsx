import Link from "next/link";
import { notFound } from "next/navigation";
import { ProductGrid } from "@/components/catalog/ProductGrid";
import { listCategories, searchProducts } from "@/lib/gateway";

const RESERVED_ROUTES = new Set(["account", "api", "cart", "checkout", "login", "orders", "products", "register", "search"]);
const LOCAL_STOREFRONT_LABELS: Record<string, { name: string; region: string; currency: string; tax: string }> = {
  au: { name: "Demo AU Store", region: "Australia", currency: "AUD", tax: "AU GST" },
  eu: { name: "Demo EU Store", region: "European Union", currency: "EUR", tax: "EU VAT" },
  us: { name: "Demo US Store", region: "United States", currency: "USD", tax: "US sales tax" },
};

export default async function LocalStorefrontPage({ params }: { params: Promise<{ storefront: string }> }) {
  const { storefront } = await params;
  const slug = storefront.toLowerCase();
  if (RESERVED_ROUTES.has(slug) || slug.includes(".")) {
    notFound();
  }

  const config = LOCAL_STOREFRONT_LABELS[slug] ?? {
    name: storefront.replace(/-/g, " ").replace(/\b\w/g, (letter) => letter.toUpperCase()),
    region: "custom local audience",
    currency: "configured in Admin",
    tax: "configured in Admin",
  };
  const [featured, categories] = await Promise.all([
    searchProducts({ pageSize: 8 }),
    listCategories(),
  ]);

  return (
    <div className="space-y-10">
      <section className="rounded-xl bg-neutral-900 text-white px-8 py-12">
        <p className="text-sm uppercase tracking-wide text-neutral-400">Local storefront demo</p>
        <h1 className="mt-2 text-3xl font-bold">{config.name}</h1>
        <p className="mt-2 text-neutral-300">
          Target region: {config.region} · Currency: {config.currency} · Tax: {config.tax}
        </p>
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
