import { NextResponse, type NextRequest } from "next/server";

// Entering a local storefront landing page (/{slug}) pins that storefront for the rest of the
// shopping session via a cookie — the main routes (home/search/PDP/cart/checkout) resolve their
// currency/tax context from it (lib/storefront-context.ts). Production domains resolve by Host
// instead; this cookie is the local-dev / path-slug path. Entering another slug switches it.
const RESERVED = new Set([
  "account", "api", "cart", "checkout", "login", "orders", "products", "register", "search",
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
