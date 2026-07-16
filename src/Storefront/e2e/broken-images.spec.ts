import { test, expect, type Page } from "@playwright/test";

/**
 * Broken-image guard (fe_audit_5). Regression protection for the SafeImage fix: product/catalog
 * imagery must never render as a broken-link icon, even when a remote host serves SVG (placehold.co,
 * which 400s through the next/image optimizer) or is unreachable — SafeImage bypasses the optimizer
 * for SVG hosts and falls back to a local placeholder on any load error. Skips when the storefront
 * app isn't up (the dev stack is brought up fresh at the end of the run).
 */
test.describe("Storefront has no broken images", () => {
  // Pages that render product imagery (home + support surface featured products; search renders results).
  const routes = ["/", "/search?q=speaker", "/support"];

  for (const route of routes) {
    test(`no broken images on ${route}`, async ({ page }) => {
      test.skip(!(await storefrontUp(page)), "storefront app not reachable on :3000");

      await page.goto(route, { waitUntil: "networkidle" });
      await page.waitForTimeout(400);

      const broken = await page.evaluate(() =>
        Array.from(document.images)
          .filter((i) => !(i.complete && i.naturalWidth > 0))
          .map((i) => i.currentSrc || i.src),
      );

      expect(broken, `broken images on ${route}:\n${broken.join("\n")}`).toEqual([]);
    });
  }
});

/** True when the storefront home renders (app up on :3000); false → skip rather than fail. */
async function storefrontUp(page: Page): Promise<boolean> {
  try {
    const response = await page.goto("/", { timeout: 5_000 });
    return Boolean(response && response.status() < 500);
  } catch {
    return false;
  }
}
