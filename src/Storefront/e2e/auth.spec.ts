import { test, expect } from "@playwright/test";

// Account flows through the storefront (Server Actions → gateway → Identity).
test.describe("Account", () => {
  test("unauthenticated account page redirects to login", async ({ page }) => {
    await page.goto("/account");
    await expect(page).toHaveURL(/\/login/);
    await expect(page.getByRole("heading", { name: /log in/i })).toBeVisible();
  });

  test("register then log in reaches the account page", async ({ page }) => {
    const email = `e2e-${Date.now()}@example.com`;
    const password = "a-strong-password";

    await page.goto("/register");
    await page.getByLabel("Email").fill(email);
    await page.getByLabel(/password/i).fill(password);
    await page.getByRole("button", { name: /create account/i }).click();

    await expect(page).toHaveURL(/\/login/);
    await page.getByLabel("Email").fill(email);
    await page.getByLabel("Password").fill(password);
    await page.getByRole("button", { name: /log in/i }).click();

    await expect(page).toHaveURL(/\/account/);
    await expect(page.getByText(email)).toBeVisible();
    await expect(page.getByRole("button", { name: /log out/i })).toBeVisible();
  });

  test("wrong password shows an error", async ({ page }) => {
    await page.goto("/login");
    await page.getByLabel("Email").fill("nobody@example.com");
    await page.getByLabel("Password").fill("definitely-wrong");
    await page.getByRole("button", { name: /log in/i }).click();
    // Target the message text (Next's route announcer also carries role="alert").
    await expect(page.getByText(/invalid email or password/i)).toBeVisible();
  });
});
