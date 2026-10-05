import { test, expect } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

// Guards PR1 admin correctness: action buttons must succeed and always surface feedback
// (no silent no-ops). expect.toPass absorbs the Blazor pre-circuit window until PR2 lands.
test.describe("Admin actions give feedback", () => {
  test("creating a payment account succeeds (numeric mode) and shows a status message", async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto("/payment-accounts");
    await expect(page.getByRole("heading", { name: /payment accounts/i })).toBeVisible();

    // Load the tenant's accounts — reveals the create form.
    await expect(async () => {
      await page.getByRole("button", { name: /^load$/i }).click();
      await expect(page.getByRole("button", { name: /create \(draft\)/i })).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 20_000 });

    const section = page.locator("section", { hasText: "New payment account" });
    const name = `E2E account ${Date.now()}`;

    // Payment accounts are per-storefront (ADR-0043): the create form now requires selecting a
    // storefront, and the API rejects a create without one. The storefront dropdown's first option is a
    // "choose…" placeholder (value=""); real storefronts only exist on a --data full stack. Match the
    // repo convention (financials.spec) and skip when none are configured rather than fail.
    const storefrontSelect = section.locator("select").first();
    const storefrontValues = await storefrontSelect
      .locator("option")
      .evaluateAll((opts) => opts.map((o) => (o as HTMLOptionElement).value).filter((v) => v !== ""));
    test.skip(
      storefrontValues.length === 0,
      "no storefronts configured (needs --data full); per-storefront payment accounts require one",
    );

    // Select storefront + fill + submit as one retrying unit so @bind binds once the circuit is live
    // (pre-circuit race — the real fix is PR2). Mode defaults to Test (numeric 1); before the fix this 500'd.
    await expect(async () => {
      await storefrontSelect.selectOption(storefrontValues[0]);
      await section.getByLabel("Name").fill(name);
      await page.getByRole("button", { name: /create \(draft\)/i }).click();
      await expect(page.getByText(/account created \(draft\)/i)).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 20_000 });
  });

  test("running the sample importer surfaces feedback (not a silent no-op)", async ({ page }) => {
    test.setTimeout(150_000); // the import itself is synchronous and can take a while on a busy stack
    await loginAsAdmin(page);
    await page.goto("/imports");
    await expect(page.getByRole("heading", { name: /catalog imports/i })).toBeVisible();

    // The import runs SYNCHRONOUSLY inside the POST (Catalog TriggerImport), and the Blazor handler only
    // re-renders once the POST + the runs-list refresh both return — so the feedback can legitimately take
    // many seconds on a busy stack (right after a seed, or mid-suite after bulk specs). The old
    // `toPass { click; expect(3s) }` re-clicked whenever a run outlived 3 s: that click lands as soon as the
    // button re-enables, which clears the status message and starts ANOTHER import, so the feedback was never
    // observed. Click only while the page is idle with no feedback yet (absorbs a click lost before the
    // circuit is live), prove the click registered (button busy, or feedback already shown), then wait for
    // the real effect — the run finishing — on the feedback locator itself.
    const runButton = page.getByRole("button", { name: /run sample importer/i });
    const busyButton = page.getByRole("button", { name: /run sample importer/i, disabled: true });
    const feedback = page.getByText(/import run started|import failed/i);
    await expect(async () => {
      // Order matters: enabled-then-no-feedback means truly idle (status and re-enable render together).
      const idle = await runButton.isEnabled();
      if (await feedback.isVisible()) return;
      if (idle) await runButton.click();
      await expect(feedback.or(busyButton).first()).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 20_000 });
    await expect(feedback).toBeVisible({ timeout: 90_000 });
  });

  test("price-entry fields carry the ADR-0038 tax-convention note (per-currency editor)", async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto("/catalog");
    await expect(page.getByRole("heading", { name: /^catalog$/i })).toBeVisible();

    await expect(async () => {
      await page.getByRole("button", { name: /new product/i }).click();
      await expect(page.getByRole("heading", { name: /new product/i })).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 20_000 });

    // The operator-visibility contract (ADR-0038): the per-currency note is on-screen, and the
    // base-price input's tooltip states the inclusive/exclusive convention.
    await expect(page.getByText(/on AU GST \/ EU VAT storefronts the price INCLUDES tax/i)).toBeVisible();
    const basePrice = page.locator('input[title*="ADR-0038"]').first();
    await expect(basePrice).toBeVisible();
    await expect(page.getByRole("button", { name: /\+ currency price/i })).toBeVisible();
  });
});
