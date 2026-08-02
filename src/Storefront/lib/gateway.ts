import { cookies } from "next/headers";
import type { ThemeTokens } from "@/lib/theme";

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
  // When set, results are scoped to products PUBLISHED to this storefront (per-storefront merchandising).
  storefrontId?: string;
}): Promise<SearchResult> {
  const query = new URLSearchParams();
  if (params.q) query.set("q", params.q);
  if (params.category) query.set("category", params.category);
  if (params.attrs) query.set("attrs", params.attrs);
  if (params.currency) query.set("currency", params.currency);
  if (params.type) query.set("type", String(params.type));
  if (params.storefrontId) query.set("storefrontId", params.storefrontId);
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

export async function getProduct(slug: string, currency?: string, storefrontId?: string): Promise<ProductDetail | null> {
  // Product pages are cacheable/ISR-friendly; revalidate periodically. Currency-specific when set, and
  // storefront-scoped when set (an unpublished product 404s just like it's hidden from the listing).
  const query = new URLSearchParams();
  if (currency) query.set("currency", currency);
  if (storefrontId) query.set("storefrontId", storefrontId);
  const qs = query.toString();
  const response = await gatewayFetch(`/api/catalog/products/${encodeURIComponent(slug)}${qs ? `?${qs}` : ""}`, {
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
  // BCP-47 UI language this storefront defaults to (i18n_0). A shopper's `3c_locale` cookie overrides
  // it for their session. Independent of currency/tax — see i18n/request.ts.
  defaultLanguage: string;
  // Per-storefront theme token overrides (mt5_6), already sanitized server-side. Undefined → default look.
  // mergeTheme() re-sanitizes as defence-in-depth before these become CSS vars.
  theme?: Partial<ThemeTokens>;
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
  const raw = (await response.json()) as Omit<StorefrontConfig, "taxRegime" | "defaultLanguage" | "theme"> & {
    taxRegime: StorefrontTaxRegime | number;
    defaultLanguage?: string;
    theme?: Partial<ThemeTokens> | null;
  };
  return {
    ...raw,
    taxRegime: typeof raw.taxRegime === "number" ? (STOREFRONT_TAX_REGIME[raw.taxRegime] ?? "Other") : raw.taxRegime,
    // Pre-i18n_0 storefronts (or an older Catalog) simply have no language configured → English.
    defaultLanguage: raw.defaultLanguage ?? "en",
    // Pre-mt5_6 storefronts (or an unthemed store) have no theme → the default look.
    theme: raw.theme ?? undefined,
  };
}

export type ProfileDto = {
  email: string;
  title: string | null;
  firstName: string | null;
  middleName: string | null;
  lastName: string | null;
  preferredName: string | null;
  phone: string | null;
  dateOfBirth: string | null;
  marketingConsent: boolean;
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

export type OrderLineDetail = {
  productId: string; variantId: string | null; variantSku: string | null; title: string;
  unitPriceMinor: number; discountMinor: number; quantity: number; fulfilmentType: string; billingMode: string;
};
export type OrderShippingAddress = { name: string; line1: string; city: string; postcode: string; country: string };
export type OrderDetail = {
  id: string; status: string; email: string;
  netMinor: number; shippingMinor: number; discountMinor: number; taxMinor: number; grossMinor: number; currency: string;
  createdAt: string; lines: OrderLineDetail[];
  publicOrderNumber: number; paymentOption: string; paymentInstrumentSummary: string | null; paymentProvider: string;
  partiallyRefunded: boolean; shippingAddress: OrderShippingAddress | null;
};

// The signed-in customer's own order, full detail (addresses, payment, items, amount breakdown).
export async function getMyOrder(orderId: string): Promise<OrderDetail | null> {
  const response = await gatewayFetch(`/api/ordering/orders/${orderId}`, { cache: "no-store" });
  return response.ok ? ((await response.json()) as OrderDetail) : null;
}

export type RefundableLine = { productId: string; title: string; unitPriceMinor: number; quantity: number };
export type RefundableOrder = { orderId: string; grossMinor: number; currency: string; lines: RefundableLine[] };

export async function getRefundableOrder(orderId: string): Promise<RefundableOrder | null> {
  const response = await gatewayFetch(`/api/support/orders/${orderId}/lines`, { cache: "no-store" });
  return response.ok ? ((await response.json()) as RefundableOrder) : null;
}

export type ProductReview = { id: string; authorName: string; rating: number; comment: string | null; createdAt: string };
export type ReviewSummary = { productId: string; average: number; count: number; items: ProductReview[] };

// Public — everyone (incl. guests) sees a product's ratings & reviews.
export async function getProductReviews(productId: string): Promise<ReviewSummary> {
  const response = await gatewayFetch(`/api/catalog/products/${productId}/reviews`, { cache: "no-store" });
  return response.ok ? ((await response.json()) as ReviewSummary) : { productId, average: 0, count: 0, items: [] };
}

export type TicketMessage = { author: string; body: string; createdAt: string };
export type TicketAttachment = { id: string; fileName: string; contentType: string; sizeBytes: number };
export type OrderTicket = { id: string; orderId: string; email: string; reason: string; status: string; createdAt: string; messages: TicketMessage[]; attachments: TicketAttachment[] };

// The signed-in customer's support tickets for one order — their "history chat" per request.
export async function getOrderTickets(orderId: string): Promise<OrderTicket[]> {
  const response = await gatewayFetch(`/api/support/tickets/by-order/${orderId}`, { cache: "no-store" });
  return response.ok ? ((await response.json()) as OrderTicket[]) : [];
}

export type CustomerRmaLine = { productId: string; title: string; quantity: number; unitPriceMinor: number };
// state is the RMA saga state: Requested | AwaitingReturn | RefundPending | Denied | RefundIssued.
export type CustomerRma = {
  id: string;
  amountMinor: number;
  currency: string;
  reason: string;
  state: string;
  createdAt: string;
  lines: CustomerRmaLine[];
};

// The signed-in customer's refund/return requests for one order, with their live lifecycle status.
export async function getOrderRefunds(orderId: string): Promise<CustomerRma[]> {
  const response = await gatewayFetch(`/api/support/tickets/rmas/by-order/${orderId}`, { cache: "no-store" });
  return response.ok ? ((await response.json()) as CustomerRma[]) : [];
}
