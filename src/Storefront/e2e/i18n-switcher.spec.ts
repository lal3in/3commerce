import { test, expect, type Page } from "@playwright/test";

/**
 * Storefront language switcher (i18n_1). Proves the header switcher changes VISIBLE UI text for the
 * session without touching the URL — English base → 中文 (the seeded token second locale). Uses the
 * home hero, which renders from the message catalog alone (no product/storefront seed needed), so
 * this is independent of --data full. Skips gracefully when the storefront app isn't up (the dev
 * stack is brought up fresh at the end of the run — see currency-tax.spec for the same guard style).
 */
test.describe("Storefront i18n language switcher", () => {
  test("switching to 中文 re-renders the UI in Chinese, with no locale URL segment", async ({ page }) => {
    test.skip(!(await storefrontUp(page)), "storefront app not reachable on :3000");

    // Baseline: English hero + English <html lang>.
    await expect(page.getByRole("heading", { name: /everything, sourced for you/i })).toBeVisible();
    await expect(page.locator("html")).toHaveAttribute("lang", "en");

    // The switcher lists exactly the locales with a catalog; pick Chinese by its endonym label.
    const switcher = page.getByLabel("Language");
    await expect(switcher).toBeVisible();
    await switcher.selectOption("zh");

    // Same URL (no /zh segment), but the visible text and <html lang> flip to Chinese.
    await expect(page).toHaveURL(/\/$/);
    await expect(page.locator("html")).toHaveAttribute("lang", "zh");
    await expect(page.getByRole("heading", { name: "万物皆备，为您甄选。" })).toBeVisible();
    await expect(page.getByRole("link", { name: "商店" })).toBeVisible(); // header "Shop" → 商店

    // The choice persists across navigation for the session (cookie-backed, not URL-backed).
    await page.goto("/cart");
    await expect(page.locator("html")).toHaveAttribute("lang", "zh");

    // Switch back to English so the run leaves no sticky locale cookie for later specs.
    await page.goto("/");
    await page.getByLabel("Language").selectOption("en");
    await expect(page.locator("html")).toHaveAttribute("lang", "en");
  });
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
