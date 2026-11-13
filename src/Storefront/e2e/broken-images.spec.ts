import { test, expect, pinStorefront } from "./fixtures";
import { abortExternalImages } from "../e2e-support/fixtures";
import type { Page } from "@playwright/test";

/**
 * Broken-image guard (fe_audit_5). Regression protection for the SafeImage fix: product/catalog
 * imagery must never render as a broken-link icon, even when a remote host serves SVG (placehold.co,
 * which 400s through the next/image optimizer) or is unreachable — SafeImage bypasses the optimizer
 * for SVG hosts and falls back to a local placeholder on any load error. Skips when the storefront
 * app isn't up (the dev stack is brought up fresh at the end of the run).
 *
 * Hermetic: the shared fixture (e2e-support/fixtures.ts) answers the external seed-image hosts with a local
 * stand-in, so these guards never depend on picsum.photos/placehold.co being reachable. Our own images
 * still hit the real server, so a genuinely broken internal image still fails here; the unreachable-host
 * case below aborts the seed hosts on purpose and requires SafeImage's placeholder.
 */
const SAFE_IMAGE_PLACEHOLDER = "data:image/svg+xml";

test.describe("Storefront has no broken images", () => {
  // Pages that render product imagery (home + support surface featured products; search renders results).
  const routes = ["/", "/search?q=speaker", "/support"];

  for (const route of routes) {
    test(`no broken images on ${route}`, async ({ page }) => {
      test.skip(!(await storefrontUp(page)), "storefront app not reachable on :3000");

      await page.goto(route, { waitUntil: "networkidle" });
      await page.waitForTimeout(400);

      const broken = await brokenImages(page);
      expect(broken, `broken images on ${route}:\n${broken.join("\n")}`).toEqual([]);
    });
  }

  test("product imagery from an unreachable image host renders the placeholder", async ({ page }) => {
    test.skip(!(await storefrontUp(page)), "storefront app not reachable on :3000");

    // Abort every seed-host image, direct or via the next/image optimizer — the host is "down" and fails fast.
    const aborted: string[] = [];
    await abortExternalImages(page, (url) => aborted.push(url));

    test.skip(!(await pinStorefront(page)), "no demo storefront with a published catalog (needs --data full)");
    await page.goto("/", { waitUntil: "networkidle" });
    await imagesSettled(page);
    test.skip(aborted.length === 0, "no seed-host product imagery on this storefront");

    // Each aborted image fired onError → SafeImage swapped in its inline placeholder, which decoded.
    await expect
      .poll(() => page.locator(`img[src^="${SAFE_IMAGE_PLACEHOLDER}"]`).count(), { timeout: 15_000 })
      .toBeGreaterThan(0);
    const broken = await brokenImages(page);
    expect(broken, `broken images on / with the image host unreachable:\n${broken.join("\n")}`).toEqual([]);
  });
});

/** Scroll the whole page (so lazy images are requested), then wait until every image loaded or failed. */
async function imagesSettled(page: Page): Promise<void> {
  await page.evaluate(() => window.scrollTo(0, document.body.scrollHeight));
  await page.waitForTimeout(300);
  await page.evaluate(() => window.scrollTo(0, 0));
  await page
    .waitForFunction(() => Array.from(document.images).every((i) => i.complete), undefined, { timeout: 15_000 })
    .catch(() => undefined);
  await page.waitForTimeout(400);
}

async function brokenImages(page: Page): Promise<string[]> {
  return page.evaluate(() =>
    Array.from(document.images)
      .filter((i) => !(i.complete && i.naturalWidth > 0))
      .map((i) => i.currentSrc || i.src),
  );
}

/** True when the storefront home renders (app up on :3000); false → skip rather than fail. */
async function storefrontUp(page: Page): Promise<boolean> {
  try {
    const response = await page.goto("/", { timeout: 5_000 });
    return Boolean(response && response.status() < 500);
  } catch {
    return false;
  }
}
