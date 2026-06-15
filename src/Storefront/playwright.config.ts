import { defineConfig, devices } from "@playwright/test";

// Drives the storefront AND the Blazor admin console in a real browser against a running
// stack (storefront :3000, admin :5200, gateway :8080). Bring the stack up first, then:
//   npm run test:e2e                          (both)
//   npm run test:e2e -- --project=storefront  (just the storefront)
const STOREFRONT = process.env.STOREFRONT_URL ?? "http://localhost:3000";
const ADMIN = process.env.ADMIN_URL ?? "http://localhost:5200";

export default defineConfig({
  timeout: 60_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [["list"]],
  use: { trace: "retain-on-failure" },
  projects: [
    {
      name: "storefront",
      testDir: "./e2e",
      use: { ...devices["Desktop Chrome"], baseURL: STOREFRONT },
    },
    {
      name: "admin",
      testDir: "./e2e-admin",
      use: { ...devices["Desktop Chrome"], baseURL: ADMIN },
    },
  ],
});
