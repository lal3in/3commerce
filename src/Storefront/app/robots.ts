import type { MetadataRoute } from "next";
import { isPrivateStorefront, siteUrl } from "@/lib/seo";

// robots.txt (mt5_8). GOTCHA: a private storefront is fully disallowed (noindex). Otherwise crawlers
// may index public pages but not account/cart/checkout/api surfaces.
export default function robots(): MetadataRoute.Robots {
  const base = siteUrl();

  if (isPrivateStorefront()) {
    return { rules: { userAgent: "*", disallow: "/" } };
  }

  return {
    rules: { userAgent: "*", allow: "/", disallow: ["/account", "/cart", "/checkout", "/api"] },
    sitemap: `${base}/sitemap.xml`,
  };
}
