import { test, expect } from "@playwright/test";
import { loginAsAdmin } from "./helpers";
import { capture } from "../e2e-support/screenshots";

const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8080";
const TENANT_ID = "00000000-0000-0000-0000-000000000001";

// storefront_duplicated_from: duplicating a storefront through Commerce ops records the source on the copy.
// Manage on the copy shows "Duplicated from: <source>" (a link-styled button that opens the source); the source itself — created
// directly — shows no such line; the admin API returns the same read-only id. Seeds its own source storefront via
// the gateway and archives both at the end so the demo stores are untouched.
test("commerce ops: a duplicated storefront shows and links the storefront it was copied from", async ({ page, request }) => {
  const login = await request.post(`${GATEWAY}/api/identity/login`, {
    data: { email: "admin@3commerce.local", password: "dev-admin-password-1" },
  });
  expect(login.ok()).toBeTruthy();

  const sourceName = `DupFrom E2E ${Date.now()}`;
  const copyName = `${sourceName} (copy)`;
  const created = await request.post(`${GATEWAY}/api/catalog/admin/storefronts`, {
    data: { tenantId: TENANT_ID, name: sourceName, visibility: 1, currency: "EUR" },
  });
  expect(created.ok()).toBeTruthy();
  const sourceId = ((await created.json()) as { id: string }).id;
  let copyId: string | undefined;

  try {
    await loginAsAdmin(page);
    await page.goto("/commerce-ops");
    await page.getByRole("button", { name: /load storefronts/i }).click();

    // Duplicate through the UI: the prompt defaults the name to "<source> (copy)".
    const sourceRow = page.locator("tr", { hasText: sourceName }).filter({ hasNotText: copyName });
    await expect(sourceRow).toBeVisible({ timeout: 10_000 });
    await sourceRow.getByRole("button", { name: "Duplicate", exact: true }).click();
    await expect(page.getByLabel("New storefront name")).toHaveValue(copyName);
    await page.getByRole("button", { name: "Create duplicate", exact: true }).click();

    const copyRow = page.locator("tr", { hasText: copyName });
    await expect(copyRow).toBeVisible({ timeout: 10_000 });

    // The API carries the link, read-only, on the copy — and none on the source.
    const list = await request.get(`${GATEWAY}/api/catalog/admin/storefronts?tenantId=${TENANT_ID}`);
    const stores = (await list.json()) as Array<{ id: string; name: string; duplicatedFromStorefrontId: string | null }>;
    const copy = stores.find((s) => s.name === copyName);
    copyId = copy?.id;
    expect(copy?.duplicatedFromStorefrontId).toBe(sourceId);
    expect(stores.find((s) => s.id === sourceId)?.duplicatedFromStorefrontId ?? null).toBeNull();

    // Manage the copy: "Duplicated from: <source name>".
    await copyRow.getByRole("button", { name: "Manage", exact: true }).click();
    await expect(page.getByRole("heading", { name: `Manage ${copyName}` })).toBeVisible({ timeout: 10_000 });
    const lineage = page.getByTestId("duplicated-from");
    await expect(lineage).toContainText("Duplicated from:");
    await expect(lineage.getByRole("button", { name: sourceName, exact: true })).toBeVisible();
    await capture(page, "duplicated-from-copy");

    // The link opens the source, which was not duplicated from anything.
    await lineage.getByRole("button", { name: sourceName, exact: true }).click();
    await expect(page.getByRole("heading", { name: `Manage ${sourceName}`, exact: true })).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId("duplicated-from")).toHaveCount(0);
  } finally {
    for (const id of [copyId, sourceId]) {
      if (id) {
        await request.post(`${GATEWAY}/api/catalog/admin/storefronts/${id}/archive`);
      }
    }
  }
});
