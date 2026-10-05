import { test, expect, type Page, type Locator } from "@playwright/test";
import { loginAsAdmin } from "./helpers";
import { capture } from "../e2e-support/screenshots";

/**
 * Admin broken-image guard (fe_audit_4, admin half — mirrors e2e/broken-images.spec.ts). The admin's two
 * remote-URL <img> tags (Catalog list thumbnail, shared ImageInput preview) carry a plain-HTML onerror that
 * swaps in the bundled /img/image-placeholder.svg, so an offline/unreachable image host renders a neutral
 * placeholder instead of the browser's broken-link icon. The unreachable host is simulated deterministically
 * by aborting the image request with page.route. Skips when the admin app isn't up.
 */
const PLACEHOLDER = "/img/image-placeholder.svg";

test.describe("Admin has no broken images", () => {
  test("no broken images on /catalog", async ({ page }) => {
    test.skip(!(await adminUp(page)), "admin app not reachable");
    await loginAsAdmin(page);
    await page.goto("/catalog");
    await catalogSettled(page);

    const broken = await brokenImages(page);
    expect(broken, `broken images on /catalog:\n${broken.join("\n")}`).toEqual([]);
  });

  test("catalog thumbnail from an unreachable host renders the placeholder", async ({ page }) => {
    test.skip(!(await adminUp(page)), "admin app not reachable");
    await abortRemoteImages(page);
    await loginAsAdmin(page);
    await page.goto("/catalog");
    await catalogSettled(page);

    const thumbs = page.locator("table tbody img");
    test.skip((await thumbs.count()) === 0, "no catalog products with images in this environment");

    // Every thumbnail's original request was aborted → each one must have degraded to the placeholder.
    await expectPlaceholder(thumbs.first());
    const broken = await brokenImages(page);
    expect(broken, `broken images on /catalog with image hosts unreachable:\n${broken.join("\n")}`).toEqual([]);

    await capture(page.locator("table").first(), "catalog-thumbnails-placeholder");
  });

  test("image URL preview from an unreachable host renders the placeholder", async ({ page }) => {
    test.skip(!(await adminUp(page)), "admin app not reachable");
    await abortRemoteImages(page);
    await loginAsAdmin(page);
    await page.goto("/catalog");
    await page.getByRole("button", { name: /new product/i }).click();

    const images = page.locator("fieldset", { has: page.locator("legend", { hasText: "Image URLs" }) }).first();
    await expect(images).toBeVisible();
    await images.getByRole("button", { name: /add image/i }).click();

    const url = images.locator("input[placeholder^='https://']").last();
    await url.fill("https://unreachable.invalid/e2e-broken-image.png");
    await url.press("Tab"); // ImageInput binds on change

    const preview = images.locator("img[alt='preview']").last();
    await expectPlaceholder(preview);

    await capture(images, "image-input-preview-placeholder");
  });
});

/** Abort every image request except the bundled placeholder — simulates an unreachable image host. */
async function abortRemoteImages(page: Page): Promise<void> {
  await page.route("**/*", (route) => {
    const request = route.request();
    if (request.resourceType() === "image" && !new URL(request.url()).pathname.endsWith(PLACEHOLDER)) {
      return route.abort("addressunreachable");
    }
    return route.continue();
  });
}

/** The img fell back: its src is the bundled placeholder and it actually decoded (not a broken icon). */
async function expectPlaceholder(img: Locator): Promise<void> {
  await expect(img).toHaveAttribute("src", new RegExp(`${PLACEHOLDER.replace(/[./]/g, "\\$&")}$`));
  await expect.poll(() => img.evaluate((i: HTMLImageElement) => i.complete && i.naturalWidth > 0)).toBe(true);
}

/** Catalog list rendered (table or the empty message) and every image finished loading or failing. */
async function catalogSettled(page: Page): Promise<void> {
  await expect(page.getByRole("heading", { name: /catalog/i })).toBeVisible();
  await expect(page.getByText(/^Loading/)).toHaveCount(0);
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

/** True when the admin login page renders; false → skip rather than fail. */
async function adminUp(page: Page): Promise<boolean> {
  try {
    const response = await page.goto("/login", { timeout: 5_000 });
    return Boolean(response && response.status() < 500);
  } catch {
    return false;
  }
}
