import type { MetadataRoute } from "next";
import { searchProducts } from "@/lib/gateway";
import { resolveStorefront } from "@/lib/storefront-context";
import { isPrivateStorefront, siteUrl } from "@/lib/seo";

// XML sitemap (mt5_8). Private storefronts emit nothing. Product fetch is best-effort so the build
// succeeds even when the gateway/catalog is unavailable.
export default async function sitemap(): Promise<MetadataRoute.Sitemap> {
  if (isPrivateStorefront()) return [];

  const base = siteUrl();
  const now = new Date();
  const core: MetadataRoute.Sitemap = [
    { url: base, lastModified: now, changeFrequency: "daily", priority: 1 },
    { url: `${base}/search`, lastModified: now, changeFrequency: "daily", priority: 0.8 },
  ];

  try {
    // Only a resolved storefront's published catalog is crawlable; no storefront → core URLs only.
    const storefront = await resolveStorefront();
    if (!storefront) return core;
    const { hits } = await searchProducts({ pageSize: 200, currency: storefront.currency, storefrontId: storefront.id });
    return [
      ...core,
      ...hits.map((hit) => ({
        url: `${base}/products/${hit.slug}`,
        lastModified: now,
        changeFrequency: "weekly" as const,
        priority: 0.6,
      })),
    ];
  } catch {
    return core;
  }
}
