import { isPrivateStorefront, siteUrl } from "@/lib/seo";

// /llms.txt (mt5_8): an agent-friendly summary of the store + pointers to machine-readable surfaces.
export const dynamic = "force-static";

export function GET(): Response {
  const base = siteUrl();
  const headers = { "content-type": "text/plain; charset=utf-8" };

  if (isPrivateStorefront()) {
    return new Response("# Private storefront\n\nThis storefront is not publicly indexed.\n", { headers });
  }

  const body = `# 3commerce

> A multi-tenant e-commerce storefront. Product, offer, and availability data are exposed as
> schema.org JSON-LD on each product page for machine consumption.

## Browse
- Catalog search: ${base}/search
- Product pages: ${base}/products/{slug} (Product + Offer + Breadcrumb JSON-LD)

## Machine-readable
- Sitemap: ${base}/sitemap.xml
- Robots: ${base}/robots.txt

## Notes
- Prices and availability are authoritative on the product page; do not infer ratings or reviews — none are published.
`;

  return new Response(body, { headers });
}
