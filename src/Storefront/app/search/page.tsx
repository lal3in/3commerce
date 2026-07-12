import Link from "next/link";
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

  const storefront = await resolveStorefront();
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
    ? `Results for “${params.q}”`
    : params.category
      ? `Category: ${params.category}`
      : "All products";

  return (
    <div className="space-y-6">
      <div className="flex items-baseline justify-between">
        <h1 className="text-xl font-semibold">{heading}</h1>
        <p className="text-sm text-neutral-500">{total} items</p>
      </div>

      <nav className="flex flex-wrap gap-2" aria-label="Filter by product type">
        <Link
          href={typeHref(params, undefined)}
          className={`rounded-full px-3 py-1 text-sm ${activeType ? "border border-neutral-300 hover:bg-neutral-50" : "bg-neutral-900 text-white"}`}
        >
          All types
        </Link>
        {PRODUCT_TYPES.map((t) => (
          <Link
            key={t.value}
            href={typeHref(params, t.value)}
            className={`rounded-full px-3 py-1 text-sm ${activeType === t.value ? "bg-neutral-900 text-white" : `border border-neutral-300 ${productTypeClasses(t.value)}`}`}
          >
            {t.label}
          </Link>
        ))}
      </nav>

      <ProductGrid products={hits} />

      {totalPages > 1 && (
        <nav className="flex items-center justify-center gap-2 pt-4" aria-label="Pagination">
          {page > 1 && (
            <Link href={pageHref(params, page - 1)} className="rounded border px-3 py-1 text-sm">
              Previous
            </Link>
          )}
          <span className="text-sm text-neutral-500">
            Page {page} of {totalPages}
          </span>
          {page < totalPages && (
            <Link href={pageHref(params, page + 1)} className="rounded border px-3 py-1 text-sm">
              Next
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
