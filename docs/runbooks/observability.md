# Observability runbook — collector, LGTM backends, alerts (ADR-0065)

What runs where, how to check it, and what to do when an alert email arrives. Decisions: ADR-0065 (Q1–Q8),
ADR-0029 (SLOs), ADR-0064 (release identity). Messaging-specific operations: `messaging-observability.md`.

## Map

| Piece | Dev (compose `observability` profile, started by `dev-up.sh`) | Production (Helm) |
|---|---|---|
| Collector | `3commerce-otel-collector` · OTLP `:4317`/`:4318` · Faro `:8027` · health `:13133` · config `deploy/observability/otel-collector-config.yaml` | `otel-collector` Deployment + ClusterIP Service (same ports) · `deploy/helm/3commerce/templates/observability.yaml` |
| Logs | Loki 3.3.2 `:3100`, filesystem | `loki` release (grafana-community/loki 21.3.1), S3, gateway `loki-gateway.observability.svc` |
| Traces | Tempo 2.6.1 `:3200`, local disk, 48 h | `tempo` release (grafana-community/tempo 3.2.1), S3, 14 d |
| Metrics | Mimir 2.14.1 `:9009` (+ Prometheus scrape view) | `mimir` release (grafana/mimir-distributed 6.2.2), S3, 13 months, gateway `mimir-gateway.observability.svc` |
| Grafana | `:3001` (admin / `GRAFANA_ADMIN_PASSWORD`) | `grafana` release (grafana-community/grafana 14.5.0), `kubectl -n observability port-forward svc/grafana 3001:80` |
| Alert rules | `deploy/observability/grafana/provisioning/alerting/` | same files, ConfigMap `grafana-alerting` |
| Error inbox (ADR-0067) | GlitchTip 7.0 `:8000` (infra `portals` set; sign-in in `deploy/observability/glitchtip/glitchtip.env`), shared Postgres db `glitchtip` | `glitchtip` release (glitchtip 9.2.0), managed Postgres, `kubectl -n observability port-forward svc/glitchtip 8000:80` |
| Profiles (ADR-0068) | Pyroscope 2.4.4 `:4040` (borrowed observability service, filesystem, 7 d); .NET profiles only from Linux containers | `pyroscope` release (grafana/pyroscope 2.4.4), S3, 14 d, tenant `3commerce`; app chart `profiling.enabled` (values-prod) |

Tenant: production backends are multi-tenant; everything is written and read with `X-Scope-OrgID: 3commerce`.

## Health checks

```bash
scripts/doctor.sh                 # local: collector, Faro receiver, Grafana, alert rules (expect 8), GlitchTip + issues, Pyroscope + profiled services
scripts/host-check.sh local       # + Loki/Tempo/Mimir /ready, Prometheus, alert rule count
curl -s localhost:13133/          # collector: {"status":"Server available", …}
curl -s -o /dev/null -w '%{http_code}\n' localhost:8027/   # Faro receiver: 405 (GET) = listening
curl -s -u admin:admin localhost:3001/api/v1/provisioning/alert-rules | grep -o '"uid":"tc-[^"]*"'
```

Production: `kubectl -n <app-ns> get pods -l app=otel-collector`, `kubectl -n observability get pods`.

## Installing / upgrading production

1. Buckets: `3commerce-loki-{chunks,ruler,admin}`, `3commerce-tempo-traces`, `3commerce-mimir-{blocks,ruler,alertmanager}`.
   No bucket lifecycle rule shorter than the retention — the backends expire data themselves.
2. Secrets in `observability`: `lgtm-object-storage` (`AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`; skip with
   workload identity), `grafana-admin` (`admin-user`, `admin-password`), `grafana-alerting` (`ALERT_EMAIL_TO`,
   `GF_SMTP_HOST`, `GF_SMTP_USER`, `GF_SMTP_PASSWORD`, `GF_SMTP_FROM_ADDRESS`).
3. Review: `deploy/helm/observability/install-lgtm.sh --template` (renders to `.run/lgtm-rendered/`).
4. Install: `S3_ENDPOINT=… S3_REGION=… deploy/helm/observability/install-lgtm.sh`.
5. App: `helm upgrade --install 3commerce deploy/helm/3commerce -f deploy/helm/3commerce/values-prod.yaml`
   (`observability.enabled=true`, `backend=external`, tail sampling on). Set
   `observability.collector.clusterName` and add a cloud detector to `resourceDetectors` per cluster.
6. Verify: Grafana → Alerting lists 8 rules in folder *3commerce alerts*; Explore → Loki
   `{service_name="identity"}` returns logs; `{log_category="security"}` returns auth logs once any are written.

Changing the alert recipient: edit `ALERT_EMAIL_TO` in `deploy/observability/alerting.env` (one file for dev and prod;
placeholder `test@test.com`), then restart Grafana in dev or re-run `install-lgtm.sh` in prod — provisioning re-reads
it at start. Test it from Grafana → Alerting → Contact points → *ops-email* → Test.

## Changing the collector config

Edit the dev file and the Helm template **together** (shared processors are verbatim copies), then validate with
the pinned binary (`otelcol-contrib` 0.165.0 from the release page):

```bash
otelcol-contrib validate --config=deploy/observability/otel-collector-config.yaml
helm template t deploy/helm/3commerce -f deploy/helm/3commerce/values-prod.yaml -s templates/observability.yaml \
  | awk '/^  config.yaml: \|/{f=1;next} f&&/^[^ ]/{f=0} f' | sed 's/^    //' > /tmp/c.yaml && otelcol-contrib validate --config=/tmp/c.yaml
```

A clean start logs no `deprecated` warning. Upgrading the collector: re-check
`gh api repos/open-telemetry/opentelemetry-collector-releases/releases/latest`, read the release notes for
removed aliases/keys, bump `docker-compose.yml` and `values.yaml` together.

**Scaling the collector:** tail sampling requires every span of a trace on one instance — keep `replicas: 1`, or put
a `loadbalancing` exporter tier (routing by trace id) in front of N sampling collectors.

## Alerts

Every rule links here (`runbook_url`). Severity `page` repeats hourly, `ticket` every 12 h.

### HTTP 5xx rate above 1 percent
SLO (ADR-0029): < 1 % per service over 5 min. Open *Services (RED)* for the service, then Explore → Tempo with
`{ resource.service.name = "<svc>" && status = error }` to read a failing trace, and its logs via *Logs for this
span*. Common causes: a dependency down (Postgres/RabbitMQ/Valkey — check the platform alerts), a bad release
(the purple *Deploy* marker on the graph — ADR-0069 — or the RED *Running releases* table shows which
`service_version` started failing → roll back). The *SLOs* dashboard shows how much of the month's error budget it cost.

### p95 latency
Catalog reads > 500 ms, other reads > 800 ms for 10 min. Find the route in the alert label, open a slow trace in
Tempo (latency > threshold — prod keeps every one of them via tail sampling), look for the long span (DB query,
downstream HTTP, lock wait). Check DB load and the PgBouncer pool (ADR-0032) if many routes are slow at once.
A *Deploy* marker just before the rise points at the release; the *SLOs* dashboard's headroom bar shows which
services are near their target.

### Messages in an _error queue
A message failed every retry and was moved to `<queue>_error` (MassTransit). It will not be retried by itself.
Read it in the RabbitMQ UI (`:15672` → Queues → `<queue>_error` → Get messages: the `MT-Fault-*` headers carry the
exception), fix the cause, then move it back (shovel to the source queue) — the consumer is idempotent
(`messaging-observability.md`). No data for this rule means the RabbitMQ per-queue metrics are not scraped yet.

### Error log spike
More than 20 error/critical log records from one service in 5 min. Logs overview dashboard → filter the service;
each record carries `trace_id` → jump to the trace.

### Valkey down
`redis_up < 1` (exporter view) or `redis_unavailable_total` rising (services' view). Sessions fall back to Identity,
rate limiting fails open/closed per partition (ADR-0044). Check the Valkey container/pod, memory and evictions on
the *Redis overview* dashboard.

### OTel collector down
No collector heartbeat for 5 min: **no telemetry is being stored**. Dev: `docker logs 3commerce-otel-collector`
(a config error stops it at boot — validate as above). Prod: `kubectl logs deploy/otel-collector`; an OOM restart
means raise `observability.collector.resources.limits.memory` (memory_limiter keeps 80 % of it). If the collector
is up but this fires, Mimir is not accepting writes (check the `mimir-distributor` and Kafka pods).

## Logs missing or late in Loki (429 / 400 from the collector)

Loki says why it refused data; read that before raising anything:

```bash
curl -s localhost:3100/metrics | grep loki_discarded        # dev; prod: the loki pod's :3100/metrics
docker logs 3commerce-otel-collector 2>&1 | grep -E 'Exporting failed|Dropping data' | tail
```

| Reason (`loki_discarded_*{reason=…}`) | Collector sees | Effect | Knob |
|---|---|---|---|
| `rate_limited` | 429, `Will retry` | Delayed, not lost (retries for up to 5 min) | `ingestion_rate_mb` / `ingestion_burst_size_mb` (per tenant), `per_stream_rate_limit[_burst]` |
| `line_too_long` | 400, `Dropping data` | The WHOLE push is lost | `max_line_size_truncate: true` (set) |
| `structured_metadata_too_large` | 400, `Dropping data` | The WHOLE push is lost | collector `transform/truncate_attributes` caps each attribute at 8 KB (set) |

Sizes (2026-11-19): dev and the in-chart kind Loki 16 MB/s, burst 32 MB, 8 MB/s per stream (burst 32 MB);
production 32 / 64 / 16 / 64 MB — production writes every service as one tenant (`3commerce`). At Loki's defaults
(4 / 6 / 3 / 15 MB) a seeded dev-up's catalog import was refused ~400k lines' worth of 429s. Don't raise the dev
values much further: 64 MB/s once got Loki OOM-killed in the 6 GB Colima VM.

## Security logs (12-month stream)

The collector marks authentication / permission-denied logs (`log.category=security`, by ILogger category — see
ADR-0065 §3). Query: `{log_category="security"}`. Retention is the Loki `retention_stream` (8760h); deleting a
specific record early (a GDPR erasure request) is a Loki delete request:
`curl -X POST -H 'X-Scope-OrgID: 3commerce' 'http://loki-gateway.observability.svc/loki/api/v1/delete?query={log_category="security"} |= "<subject>"&start=<unix>'`.

## Browser telemetry (Faro)

The storefront relays Faro payloads from its `/rum` route to `http://otel-collector:8027/` (bare-run
`http://localhost:8027/`). The collector strips identifiers, IPs, user agents and query strings before storage
(ADR-0065 Q3). Browser data appears under `service_name="storefront"` (or the Faro app name) in Loki/Tempo; a
browser span shares the trace of the gateway request it made.

## Error inbox (GlitchTip)

ADR-0067. Every .NET process (project `backend`) and the storefront (project `storefront`) report errors to GlitchTip,
each with its release (`service.version`) and its OpenTelemetry trace id (tag `trace_id`). Alerts (rates) stay in
Grafana; GlitchTip is where an error becomes an issue to assign, resolve and watch for regressions.

**Triage an issue.** Open it, read the tags: `release` (which build — a regression flag means it came back in a newer
one), `service`, `http.route`, `trace_id`. Paste the trace id into Grafana → Explore → Tempo for the full request, and
into Loki (`{service_name="<service>"} | trace_id="<id>"`) for its log lines. A storefront issue's `support_ref` is
the reference the error page showed the shopper (digest or trace id): search by it when a customer quotes one.

**Nothing arrives.**
1. `scripts/doctor.sh` — `glitchtip :8000 200` and the issue counts. Down → `docker logs 3commerce-glitchtip`
   (the entrypoint prints `glitchtip-entrypoint:` / `glitchtip-bootstrap:` lines; a failed bootstrap stops the start).
2. Is the process configured? Bare-run services get `SENTRY_DSN` from `scripts/run-all.sh` (`SENTRY_DSN=` opts out);
   the storefront from `dev-up.sh` / `e2e-verify.sh` (`STOREFRONT_SENTRY_DSN` overrides). In Kubernetes, the Secret
   named by `errorTracking.dsnSecret` (keys `backend`, `storefront`) — it is optional, so a missing Secret silently
   means "off": `kubectl get secret glitchtip-dsn`.
3. Prove the path: `curl -s localhost:5102/health/error-probe` (any service, not in Production) answers 500 with a
   `traceId`; within seconds the backend project has an `ErrorProbeException` issue tagged with that trace id.
4. 4xx answers never appear on purpose (`ClientErrorExceptionFilter`): validation and domain failures are the
   caller's, not defects.

**Dev database.** GlitchTip uses the shared dev Postgres, database + role `glitchtip`, created by its entrypoint when
missing (fresh or existing volume). `dev-down.sh --clean` / `dev-up --fresh` wipe it with the rest; the bootstrap
recreates the projects with the same DSNs. Retention 14 days.

**Production install** (once, after `install-lgtm.sh` has installed the `glitchtip` release):
1. Secrets in `observability` first: `glitchtip-secret-key` (`SECRET_KEY`), `glitchtip-database` (`DATABASE_URL` of a
   managed Postgres ≥ 15 with `max_locks_per_transaction = 512`), optional `glitchtip-email` (`EMAIL_URL`); set
   `GLITCHTIP_DOMAIN` for the install.
2. Port-forward (`svc/glitchtip 8000:80`), sign up — the first account becomes the owner; registration is closed
   after it — create organisation `3commerce` and projects `backend` (.NET) and `storefront` (JavaScript); turn on
   "scrub IP addresses" (the default) and set the alert email under the project's alerts.
3. Hand the DSNs to the app (in-cluster host is fine — every sender is server-side):
   `kubectl -n <app-ns> create secret generic glitchtip-dsn --from-literal=backend=http://<key>@glitchtip.observability.svc/<id> --from-literal=storefront=…`
   then roll the app (`helm upgrade … -f values-prod.yaml`; `errorTracking.dsnSecret: glitchtip-dsn` is already set).

## Profiling (Pyroscope)

ADR-0068. Every .NET image carries the Pyroscope .NET profiler (a native CLR profiler, ~16 MB, checksum-pinned), inert
until the deployment turns it on: Helm `profiling.enabled` (on in `values-prod.yaml`) sets `CORECLR_ENABLE_PROFILING=1`,
`LD_PRELOAD=/pyroscope/Pyroscope.Linux.ApiWrapper.x64.so`, `PYROSCOPE_PROFILING_ENABLED=true`,
`PYROSCOPE_SERVER_ADDRESS`, `PYROSCOPE_TENANT_ID` and the profile types (CPU, allocation, lock, exception; wall time off).

**The profiler is Linux-only.** Bare-run services on macOS (the default dev mode) are never profiled; the dev Pyroscope
then holds only its own profiles (`service_name=pyroscope`). To profile a .NET service locally, run its image (Linux
container) with the env above and `PYROSCOPE_SERVER_ADDRESS=http://pyroscope:4040` on the `3commerce-data` network, or
turn it on for the containerized launch in `deploy/.env.dev`.

**Read a profile.** Grafana → Explore → Pyroscope → `service_name` = the OTel service name, profile type
`process_cpu` (or `alloc_*`, `lock_*`, `exceptions`). From a slow span in Tempo, "Profiles for this span" opens the CPU
samples of exactly that request (span profiles: `pyroscope.profile.id` on root spans).

**Nothing arrives.**
1. `scripts/doctor.sh` — `pyroscope :4040 200` and the list of profiled services.
2. The pod has the env (`kubectl exec … -- env | grep -E 'CORECLR|PYROSCOPE'`)? The profiler logs to
   `/tmp/pyroscope/` inside the container (`PYROSCOPE_PROFILING_LOG_DIR`).
3. Multi-tenant production: the pods need `PYROSCOPE_TENANT_ID=3commerce` (Helm `profiling.tenant`) and Grafana the
   same `X-Scope-OrgID`, or the profiles land in, or are read from, the wrong tenant.

**Overhead.** A few percent CPU for the default types. Turn a type off per environment (`profiling.types.*`), or the
whole profiler (`profiling.enabled=false`) — the image is unchanged either way.

**Bump the profiler.** One version and two SHA-256 sums (x86_64, aarch64) in the `profiler` stage, byte-identical in
all 17 .NET Dockerfiles (`ProfilingTests` fails otherwise); the NuGet `Pyroscope` version follows it.
## SLOs and deploy markers

ADR-0069 (SLO targets: ADR-0029). Dashboard **3commerce — SLOs** (`3commerce-slo`, same file in dev and production):

- **Availability, 30 days** per service = share of HTTP requests that are not 5xx; objective 99.5 %, so the **error
  budget** is 0.5 % of requests. *Error budget remaining* below 0 = the month's objective is already missed.
- **Burn rate** (1 h / 6 h) = 5xx ratio ÷ 0.5 %. 1 spends the budget in exactly 30 days. The 5-minute *HTTP 5xx rate above
  1 %* alert is burn rate 2; a 6-hour burn above 6 or a 1-hour burn above 14.4 empties the budget in days or hours —
  treat it like a page even if the 5-minute alert has not fired yet.
- **Latency**: the p95 panels are the latency alerts' own expressions (catalog < 500 ms, other reads < 800 ms); the
  headroom bar is worst route p95 ÷ target (> 100 % = missed now).

**Deploy markers.** Every Mimir dashboard (RED, SLOs, .NET runtime, Database, Messaging) shows a purple region
"Deploy: <service> <service.version>" for the first 15 minutes after a new version starts reporting, with the
environment and commit in its text. It is computed from the metrics (no one posts it), so it covers every way of
deploying — Helm, a rollback, a hotfix, a dev restart. A marker for an unchanged version means the service was silent
for more than 15 minutes and came back (a restart). No marker after a deploy: the new pods never reported — check
`kubectl rollout status` and the collector. At ranges wider than ~a week Grafana's step can skip a marker; the RED
*Running releases* table is the record.
