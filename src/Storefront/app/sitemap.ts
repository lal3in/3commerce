import type { MetadataRoute } from "next";
import { searchProducts } from "@/lib/gateway";
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
    const { hits } = await searchProducts({ pageSize: 200 });
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
