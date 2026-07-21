import { test, expect, request as pwRequest } from "@playwright/test";
import { driveCheckout, reachable } from "./portal-helpers";

/**
 * LGTM observability portal verification: the always-on local stack (compose `observability`
 * profile) must be up AND ingesting REAL service telemetry, not just answering health checks.
 *  - Grafana (:3001): /api/health ok, and the provisioned prometheus/loki/tempo/mimir datasources exist.
 *  - The provisioned dashboards exist AND their panel queries return data — a dashboard that loads
 *    but shows "No data" is the failure mode this suite exists to catch (a dangling `${DS_PROMETHEUS}`
 *    datasource uid emptied every RED panel while every health check stayed green).
 *  - Loki (:3100) / Tempo (:3200) / Mimir (:9009): /ready.
 *  - After a real guest checkout through the gateway, Loki holds log lines labelled with the
 *    bare-run services' service_name and Tempo search finds traces rooted in those services —
 *    proof the OTLP pipeline (service → collector → backend) carries the flow just driven.
 * Each test skips cleanly when its portal (or the stack) isn't running — CI-safe.
 */
const GRAFANA = "http://localhost:3001";
const LOKI = "http://localhost:3100";
const TEMPO = "http://localhost:3200";
const MIMIR = "http://localhost:9009";

const GRAFANA_AUTH = { authorization: `Basic ${Buffer.from("admin:admin").toString("base64")}` };

// Every dashboard provisioned under deploy/observability/grafana/provisioning/dashboards.
const DASHBOARD_UIDS = ["3commerce-services-red", "3commerce-logs-overview", "3commerce-traces-overview"];

// Panels whose emptiness is a healthy stack, not a broken dashboard: they only plot failures.
// For these the query must still EXECUTE (datasource wired, expression parses) — that is what the
// `${DS_PROMETHEUS}` regression broke — but zero series is an acceptable result.
const FAILURE_ONLY_PANEL = /error|failed|5xx/i;

type PanelTarget = { refId: string; expr?: string; query?: string; limit?: number; queryType?: string };
type Panel = { id: number; title: string; datasource: { type: string; uid: string }; targets?: PanelTarget[] };

// The bare-run manifest names (scripts/lib/services.sh == AddServiceTelemetry service names).
const REAL_SERVICES = /^(gateway|identity|catalog|ordering|payments|fulfillment|support|entity|marketing|pricing|audit|workflow|entitlement|usage|notifications)$/;

test.describe("Observability portals ingest real service telemetry", () => {
  test("Grafana is healthy with the LGTM datasources provisioned", async () => {
    test.skip(!(await reachable(`${GRAFANA}/api/health`)), "Grafana not running");

    const ctx = await pwRequest.newContext();
    try {
      const health = await ctx.get(`${GRAFANA}/api/health`);
      expect(health.ok()).toBe(true);
      expect(((await health.json()) as { database: string }).database).toBe("ok");

      // Dev creds (admin/admin) — the provisioned datasource uids Mission Control deep-links to.
      const ds = await ctx.get(`${GRAFANA}/api/datasources`, {
        headers: { authorization: `Basic ${Buffer.from("admin:admin").toString("base64")}` },
      });
      expect(ds.ok()).toBe(true);
      const uids = ((await ds.json()) as Array<{ uid: string }>).map((d) => d.uid);
      for (const uid of ["prometheus", "loki", "tempo", "mimir"]) expect(uids).toContain(uid);
    } finally {
      await ctx.dispose();
    }
  });

  test("Loki, Tempo and Mimir all report ready", async () => {
    // One reachable backend is proof the profile is up — then ALL three must be ready.
    const up = await Promise.all([reachable(`${LOKI}/ready`), reachable(`${TEMPO}/ready`), reachable(`${MIMIR}/ready`)]);
    test.skip(!up.some(Boolean), "LGTM stack not running");

    const ctx = await pwRequest.newContext();
    try {
      for (const url of [`${LOKI}/ready`, `${TEMPO}/ready`, `${MIMIR}/ready`]) {
        const res = await ctx.get(url);
        expect(res.status(), `${url} should be ready`).toBe(200);
      }
    } finally {
      await ctx.dispose();
    }
  });

  test("a real checkout lands real-service logs in Loki and traces in Tempo", async () => {
    test.skip(!(await reachable(`${LOKI}/ready`)) || !(await reachable(`${TEMPO}/ready`)), "LGTM stack not running");
    const flowed = await driveCheckout();
    test.skip(!flowed, "stack/seed not running — no gateway checkout possible");

    const ctx = await pwRequest.newContext();
    try {
      // Loki: services export logs over OTLP with service.name promoted to the service_name
      // stream label. Poll — the OTel batch exporter + collector flush add a few seconds of lag.
      // query_range with EXPLICIT client-side bounds, not an instant query: instant queries
      // evaluate ranges against the server's clock, and the Docker Desktop VM clock can drift
      // minutes from the host after a sleep/reboot — enough to empty a relative window.
      const lokiQuery = `sum by (service_name) (count_over_time({service_name=~".+"}[15m]))`;
      await expect
        .poll(
          async () => {
            const end = BigInt(Date.now()) * 1_000_000n;
            const start = end - 900n * 1_000_000_000n;
            const res = await ctx.get(
              `${LOKI}/loki/api/v1/query_range?query=${encodeURIComponent(lokiQuery)}&start=${start}&end=${end}&step=900s`,
            );
            if (!res.ok()) return [];
            const body = (await res.json()) as { data?: { result?: Array<{ metric: { service_name?: string } }> } };
            return (body.data?.result ?? []).map((r) => r.metric.service_name ?? "").filter((n) => REAL_SERVICES.test(n));
          },
          { message: "Loki should hold log lines from real bare-run services", timeout: 60_000 },
        )
        .not.toHaveLength(0);

      // Tempo: search the last 15 minutes for traces rooted in a real service.
      await expect
        .poll(
          async () => {
            const end = Math.floor(Date.now() / 1000);
            const res = await ctx.get(`${TEMPO}/api/search?limit=50&start=${end - 900}&end=${end}`);
            if (!res.ok()) return [];
            const body = (await res.json()) as { traces?: Array<{ rootServiceName?: string }> };
            return (body.traces ?? []).map((t) => t.rootServiceName ?? "").filter((n) => REAL_SERVICES.test(n));
          },
          { message: "Tempo search should find traces from real bare-run services", timeout: 60_000 },
        )
        .not.toHaveLength(0);
    } finally {
      await ctx.dispose();
    }
  });

  test("every provisioned dashboard panel returns data", async () => {
    test.skip(!(await reachable(`${GRAFANA}/api/health`)), "Grafana not running");
    const flowed = await driveCheckout();
    test.skip(!flowed, "stack/seed not running — no gateway checkout possible");

    const ctx = await pwRequest.newContext({ extraHTTPHeaders: GRAFANA_AUTH });
    try {
      for (const uid of DASHBOARD_UIDS) {
        const res = await ctx.get(`${GRAFANA}/api/dashboards/uid/${uid}`);
        expect(res.ok(), `dashboard ${uid} should be provisioned`).toBe(true);
        const dash = ((await res.json()) as { dashboard: { panels: Panel[] } }).dashboard;
        expect(dash.panels.length, `${uid} should have panels`).toBeGreaterThan(0);

        for (const panel of dash.panels) {
          for (const target of panel.targets ?? []) {
            const label = `${uid} / ${panel.title}`;
            // `$service` etc. are dashboard variables Grafana interpolates in the browser; the
            // API takes the raw expression, so widen them to "match anything" here.
            const expr = (target.expr ?? target.query ?? "").replace(/\$\w+/g, ".+");
            const mustHaveData = !FAILURE_ONLY_PANEL.test(panel.title);

            await expect
              .poll(async () => queryPanel(ctx, panel, target, expr), {
                message: `${label} should return data`,
                timeout: 60_000,
              })
              .toBeGreaterThanOrEqual(mustHaveData ? 1 : 0);
          }
        }
      }
    } finally {
      await ctx.dispose();
    }
  });
});

/**
 * Run one panel target the way Grafana itself would and return the row count (-1 on a failed
 * query, so a broken datasource/expression can never satisfy even the "may be empty" panels).
 * Time bounds are always explicit and CLIENT-side: the Docker Desktop VM clock drifts from the
 * host after sleep/reboot, and a relative window evaluated against the server clock comes back
 * silently empty.
 */
async function queryPanel(
  ctx: Awaited<ReturnType<typeof pwRequest.newContext>>,
  panel: Panel,
  target: PanelTarget,
  expr: string,
): Promise<number> {
  const nowMs = Date.now();
  const fromMs = nowMs - 30 * 60 * 1000;

  // Tempo's TraceQL search runs in the Grafana FRONTEND — the backend plugin rejects the
  // `traceql` query type — so exercise these panels through the datasource proxy to Tempo's
  // search API, which is the same request the browser ends up making.
  if (panel.datasource.type === "tempo") {
    const params = new URLSearchParams({
      q: expr,
      limit: String(target.limit ?? 20),
      start: String(Math.floor(fromMs / 1000)),
      end: String(Math.floor(nowMs / 1000)),
    });
    const res = await ctx.get(`${GRAFANA}/api/datasources/proxy/uid/${panel.datasource.uid}/api/search?${params}`);
    if (!res.ok()) return -1;
    return ((await res.json()) as { traces?: unknown[] }).traces?.length ?? 0;
  }

  const query: Record<string, unknown> = {
    ...target,
    expr,
    datasource: panel.datasource,
    intervalMs: 60_000,
    maxDataPoints: 200,
    ...(panel.datasource.type === "prometheus" ? { range: true, instant: false } : { queryType: "range" }),
  };
  const res = await ctx.post(`${GRAFANA}/api/ds/query`, {
    data: { from: String(fromMs), to: String(nowMs), queries: [query] },
  });
  if (!res.ok()) return -1;
  const body = (await res.json()) as {
    results?: Record<string, { error?: string; frames?: Array<{ data?: { values?: unknown[][] } }> }>;
  };
  const result = body.results?.[target.refId];
  if (!result || result.error) return -1;
  return (result.frames ?? []).reduce((rows, f) => rows + (f.data?.values?.[0]?.length ?? 0), 0);
}
