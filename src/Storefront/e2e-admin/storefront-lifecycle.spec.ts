import { test, expect } from "@playwright/test";
import { loginAsAdmin } from "./helpers";

const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";
const TENANT_ID = "00000000-0000-0000-0000-000000000001";

test.describe("Commerce ops storefront lifecycle", () => {
  // Regression for the Preview→Preview 400: every row offered all four transition buttons
  // regardless of state, so clicking Preview on an already-previewing storefront failed with
  // "Storefront state Preview cannot perform this transition." Buttons must now mirror the
  // domain state machine (Draft → Preview → Active ⇄ Paused, Paused → Preview, Archive anywhere).
  test("only domain-allowed transitions are offered and the lifecycle drives cleanly", async ({ page, request }) => {
    // Seed a fresh Draft storefront via the gateway so the test owns its row.
    const login = await request.post(`${GATEWAY}/api/identity/login`, {
      data: { email: "admin@3commerce.local", password: "dev-admin-password-1" },
    });
    expect(login.ok()).toBeTruthy();
    const name = `Lifecycle E2E ${Date.now()}`;
    const created = await request.post(`${GATEWAY}/api/catalog/admin/storefronts`, {
      data: { tenantId: TENANT_ID, name, visibility: 1, currency: "EUR", taxRegime: 0, taxRateBasisPoints: 0 },
    });
    expect(created.ok()).toBeTruthy();

    await loginAsAdmin(page);
    await page.goto("/commerce-ops");
    await page.getByRole("button", { name: /load storefronts/i }).click();

    const row = page.locator("tr", { hasText: name });
    await expect(row).toBeVisible();
    // Assert on the State cell, not the whole row — button captions like "Preview" would
    // otherwise satisfy a row-level substring match before the state actually changed.
    const stateCell = row.locator("td").nth(4);

    // Draft: Preview/Pause/Archive are offered, Activate is not (Draft cannot activate).
    await expect(stateCell).toHaveText("Draft");
    await expect(row.getByRole("button", { name: "Preview" })).toBeVisible();
    await expect(row.getByRole("button", { name: "Activate" })).toHaveCount(0);
    await expect(row.getByRole("button", { name: "Pause" })).toBeVisible();

    // Draft → Preview. The previewing row must no longer offer Preview (the old 400).
    await row.getByRole("button", { name: "Preview" }).click();
    await expect(stateCell).toHaveText("Preview");
    await expect(row.getByRole("button", { name: "Preview" })).toHaveCount(0);
    await expect(row.getByRole("button", { name: "Activate" })).toBeVisible();
    await expect(page.getByText(/cannot perform this transition/i)).toHaveCount(0);

    // Preview → Paused, and Paused may re-enter Preview.
    await row.getByRole("button", { name: "Pause" }).click();
    await expect(stateCell).toHaveText("Paused");
    await expect(row.getByRole("button", { name: "Pause" })).toHaveCount(0);
    await row.getByRole("button", { name: "Preview" }).click();
    await expect(stateCell).toHaveText("Preview");

    // Archive is terminal — the admin list excludes Archived storefronts, so the row disappears.
    await row.getByRole("button", { name: "Archive" }).click();
    await expect(page.getByText("Storefront archive requested.")).toBeVisible();
    await expect(row).toHaveCount(0);
  });
});
