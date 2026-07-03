import Link from "next/link";
import { listCategories, searchProducts } from "@/lib/gateway";
import { resolveStorefront } from "@/lib/storefront-context";
import { ProductGrid } from "@/components/catalog/ProductGrid";

// Server Component, SSR: featured products + categories fetched on the server (components.md §1).
export default async function HomePage() {
  const storefront = await resolveStorefront();
  const [featured, categories] = await Promise.all([
    searchProducts({ pageSize: 8, currency: storefront?.currency }),
    listCategories(),
  ]);

  return (
    <div className="space-y-10">
      <section className="rounded-xl bg-neutral-900 text-white px-8 py-12">
        <h1 className="text-3xl font-bold">Everything, sourced for you.</h1>
        <p className="mt-2 text-neutral-300">Browse thousands of products across every category.</p>
        <Link
          href="/search"
          className="mt-4 inline-block rounded-md bg-white text-neutral-900 px-4 py-2 text-sm font-medium"
        >
          Start shopping
        </Link>
      </section>

      {categories.length > 0 && (
        <section>
          <h2 className="text-lg font-semibold mb-3">Categories</h2>
          <div className="flex flex-wrap gap-2">
            {categories.map((c) => (
              <Link
                key={c.id}
                href={`/search?category=${c.slug}`}
                className="rounded-full border border-neutral-300 px-3 py-1 text-sm hover:bg-neutral-100"
              >
                {c.name}
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
