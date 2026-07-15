import Link from "next/link";
import { getTranslations } from "next-intl/server";
import { searchProducts } from "@/lib/gateway";
import { resolveStorefront } from "@/lib/storefront-context";
import { ProductGrid } from "@/components/catalog/ProductGrid";
import { PRODUCT_TYPES, productTypeClasses } from "@/lib/product-type";

// URL is the state for search/filters/pagination (components.md §4): shareable + crawlable.
export default async function SearchPage({
  searchParams,
}: {
  searchParams: Promise<{ q?: string; category?: string; attrs?: string; type?: string; page?: string }>;
}) {
  const params = await searchParams;
  const page = Math.max(1, Number(params.page ?? "1") || 1);
  const pageSize = 24;
  const activeType = Number(params.type) || undefined;

  const [t, tt, storefront] = await Promise.all([
    getTranslations("search"),
    getTranslations("productTypes"),
    resolveStorefront(),
  ]);
  const { hits, total } = await searchProducts({
    q: params.q,
    category: params.category,
    attrs: params.attrs,
    currency: storefront?.currency,
    type: activeType,
    page,
    pageSize,
  });

  const totalPages = Math.max(1, Math.ceil(total / pageSize));
  const heading = params.q
    ? t("resultsFor", { query: params.q })
    : params.category
      ? t("category", { category: params.category })
      : t("allProducts");

  return (
    <div className="space-y-6">
      <div className="flex items-baseline justify-between">
        <h1 className="text-xl font-semibold">{heading}</h1>
        <p className="text-sm text-neutral-500">{t("itemCount", { count: total })}</p>
      </div>

      <nav className="flex flex-wrap gap-2" aria-label={t("filterByType")}>
        <Link
          href={typeHref(params, undefined)}
          title={t("tips.allTypes")}
          className={`rounded-full px-3 py-1 text-sm ${activeType ? "border border-neutral-300 hover:bg-neutral-50" : "bg-neutral-900 text-white"}`}
        >
          {t("allTypes")}
        </Link>
        {PRODUCT_TYPES.map((type) => (
          <Link
            key={type.value}
            href={typeHref(params, type.value)}
            title={t("tips.type", { type: tt(type.labelKey) })}
            className={`rounded-full px-3 py-1 text-sm ${activeType === type.value ? "bg-neutral-900 text-white" : `border border-neutral-300 ${productTypeClasses(type.value)}`}`}
          >
            {tt(type.labelKey)}
          </Link>
        ))}
      </nav>

      <ProductGrid products={hits} />

      {totalPages > 1 && (
        <nav className="flex items-center justify-center gap-2 pt-4" aria-label={t("pagination")}>
          {page > 1 && (
            <Link href={pageHref(params, page - 1)} title={t("tips.previous")} className="rounded border px-3 py-1 text-sm">
              {t("previous")}
            </Link>
          )}
          <span className="text-sm text-neutral-500">{t("pageOf", { page, total: totalPages })}</span>
          {page < totalPages && (
            <Link href={pageHref(params, page + 1)} title={t("tips.next")} className="rounded border px-3 py-1 text-sm">
              {t("next")}
            </Link>
          )}
        </nav>
      )}
    </div>
  );
}

function pageHref(
  params: { q?: string; category?: string; attrs?: string; type?: string },
  page: number,
): string {
  const query = new URLSearchParams();
  if (params.q) query.set("q", params.q);
  if (params.category) query.set("category", params.category);
  if (params.attrs) query.set("attrs", params.attrs);
  if (params.type) query.set("type", params.type);
  query.set("page", String(page));
  return `/search?${query.toString()}`;
}

// Type chip href: set/clear the type filter, reset to page 1, preserve q/category/attrs.
function typeHref(
  params: { q?: string; category?: string; attrs?: string },
  type: number | undefined,
): string {
  const query = new URLSearchParams();
  if (params.q) query.set("q", params.q);
  if (params.category) query.set("category", params.category);
  if (params.attrs) query.set("attrs", params.attrs);
  if (type) query.set("type", String(type));
  return `/search?${query.toString()}`;
}
