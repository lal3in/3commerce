import { cookies } from "next/headers";

// The browser and server components talk ONLY to the YARP gateway (ADR-0011).
const GATEWAY_URL = process.env.GATEWAY_URL ?? "http://localhost:8080";

export type ProductHit = {
  id: string;
  slug: string;
  title: string;
  brand: string;
  minPriceMinor: number;
  currency: string;
  imageUrl: string | null;
};

export type Variant = {
  id: string;
  sku: string;
  priceMinor: number;
  currency: string;
  inStock: boolean;
};

export type ProductDetail = {
  id: string;
  slug: string;
  title: string;
  brand: string;
  description: string;
  categorySlug: string | null;
  categoryName: string | null;
  attributes: Record<string, string>;
  imageUrls: string[];
  variants: Variant[];
};

export type Category = { id: string; slug: string; name: string };

export type SearchResult = { hits: ProductHit[]; total: number };

/** Server-side fetch through the gateway, forwarding the session cookie (components.md §2). */
async function gatewayFetch(path: string, init?: RequestInit): Promise<Response> {
  const cookieStore = await cookies();
  const session = cookieStore.get("3c_session");
  const headers = new Headers(init?.headers);
  if (session) {
    headers.set("cookie", `3c_session=${session.value}`);
  }
  return fetch(`${GATEWAY_URL}${path}`, { ...init, headers });
}

export async function searchProducts(params: {
  q?: string;
  category?: string;
  attrs?: string;
  page?: number;
  pageSize?: number;
}): Promise<SearchResult> {
  const query = new URLSearchParams();
  if (params.q) query.set("q", params.q);
  if (params.category) query.set("category", params.category);
  if (params.attrs) query.set("attrs", params.attrs);
  query.set("page", String(params.page ?? 1));
  query.set("pageSize", String(params.pageSize ?? 24));

  const response = await gatewayFetch(`/api/catalog/products?${query.toString()}`, {
    cache: "no-store",
  });
  if (!response.ok) {
    return { hits: [], total: 0 };
  }
  const hits = (await response.json()) as ProductHit[];
  const total = Number(response.headers.get("X-Total-Count") ?? hits.length);
  return { hits, total };
}

export async function getProduct(slug: string): Promise<ProductDetail | null> {
  // Product pages are cacheable/ISR-friendly; revalidate periodically.
  const response = await gatewayFetch(`/api/catalog/products/${encodeURIComponent(slug)}`, {
    next: { revalidate: 300 },
  });
  return response.ok ? ((await response.json()) as ProductDetail) : null;
}

export async function listCategories(): Promise<Category[]> {
  const response = await gatewayFetch(`/api/catalog/categories`, { next: { revalidate: 600 } });
  return response.ok ? ((await response.json()) as Category[]) : [];
}

export async function getProfile(): Promise<{ email: string; emailVerified: boolean } | null> {
  const response = await gatewayFetch(`/api/identity/me`, { cache: "no-store" });
  return response.ok ? ((await response.json()) as { email: string; emailVerified: boolean }) : null;
}

export { GATEWAY_URL };
