import Link from "next/link";
import { getTranslations } from "next-intl/server";
import { listCategories, searchProducts } from "@/lib/gateway";
import { resolveStorefront } from "@/lib/storefront-context";
import { ProductGrid } from "@/components/catalog/ProductGrid";

// Server Component, SSR: featured products + categories fetched on the server (components.md §1).
// UI text comes from the request locale's catalog (i18n_1); product CONTENT (titles/descriptions)
// stays in the language the tenant imported it in.
export default async function HomePage() {
  const t = await getTranslations("home");
  // Category names arrive from the catalog as imported (single-language). We localise the KNOWN
  // storefront categories by slug and fall back to the raw name for anything not in the catalog,
  // so a new category never renders blank.
  const tc = await getTranslations("categories");
  const storefront = await resolveStorefront();
  const [featured, categories] = await Promise.all([
    searchProducts({ pageSize: 8, currency: storefront?.currency }),
    listCategories(),
  ]);

  return (
    <div className="space-y-10">
      <section className="rounded-xl bg-neutral-900 text-white px-8 py-12">
        <h1 className="text-3xl font-bold">{t("heroTitle")}</h1>
        <p className="mt-2 text-neutral-300">{t("heroSubtitle")}</p>
        <Link
          href="/search"
          title={t("tips.startShopping")}
          className="mt-4 inline-block rounded-md bg-white text-neutral-900 px-4 py-2 text-sm font-medium"
        >
          {t("startShopping")}
        </Link>
      </section>

      {categories.length > 0 && (
        <section>
          <h2 className="text-lg font-semibold mb-3">{t("categories")}</h2>
          <div className="flex flex-wrap gap-2">
            {categories.map((c) => (
              <Link
                key={c.id}
                href={`/search?category=${c.slug}`}
                title={t("tips.category")}
                className="rounded-full border border-neutral-300 px-3 py-1 text-sm hover:bg-neutral-100"
              >
                {tc.has(c.slug) ? tc(c.slug) : c.name}
              </Link>
            ))}
          </div>
        </section>
      )}

      <section>
        <h2 className="text-lg font-semibold mb-3">{t("featured")}</h2>
        <ProductGrid products={featured.hits} />
      </section>
    </div>
  );
}
