import { test as base, type BrowserContext, type Page } from "@playwright/test";
import { ALLOWED_IMAGE_HOSTS } from "../lib/image-hosts";

// Everything else (expect, request, types…) passes straight through, so a spec swaps only its module path.
// The local `test` below shadows the star re-export's `test`.
export * from "@playwright/test";

/**
 * Hermetic external images — the shared base `test` for EVERY Playwright project (storefront, admin,
 * supplier). Import `test` from here (or from e2e/fixtures.ts, which builds on it), never from
 * @playwright/test: the lint rule in .eslintrc.json enforces it.
 *
 * Why: the demo seed points product imagery at third-party hosts (picsum.photos from the Catalog
 * SampleDataImporter, placehold.co from dev-dummy-data.sh — the same list as lib/image-hosts). When one of
 * them is slow or down, a hung TCP connect never fires the image's `onerror`, so (a) the broken-image guards
 * count every seed thumbnail as broken and (b) `waitForLoadState("networkidle")` never arrives and the
 * screenshot specs time out. Neither failure says anything about our code.
 *
 * So every browser context answers requests for those hosts — directly, or through the storefront's
 * next/image optimizer (`/_next/image?url=<seed host>`) — with a small local stand-in image via
 * route.fulfill. Image loading and network idle become deterministic, and nothing else changes:
 *  - our OWN images (bundled placeholders, same-origin assets, any non-seed host) still hit the real server,
 *    so a genuinely broken internal image still fails the broken-image guards;
 *  - a spec that wants the failure path registers its own page.route (page routes take precedence over
 *    these context routes) and aborts the request — see the "unreachable host" cases in the broken-images
 *    specs, which prove the product's placeholder fallback.
 */
export const EXTERNAL_IMAGE_HOSTS: readonly string[] = ALLOWED_IMAGE_HOSTS;

/** Response header marking a fulfilled stand-in, so a spec can tell it apart from a real image. */
export const STAND_IN_HEADER = "x-e2e-stand-in";

// A neutral 600×600 tile, deliberately unlike the products' own placeholders (admin /img/image-placeholder.svg,
// storefront SafeImage's inline SVG) so a screenshot never confuses "stubbed seed image" with "fallback fired".
const STAND_IN_SVG =
  "<svg xmlns='http://www.w3.org/2000/svg' width='600' height='600' viewBox='0 0 600 600'>" +
  "<rect width='600' height='600' fill='#e2e8f0'/>" +
  "<path d='M150 420l110-120 80 80 60-50 50 90z' fill='#cbd5e1'/><circle cx='400' cy='210' r='40' fill='#cbd5e1'/>" +
  "</svg>";

function isSeedHost(url: string): boolean {
  try {
    return EXTERNAL_IMAGE_HOSTS.includes(new URL(url).hostname);
  } catch {
    return false;
  }
}

/**
 * True for a request that would load an external seed image: the seed host itself, or the storefront's
 * next/image optimizer proxying one (the optimizer fetches it server-side, out of the browser's reach, and
 * only gives up after its 7 s upstream timeout).
 */
export function isExternalSeedImage(url: URL): boolean {
  if (isSeedHost(url.href)) return true;
  if (url.pathname === "/_next/image") {
    const inner = url.searchParams.get("url");
    return inner !== null && isSeedHost(inner);
  }
  return false;
}

/** Answer every external seed-image request in `target` with the local stand-in. */
export async function routeExternalImagesToStandIn(target: BrowserContext | Page): Promise<void> {
  await target.route(isExternalSeedImage, (route) =>
    route.fulfill({
      status: 200,
      contentType: "image/svg+xml",
      headers: { [STAND_IN_HEADER]: "1", "cache-control": "no-store" },
      body: STAND_IN_SVG,
    }),
  );
}

/** Abort every external seed-image request in `page` — simulates the seed image host being unreachable. */
export async function abortExternalImages(page: Page, onAbort?: (url: string) => void): Promise<void> {
  await page.route(isExternalSeedImage, (route) => {
    onAbort?.(route.request().url());
    return route.abort("addressunreachable");
  });
}

export const test = base.extend({
  // Overrides the built-in context fixture, so the page (and any context.newPage()) inherits the routes.
  // Not a React hook: Playwright's fixture callback is named `use`.
  context: async ({ context }, use) => {
    await routeExternalImagesToStandIn(context);
    await use(context);
  },
});
