// SEO + agent-friendly metadata helpers (mt5_8): canonical base, schema.org JSON-LD builders, and the
// private-storefront flag. GOTCHA: we never emit aggregateRating/review — we have none, and faking
// social proof is prohibited. Private storefronts are marked noindex (see app/robots.ts).

import { minorToMajor } from "@/lib/money";

export function siteUrl(): string {
  return (process.env.SITE_URL ?? "http://localhost:3000").replace(/\/$/, "");
}

export function isPrivateStorefront(): boolean {
  return process.env.STOREFRONT_PRIVATE === "true";
}

type Json = Record<string, unknown>;

interface ProductLike {
  title: string;
  description?: string;
  brand?: string;
  imageUrls: string[];
  variants: { priceMinor: number; currency: string; inStock: boolean }[];
}

const availability = (inStock: boolean) =>
  inStock ? "https://schema.org/InStock" : "https://schema.org/OutOfStock";

// JSON-LD wants a numeric major-unit price; honor each currency's minor-unit exponent
// (JPY→0, KWD→3) via the same helper as formatMoney rather than a fixed /100 (currency_3 / P3).
const money = (minor: number, currency: string) => minorToMajor(minor, currency);

export function productJsonLd(product: ProductLike, url: string): Json {
  const prices = product.variants.map((v) => v.priceMinor);
  const currency = product.variants[0]?.currency ?? "EUR";
  const anyInStock = product.variants.some((v) => v.inStock);

  const offers: Json =
    product.variants.length > 1
      ? {
          "@type": "AggregateOffer",
          priceCurrency: currency,
          lowPrice: money(Math.min(...prices), currency),
          highPrice: money(Math.max(...prices), currency),
          offerCount: product.variants.length,
          availability: availability(anyInStock),
          url,
        }
      : {
          "@type": "Offer",
          priceCurrency: currency,
          price: money(prices[0] ?? 0, currency),
          availability: availability(anyInStock),
          url,
        };

  return {
    "@context": "https://schema.org",
    "@type": "Product",
    name: product.title,
    description: product.description,
    image: product.imageUrls,
    ...(product.brand ? { brand: { "@type": "Brand", name: product.brand } } : {}),
    offers,
    // No aggregateRating / review: we have no genuine reviews and must not fabricate them (GOTCHA).
  };
}

export function breadcrumbJsonLd(items: { name: string; url: string }[]): Json {
  return {
    "@context": "https://schema.org",
    "@type": "BreadcrumbList",
    itemListElement: items.map((item, index) => ({
      "@type": "ListItem",
      position: index + 1,
      name: item.name,
      item: item.url,
    })),
  };
}

export function organizationJsonLd(): Json {
  const base = siteUrl();
  return {
    "@context": "https://schema.org",
    "@type": "Organization",
    name: "3commerce",
    url: base,
  };
}

export function webSiteJsonLd(): Json {
  const base = siteUrl();
  return {
    "@context": "https://schema.org",
    "@type": "WebSite",
    url: base,
    name: "3commerce",
    potentialAction: {
      "@type": "SearchAction",
      target: `${base}/search?q={search_term_string}`,
      "query-input": "required name=search_term_string",
    },
  };
}
