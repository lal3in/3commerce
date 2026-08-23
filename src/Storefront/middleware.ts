import { NextResponse, type NextRequest } from "next/server";

// Entering a local storefront landing page (/{slug}) pins that storefront for the rest of the
// shopping session via a cookie — the main routes (home/search/PDP/cart/checkout) resolve their
// currency/tax context from it (lib/storefront-context.ts). Production domains resolve by Host
// instead; this cookie is the local-dev / path-slug path. Entering another slug switches it.
// Every top-level app route (app/<name>) that is NOT a storefront landing slug. A single-segment path
// that is one of these must never be mistaken for a storefront and pin the cookie — critically including
// "privacy" and "preview": Next.js PREFETCHES the in-viewport links (the consent banner links to /privacy),
// and a prefetch of /privacy would otherwise clobber the shopper's pinned storefront cookie with "privacy",
// blanking the catalog on the next navigation. Keep this in sync with the app/ route folders.
const RESERVED = new Set([
  "account", "api", "cart", "checkout", "login", "orders", "preview", "privacy", "products", "register", "search",
]);

export const STOREFRONT_COOKIE = "3c_storefront";

export function middleware(request: NextRequest) {
  const segments = request.nextUrl.pathname.split("/").filter(Boolean);
  const response = NextResponse.next();
  if (segments.length === 1) {
    const slug = segments[0].toLowerCase();
    if (!RESERVED.has(slug) && !slug.includes(".")) {
      response.cookies.set(STOREFRONT_COOKIE, slug, { path: "/", sameSite: "lax" });
    }
  }

  return response;
}

export const config = {
  // Only page navigations — skip Next internals, API routes, and static files.
  matcher: ["/((?!_next|api|.*\\..*).*)"],
};
