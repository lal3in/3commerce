import path from "node:path";
import { defineConfig, devices } from "@playwright/test";

// Drives the storefront AND the Blazor admin console in a real browser against a running
// stack (storefront :3000, admin :5200, gateway :8080). Bring the stack up first, then:
//   npm run test:e2e                          (both)
//   npm run test:e2e -- --project=storefront  (just the storefront)
const STOREFRONT = process.env.STOREFRONT_URL ?? "http://localhost:3000";
const ADMIN = process.env.ADMIN_URL ?? "http://localhost:5200";
const SUPPLIER = process.env.SUPPLIER_URL ?? "http://localhost:5300";

// Screenshot history (e2e-support/screenshots.ts + screenshot-reporter.ts). The config is evaluated in the
// main process BEFORE the workers are forked, and the workers inherit its environment — so `??=` gives every
// worker of one invocation the same run id, and a caller can pin one explicitly (e2e-verify.sh does).
// eslint-disable-next-line @typescript-eslint/no-require-imports
const shotsLib = require(path.resolve(__dirname, "..", "..", "scripts", "screenshots", "lib.cjs"));
process.env.SCREENSHOT_ARCHIVE ??= shotsLib.archiveRoot();
process.env.SCREENSHOT_RUN_ID ??= shotsLib.newRunId();

export default defineConfig({
  timeout: 60_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1,
  // Retry on CI to absorb flaky Blazor-circuit timing in the admin/supplier E2E
  // projects (e.g. supplier readiness/stock render); locally keep 0 for fast feedback.
  retries: process.env.CI ? 2 : 0,
  // The screenshot reporter never fails a run; it archives every capture() and rebuilds the index.
  reporter: [["list"], ["./e2e-support/screenshot-reporter.ts"]],
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
    {
      name: "supplier",
      testDir: "./e2e-supplier",
      use: { ...devices["Desktop Chrome"], baseURL: SUPPLIER },
    },
  ],
});
