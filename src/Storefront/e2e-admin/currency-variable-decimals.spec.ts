import { test, expect } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

// currency_4: prove a 0-decimal currency (JPY) behaves end-to-end. A JPY demo storefront with JPY-priced
// products and placed JPY orders is seeded, so JPY money surfaces on the admin dashboards. JPY has zero
// decimal places, so every JPY amount must render as a whole (thousands-grouped) number with NO fractional
// part. The admin dashboards render money as "<amount> JPY" (Dashboard/Financials) or "JPY <amount>"
// (Mission Control) — see currency-coverage.spec.ts for the per-page convention.
test("JPY amounts render with zero decimal places on the money dashboards", async ({ page }) => {
  await loginAsAdmin(page); // lands on the dashboard

  // Dashboard renders money code-after, e.g. "3,300 JPY". A whole-number JPY amount must be present, and
  // no JPY amount may show a fractional part ("… .NN JPY").
  await page.waitForTimeout(2000); // let the Blazor circuit load the ledger balances
  await expect(page.getByText(/\d[\d,]* JPY\b/).first()).toBeVisible({ timeout: 10_000 });
  await expect(page.getByText(/\d[\d,]*\.\d+ JPY/)).toHaveCount(0);

  // Financials renders a per-currency P&L section (JPY <h2>) plus money rows, also code-after.
  await page.goto("/financials");
  await expect(page.getByRole("heading", { name: /financials/i })).toBeVisible();
  await page.waitForTimeout(2000);
  await expect(page.getByRole("heading", { name: "JPY", exact: true }).first()).toBeVisible({ timeout: 10_000 });
  await expect(page.getByText(/\d[\d,]* JPY\b/).first()).toBeVisible({ timeout: 10_000 });
  await expect(page.getByText(/\d[\d,]*\.\d+ JPY/)).toHaveCount(0);

  // Mission Control renders money code-before, e.g. "JPY 3,300".
  await page.goto("/mission-control");
  await expect(page.getByRole("heading", { name: /mission control/i })).toBeVisible();
  await page.waitForTimeout(2000);
  await expect(page.getByText(/JPY \d[\d,]*/).first()).toBeVisible({ timeout: 12_000 });
  await expect(page.getByText(/JPY \d[\d,]*\.\d/)).toHaveCount(0);
});

// currency_2/currency_4: the storefront currency picker is driven by the ENABLED registry, so a currency an
// operator has disabled is not offered for a new storefront. Add a throwaway code, disable it via the admin
// UI, then confirm the /commerce-ops picker no longer lists it (while a known enabled code, EUR, remains).
test("a disabled currency is not offered for a new storefront", async ({ page }) => {
  await loginAsAdmin(page);
  await page.goto("/currencies");
  await expect(page.getByRole("heading", { name: /currencies/i })).toBeVisible();
  await page.waitForTimeout(1500); // let the Blazor circuit load the registry

  // Add a fresh throwaway currency (unique per run), mirroring currencies.spec.ts.
  const code = `Z${String.fromCharCode(65 + Math.floor(Math.random() * 26))}${String.fromCharCode(65 + Math.floor(Math.random() * 26))}`;
  await page.getByPlaceholder("JPY").fill(code);
  await page.getByPlaceholder("Japanese Yen").fill("Throwaway Currency");
  await page.getByPlaceholder("¥").fill("¤");
  await page.getByRole("button", { name: "Add", exact: true }).click();

  // The new row lands enabled; find it by its ISO code cell and click its "Disable" action
  // (Currencies.razor: <button ... @onclick="() => ToggleAsync(c.Code, false)">Disable</button>).
  const row = page.locator("tr").filter({ has: page.getByText(code, { exact: true }) });
  await expect(row.getByRole("button", { name: "Disable", exact: true })).toBeVisible({ timeout: 10_000 });
  await row.getByRole("button", { name: "Disable", exact: true }).click();

  // ToggleAsync reloads the list; the row now shows the "Disabled" status badge.
  await page.waitForTimeout(1500);
  await expect(row.getByText("Disabled", { exact: true })).toBeVisible({ timeout: 10_000 });

  // The new-storefront currency picker (the <select> containing option[value="EUR"]) is driven by the
  // ENABLED registry (CurrencySelect fetches /currencies without includeDisabled), so the disabled code is
  // gone while EUR — a known enabled code — is still offered.
  await page.goto("/commerce-ops");
  await page.waitForTimeout(2500); // let the CurrencySelect fetch the registry
  const select = page.locator("select").filter({ has: page.locator('option[value="EUR"]') }).first();
  await expect(select).toBeVisible({ timeout: 10_000 });
  await expect(select.locator('option[value="EUR"]')).toHaveCount(1);
  await expect(select.locator(`option[value="${code}"]`)).toHaveCount(0);
});
