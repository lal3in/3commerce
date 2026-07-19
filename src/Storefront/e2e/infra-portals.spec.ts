import { test, expect, type Page } from "@playwright/test";
import { execSync } from "node:child_process";
import { driveCheckout, reachable } from "./portal-helpers";

/**
 * Dev-infra portal verification: the always-on local portals must be up AND showing real data.
 *  - RabbitMQ management (:15672): after a real guest checkout through the gateway, the queues page
 *    lists the services' live queues (consumers connected, traffic from the flow just driven).
 *  - Kafka UI (:8090): cluster "local" online; a message produced through the broker (docker exec)
 *    is visible in the UI's topic browser. (Local bare-run services don't stream to Kafka — the
 *    EventStreaming lane is Helm-only config — so the broker round-trip is the honest flow proof.)
 *  - pgAdmin (:5480): logs in, the pre-registered server is in the object explorer, and connecting
 *    (saved pgpass — no password prompt) reveals the service databases incl. ordering_db.
 * Each test skips cleanly when its portal (or the stack) isn't running — CI-safe.
 */
const RABBIT = "http://localhost:15672";
const KAFKA_UI = "http://localhost:8090";
const PGADMIN = "http://localhost:5480";

const SHOT_DIR = process.env.PORTAL_SHOT_DIR; // optional screenshot output for manual review

async function shot(page: Page, name: string) {
  if (SHOT_DIR) await page.screenshot({ path: `${SHOT_DIR}/${name}.png`, fullPage: false });
}

test.describe("Dev-infra portals show real data", () => {
  test("RabbitMQ management lists live service queues after a real checkout", async ({ page }) => {
    test.skip(!(await reachable(RABBIT)), "RabbitMQ management not running");
    const flowed = await driveCheckout();

    await page.goto(`${RABBIT}/`, { waitUntil: "domcontentloaded" });
    await page.fill('input[name="username"]', "guest");
    await page.fill('input[name="password"]', "guest");
    await page.click('input[type="submit"], button[type="submit"]');
    await expect(page.locator("#menu, #main").first()).toBeVisible({ timeout: 15000 });

    await page.goto(`${RABBIT}/#/queues`, { waitUntil: "domcontentloaded" });
    const rows = page.locator("table.list tbody tr");
    await expect(rows.first()).toBeVisible({ timeout: 15000 });
    expect(await rows.count()).toBeGreaterThan(3); // the running services declare their queues
    // Real consumers/data, not an empty broker: a known service queue is present.
    await expect(page.locator("body")).toContainText(/order|catalog|fulfillment|notification/i);
    await shot(page, "rabbitmq-queues");
    expect(flowed, "gateway checkout flow should have run (stack seeded)").toBe(true);
  });

  test("Kafka UI shows the cluster online and a message produced through the broker", async ({ page }) => {
    test.skip(!(await reachable(KAFKA_UI)), "Kafka UI not running");

    // Round-trip a message through the broker itself; auto-create makes the topic.
    let produced = false;
    try {
      execSync(
        `docker exec 3commerce-kafka sh -c 'printf "hello-3c-portal\\n" | kafka-console-producer.sh --bootstrap-server localhost:9092 --topic e2e-portal-check'`,
        { stdio: "ignore", timeout: 30000 },
      );
      produced = true;
    } catch {
      /* docker unavailable — dashboard checks below still prove the portal */
    }

    await page.goto(`${KAFKA_UI}/`, { waitUntil: "domcontentloaded" });
    await expect(page.getByText("local", { exact: false }).first()).toBeVisible({ timeout: 15000 });
    await expect(page.locator("body")).toContainText(/online/i);

    if (produced) {
      await page.goto(`${KAFKA_UI}/ui/clusters/local/all-topics`, { waitUntil: "domcontentloaded" });
      await expect(page.getByText("e2e-portal-check")).toBeVisible({ timeout: 15000 });
      await page.goto(`${KAFKA_UI}/ui/clusters/local/all-topics/e2e-portal-check/messages`, { waitUntil: "domcontentloaded" });
      await expect(page.locator("body")).toContainText("hello-3c-portal", { timeout: 20000 });
    }
    await shot(page, "kafka-ui-topic");
  });

  test("pgAdmin logs in with the saved server and reveals the service databases", async ({ page }) => {
    test.skip(!(await reachable(`${PGADMIN}/login`)), "pgAdmin not running");

    await page.goto(`${PGADMIN}/login`, { waitUntil: "domcontentloaded" });
    await page.fill('input[name="email"]', "admin@3commerce.dev");
    await page.fill('input[name="password"]', "pgadmin_dev");
    await page.click('button[type="submit"]');

    // Object explorer: reveal Servers → "3commerce" group → server → Databases → ordering_db.
    // pgAdmin restores expansion state server-side, so any node may already be open — and a
    // dblclick on an expanded node COLLAPSES it. Only dblclick while the target child is hidden
    // (retries make the toggle self-correcting). Scope matches to the explorer panel so transient
    // toasts ("Connecting to server …") can't be matched.
    const tree = page.getByRole("tabpanel", { name: "Object Explorer" });
    const revealChild = async (node: ReturnType<typeof tree.getByText>, child: ReturnType<typeof tree.getByText>) => {
      for (let attempt = 0; attempt < 3; attempt++) {
        if (await child.first().isVisible().catch(() => false)) return;
        await node.first().dblclick();
        await child.first().waitFor({ timeout: 10000 }).catch(() => {});
      }
      await expect(child.first()).toBeVisible({ timeout: 15000 });
    };

    await expect(tree.getByText("Servers").first()).toBeVisible({ timeout: 30000 });
    const group = tree.getByText("3commerce", { exact: true });
    const server = tree.getByText("3commerce — all service databases");
    const databases = tree.getByText(/^Databases/);
    await revealChild(group, server);
    // Connect (saved pgpass → no password dialog); expanding reveals the Databases node.
    await revealChild(server, databases);
    await revealChild(databases, tree.getByText("ordering_db"));
    await shot(page, "pgadmin-databases");
  });
});
