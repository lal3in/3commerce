import Link from "next/link";
import { searchProducts } from "@/lib/gateway";
import { resolveStorefront } from "@/lib/storefront-context";
import { ProductGrid } from "@/components/catalog/ProductGrid";

// URL is the state for search/filters/pagination (components.md §4): shareable + crawlable.
export default async function SearchPage({
  searchParams,
}: {
  searchParams: Promise<{ q?: string; category?: string; attrs?: string; page?: string }>;
}) {
  const params = await searchParams;
  const page = Math.max(1, Number(params.page ?? "1") || 1);
  const pageSize = 24;

  const storefront = await resolveStorefront();
  const { hits, total } = await searchProducts({
    q: params.q,
    category: params.category,
    attrs: params.attrs,
    currency: storefront?.currency,
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
  params: { q?: string; category?: string; attrs?: string },
  page: number,
): string {
  const query = new URLSearchParams();
  if (params.q) query.set("q", params.q);
  if (params.category) query.set("category", params.category);
  if (params.attrs) query.set("attrs", params.attrs);
  query.set("page", String(page));
  return `/search?${query.toString()}`;
}
