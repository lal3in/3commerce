import { defineConfig, devices } from "@playwright/test";

// Drives the storefront in a real browser against a running stack
// (storefront :3000 → gateway :8080). Bring the stack up first
// (scripts/run-all.sh + npm run start), then: npm run test:e2e
export default defineConfig({
  testDir: "./e2e",
  timeout: 40_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [["list"]],
  use: {
    baseURL: process.env.STOREFRONT_URL ?? "http://localhost:3000",
    trace: "retain-on-failure",
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
});
