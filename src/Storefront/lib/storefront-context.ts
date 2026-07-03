import { cache } from "react";
import { cookies, headers } from "next/headers";
import { getStorefrontConfig, type StorefrontConfig } from "@/lib/gateway";

const STOREFRONT_COOKIE = "3c_storefront";

/**
 * Resolve the active storefront for this request (rev_5 / F5). Order:
 *  1. `3c_storefront` cookie — set by middleware when the shopper enters a /{slug} landing page
 *     (the local-dev / path-slug flow).
 *  2. Request Host — production, where each storefront has its own domain (the same map the
 *     gateway's DomainResolutionMiddleware uses).
 *  3. STOREFRONT_SLUG env — single-storefront deployments / local override.
 * Null = no storefront context: routes fall back to base-currency behavior, exactly as before.
 * Cached per request so every component in the tree shares one lookup.
 */
export const resolveStorefront = cache(async (): Promise<StorefrontConfig | null> => {
  const cookieStore = await cookies();
  const slug = cookieStore.get(STOREFRONT_COOKIE)?.value;
  if (slug) {
    const bySlug = await getStorefrontConfig({ slug });
    if (bySlug) return bySlug;
  }

  const headerStore = await headers();
  const host = headerStore.get("host");
  if (host) {
    const byHost = await getStorefrontConfig({ host: host.split(":")[0] });
    if (byHost) return byHost;
  }

  if (process.env.STOREFRONT_SLUG) {
    return getStorefrontConfig({ slug: process.env.STOREFRONT_SLUG });
  }

  return null;
});
