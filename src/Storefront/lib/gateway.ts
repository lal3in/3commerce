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
  productType: number;
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
  productType: number;
};

export type Category = { id: string; slug: string; name: string };

export type SearchResult = { hits: ProductHit[]; total: number };

/** Server-side fetch through the gateway, forwarding the session cookie (components.md §2). */
async function gatewayFetch(path: string, init?: RequestInit): Promise<Response> {
  const cookieStore = await cookies();
  const headers = new Headers(init?.headers);
  const parts: string[] = [];
  const session = cookieStore.get("3c_session");
  const cart = cookieStore.get("3c_cart");
  if (session) parts.push(`3c_session=${session.value}`);
  if (cart) parts.push(`3c_cart=${cart.value}`);
  if (parts.length) headers.set("cookie", parts.join("; "));
  return fetch(`${GATEWAY_URL}${path}`, { ...init, headers });
}

export async function searchProducts(params: {
  q?: string;
  category?: string;
  attrs?: string;
  page?: number;
  pageSize?: number;
  // When set, prices come back in this currency and products with no tenant-set price in it are hidden.
  currency?: string;
  // Numeric ProductType filter (browse-by-type).
  type?: number;
}): Promise<SearchResult> {
  const query = new URLSearchParams();
  if (params.q) query.set("q", params.q);
  if (params.category) query.set("category", params.category);
  if (params.attrs) query.set("attrs", params.attrs);
  if (params.currency) query.set("currency", params.currency);
  if (params.type) query.set("type", String(params.type));
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

export async function getProduct(slug: string, currency?: string): Promise<ProductDetail | null> {
  // Product pages are cacheable/ISR-friendly; revalidate periodically. Currency-specific when set.
  const query = currency ? `?currency=${encodeURIComponent(currency)}` : "";
  const response = await gatewayFetch(`/api/catalog/products/${encodeURIComponent(slug)}${query}`, {
    next: { revalidate: 300 },
  });
  return response.ok ? ((await response.json()) as ProductDetail) : null;
}

export async function listCategories(): Promise<Category[]> {
  const response = await gatewayFetch(`/api/catalog/categories`, { next: { revalidate: 600 } });
  return response.ok ? ((await response.json()) as Category[]) : [];
}

export type StorefrontTaxRegime = "None" | "AuGst" | "EuVat" | "UsSalesTax" | "Other";

export type StorefrontConfig = {
  // Non-secret identifiers forwarded at checkout for order attribution (X-3C-* headers).
  id: string;
  tenantId: string;
  name: string;
  publicUrl: string;
  currency: string;
  taxRegime: StorefrontTaxRegime;
  taxRateBasisPoints: number;
};

// Enum ordinals from Catalog StorefrontTaxRegime (System.Text.Json serializes enums as numbers).
const STOREFRONT_TAX_REGIME: Record<number, StorefrontTaxRegime> = {
  0: "None",
  1: "AuGst",
  2: "EuVat",
  3: "UsSalesTax",
  99: "Other",
};

// Resolve the active storefront's shopper-facing config (currency + tax) by canonical host
// (production) or PublicUrl path slug (local /{slug} demo). Returns null when no live storefront matches.
export async function getStorefrontConfig(params: { slug?: string; host?: string; currency?: string }): Promise<StorefrontConfig | null> {
  const query = new URLSearchParams();
  if (params.host) query.set("host", params.host);
  if (params.slug) query.set("slug", params.slug);
  if (params.currency) query.set("currency", params.currency);
  if ([...query.keys()].length === 0) return null;

  const response = await gatewayFetch(`/api/catalog/storefronts/public?${query.toString()}`, { cache: "no-store" });
  if (!response.ok) return null;
  const raw = (await response.json()) as Omit<StorefrontConfig, "taxRegime"> & { taxRegime: StorefrontTaxRegime | number };
  return {
    ...raw,
    taxRegime: typeof raw.taxRegime === "number" ? (STOREFRONT_TAX_REGIME[raw.taxRegime] ?? "Other") : raw.taxRegime,
  };
}

export type ProfileDto = {
  email: string;
  givenName: string | null;
  familyName: string | null;
  emailVerified: boolean;
};

export type AddressPurpose = "Billing" | "Shipping" | "Both";

export type AddressDto = {
  id: string;
  purpose: AddressPurpose;
  isDefault: boolean;
  name: string;
  line1: string;
  line2: string | null;
  city: string;
  postcode: string;
  country: string;
};

export async function getProfile(): Promise<ProfileDto | null> {
  const response = await gatewayFetch(`/api/identity/me`, { cache: "no-store" });
  return response.ok ? ((await response.json()) as ProfileDto) : null;
}

export async function getAddresses(): Promise<AddressDto[]> {
  const response = await gatewayFetch(`/api/identity/me/addresses`, { cache: "no-store" });
  if (!response.ok) return [];
  const addresses = (await response.json()) as Array<Omit<AddressDto, "purpose"> & { purpose: AddressPurpose | number }>;
  return addresses.map((address) => ({ ...address, purpose: normalizeAddressPurpose(address.purpose) }));
}

function normalizeAddressPurpose(purpose: AddressPurpose | number): AddressPurpose {
  if (purpose === 1 || purpose === "Billing") return "Billing";
  if (purpose === 2 || purpose === "Shipping") return "Shipping";
  return "Both";
}

export { GATEWAY_URL };

export type SavedPaymentMethodDto = {
  id: string;
  brand: string;
  last4: string;
  expMonth: number;
  expYear: number;
  isDefault: boolean;
};

export async function getSavedPaymentMethods(): Promise<SavedPaymentMethodDto[]> {
  const response = await gatewayFetch(`/api/payments/payment-methods/`, { cache: "no-store" });
  return response.ok ? ((await response.json()) as SavedPaymentMethodDto[]) : [];
}

export type CartItemDto = {
  productId: string;
  variantId: string | null;
  variantSku: string | null;
  slug: string;
  title: string;
  imageUrl: string | null;
  unitPriceMinor: number;
  currency: string;
  quantity: number;
};
export type CartDto = { cartId: string; items: CartItemDto[]; subtotalMinor: number; currency: string };

export async function getCart(): Promise<CartDto> {
  // Read-only: never sets cookies (forbidden in a Server Component render). The cart cookie
  // is established by the add-to-cart Server Action; an unkeyed read just returns empty.
  const response = await gatewayFetch(`/api/ordering/cart/`, { cache: "no-store" });
  if (!response.ok) {
    return { cartId: "", items: [], subtotalMinor: 0, currency: process.env.STORE_CURRENCY ?? "EUR" };
  }
  return (await response.json()) as CartDto;
}

export async function getOrderStatus(orderId: string): Promise<string | null> {
  const response = await gatewayFetch(`/api/ordering/orders/${orderId}/status`, { cache: "no-store" });
  return response.ok ? ((await response.json()) as { status: string }).status : null;
}

export type OrderSummaryDto = { id: string; status: string; grossMinor: number; currency: string; createdAt: string };

export async function getMyOrders(): Promise<OrderSummaryDto[]> {
  const response = await gatewayFetch(`/api/ordering/orders`, { cache: "no-store" });
  return response.ok ? ((await response.json()) as OrderSummaryDto[]) : [];
}

export type RefundableLine = { productId: string; title: string; unitPriceMinor: number; quantity: number };
export type RefundableOrder = { orderId: string; grossMinor: number; currency: string; lines: RefundableLine[] };

export async function getRefundableOrder(orderId: string): Promise<RefundableOrder | null> {
  const response = await gatewayFetch(`/api/support/orders/${orderId}/lines`, { cache: "no-store" });
  return response.ok ? ((await response.json()) as RefundableOrder) : null;
}
