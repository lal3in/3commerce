# 0067 — Error inbox: self-hosted GlitchTip, fed by the Sentry .NET SDK and the storefront's RUM relay

Status: Accepted — implemented
Area: Observability / privacy / dev infra
Extends: [0064](./0064-release-identity-on-telemetry.md) (release identity), [0065](./0065-self-hosted-lgtm-telemetry-alerting-and-browser-rum.md) (self-hosted telemetry, owner Q5), [0066](./0066-storefront-browser-and-ssr-telemetry.md) (anonymous browser telemetry), [0009](./0009-local-dev-bare-dotnet-run.md) (bare-run infra)
Source: observability capability audit (`docs/reviews/observability-capability-audit.md`) Q5 and its "Faro + LGTM vs a
Sentry-compatible tracker" comparison; owner decisions 2026-11-15 (Q5: GlitchTip) and 2026-11-19 (GlitchTip is ON BY
DEFAULT in the dev infra set; the 6 GB dev VM grows only by what is measured)

## Context

Errors already reach Loki (logs), Tempo (exception events on spans) and the Grafana alert rules (rates). What is
missing is **triage**: one place where an error is an *issue* — grouped by fingerprint, with first seen, last seen,
count, a regression flag when a resolved issue comes back in a new release, and assign/resolve. The owner chose
GlitchTip (Q5): MIT, self-hosted, Sentry-API-compatible, small (one Django process plus Postgres), where Sentry
self-hosted needs ~20 containers and does not fit the dev VM.

## Decision

### 1. Two GlitchTip projects; every error carries the release and the trace id

`backend` (every .NET process: 13 services, gateway, Notifications worker, Admin, Supplier Portal) and `storefront`
(browser + SSR). Every event carries:

- `release` = `service.version` (`<Version>+<sha7>[-dirty]`, ADR-0064) and `environment` =
  `deployment.environment.name` — so "first seen in release X" and regressions work across the ADR-0058 promotions;
- the OpenTelemetry **trace id** in the event's trace context *and* as a searchable `trace_id` tag (span id too) — an
  issue leads to its trace in Tempo and its log lines in Loki (`trace_id` is a Loki label match);
- tags `service`, `service.instance.id`, `vcs.ref.head.revision`, the route **template** (`http.route`, never the raw
  path) and method; the storefront adds `runtime` (`browser` / `node`) and `support_ref` (the reference the error page
  shows the shopper — digest or trace id, ADR-0066), so support can search the inbox by what the customer quotes.

### 2. .NET: the Sentry SDK's generic-host + ILogger integration, OFF without a DSN

`AddTelemetryCore` (BuildingBlocks.Observability, so every host gets it) calls `AddErrorTracking`: when `Sentry:Dsn`
or `SENTRY_DSN` is set it calls `UseSentry` (`Sentry.Extensions.Hosting`; Sentry 7 moved SDK initialisation out of
the logging integration). Every `Error`/`Critical` log record with its exception becomes an event: unhandled request
exceptions (the exception handler logs them), MassTransit faults, scheduled-job failures, Blazor circuit errors
(`LoggingErrorBoundary`). Unset = nothing registered, nothing sent (unit tests, CI's core set, any environment that
does not opt in).

Privacy and coexistence with OpenTelemetry (`ErrorTracking.Configure`):

- no `Sentry.AspNetCore` → no request URL, query, headers, cookies, body or client IP is ever captured; the hosting
  log scope's raw `RequestPath` tag is dropped; `SendDefaultPii = false`, `IsEnvironmentUser = false`, user cleared;
- **no breadcrumbs** (`MinimumBreadcrumbLevel = None`): without a per-request scope they would mix requests — the
  trace id gives the real context in Loki/Tempo;
- no Sentry tracing, no `sentry-trace`/`baggage`/`traceparent` headers on outgoing calls (`TracePropagationTargets`
  empty), no HTTP-message-handler, no failed-request capture, no DiagnosticSource integration: OpenTelemetry owns
  tracing and propagation (Stripe and every other outbound call see no new header);
- `BeforeSend` (`ErrorTracking.Prepare` → `TelemetryScrubber`) scrubs messages, exception values, mechanism data, tags
  and extras with the collector's `transform/redact` patterns (emails, `token=`, `password=`, bearer) plus JWTs — a
  Postgres unique violation can quote the email it rejected;
- **client errors are not issues**: `AddServiceTelemetry` adds `ClientErrorExceptionFilter`, dropping
  `IProblemException` / `BadHttpRequestException` mapped to a 4xx (the exception handler logs those at Error too);
  a 5xx of the same types still goes through.

An internal **error probe** `GET /health/error-probe` (every service and the worker via `MapServiceHealth`; the
gateway 404s any `/api/*/health*` path) throws on purpose so the live suite can prove the whole path; it is **not
mapped in Production**.

### 3. Storefront: no Sentry SDK — the `/rum` relay forwards Faro exceptions, SSR errors go from `onRequestError`

Two options were weighed: the Sentry browser SDK (`@sentry/nextjs`) under ADR-0066's rules, or routing what Faro
already captures. **Routing Faro errors is cleaner**, so:

- the browser keeps ONE instrumentation path (Faro, anonymised by `lib/telemetry/scrub.ts` in the page and again in
  the relay); the relay (`app/rum/route.ts`) converts each Faro *exception* of the scrubbed batch into a Sentry store
  event (`lib/telemetry/error-inbox.ts`: Sentry frame order, chunk frames marked not-in-app, at most 10 per batch)
  and posts it **server-side** to `SENTRY_DSN` (a server env var, never `NEXT_PUBLIC_`). The shopper's browser never
  talks to GlitchTip: no DSN, no IP, no user agent reaches it from the page;
- `instrumentation.ts` `onRequestError` sends each SSR/route-handler error with digest, route, method and trace id;
- both are best-effort side by side with the Faro receiver hop (`Promise.allSettled`, 3 s timeout) — a missing inbox
  never changes what the page sees;
- **no new npm dependency**: the store API is one `fetch`. Source maps for symbolication stay the documented manual
  upload step of ADR-0066 (GlitchTip accepts `sentry-cli` uploads).

### 4. Local dev: lean, ON BY DEFAULT, part of the all-or-nothing infra set

`glitchtip/glitchtip:7.0.0` is a `portals`-profile service of `docker-compose.infra.yml`, so `scripts/lib/infra.sh`
counts it in the full set (dev-up, dev-down, doctor, host-check, e2e-verify) and CI's full set (browser-e2e on pushes
to `test`/`main`) with no extra list to maintain. Lean by construction:

- **one process**: GlitchTip 7.0 removed `SERVER_ROLE` / `GLITCHTIP_EMBED_WORKER`; its `bin/start.sh` runs one granian
  process that serves HTTP and consumes the task queue and runs the scheduler — one granian worker, ingest
  concurrency 2, `cpus: 1`, `memory: 512M`;
- **no Valkey**: `VALKEY_URL=""` keeps the task queue and cache in its Postgres database (the app's Valkey stays the
  app's; its `volatile-lru` policy is tuned for TTL'd keys);
- the **shared dev Postgres**, database + role `glitchtip` (no `_db` suffix: those are the service databases e2e L1
  counts against `init-databases.sql`), created **idempotently by the container's entrypoint**
  (`deploy/observability/glitchtip/entrypoint.sh`, as the dev superuser) — `init-databases.sql` only runs on a brand-new
  volume, so this one mechanism covers fresh and existing volumes alike;
- a bootstrap (`bootstrap.py`, run on every start, idempotent) creates the admin sign-in, organisation `3commerce` and
  the two projects **with the exact ids and keys of the DSNs in `deploy/observability/glitchtip/glitchtip.env`** —
  the one file the compose service and `scripts/lib/glitchtip.sh` both read, so `run-all.sh`, `dev-up.sh` and
  `e2e-verify.sh` hand every process its `SENTRY_DSN` before GlitchTip has ever started (and a read-only API token for
  `doctor.sh` and L20c). Throwaway dev values, like the other dev passwords;
- server-side scrubbing on too (`GLITCHTIP_PII_SCRUB_DEFAULT`, `scrub_ip_addresses`), log ingestion and uptime
  monitoring off (Loki and the Grafana alert rules own those), 14-day retention.

Readiness: the image's own healthcheck (`/_health/`), an `_infra_probe` on the published port, and dev-up's gate waits
until the bootstrapped project answers the API. `doctor.sh` prints health and unresolved issue counts per project;
`host-check.sh` probes `:8000` and scans its log.

### 5. Production: the upstream chart, managed Postgres, DSNs as a Secret

`deploy/helm/observability/values-glitchtip.yaml` for chart `glitchtip/glitchtip` 9.2.0 (app 7.0.0), installed by
`install-lgtm.sh` into `observability` beside LGTM: 2 replicas (each runs the embedded worker), no Valkey (also for the
migration Job), managed Postgres ≥ 15 with `max_locks_per_transaction = 512`, registration and organisation creation
off, 90-day retention, `SECRET_KEY` / `DATABASE_URL` / `EMAIL_URL` from Secrets, no ingress (port-forward, like Grafana).
The app chart's `errorTracking.dsnSecret` (values-prod: `glitchtip-dsn`, keys `backend` / `storefront`) becomes
`SENTRY_DSN` on every .NET pod and the storefront, as an **optional** secretKeyRef: a missing Secret means "off", never
a crash-looping pod. Admin and Supplier Portal pods now also get `OTEL_RESOURCE_ATTRIBUTES`, so their errors carry the
release stage.

### 6. Dependencies (each justified)

| Dependency | Why | Licence |
|---|---|---|
| `glitchtip/glitchtip:7.0.0` image (dev) + chart `glitchtip` 9.2.0 (prod) | The inbox itself (owner Q5). Smallest Sentry-compatible server: one process + Postgres. | MIT |
| `Sentry.Extensions.Hosting` 7.0.1 (→ `Sentry`, `Sentry.Extensions.Logging` 7.0.1) | The maintained client for the Sentry protocol: envelopes, grouping-friendly stack traces, rate limiting, a bounded background queue. Hosting is required since 7.0 (the logging package no longer initialises the SDK). No AspNetCore package. | MIT |
| npm: none | The storefront posts store events with `fetch`. | — |

## Rejected

- **Sentry browser SDK / `@sentry/nextjs`**: a second instrumentation path in the page beside Faro (already rejected in
  ADR-0066), a public ingest endpoint for the browser, and a third scrubber to keep equal to `scrub.ts`.
- **`Sentry.AspNetCore`**: request capture (URL, headers, cookies, body, IP) is exactly what Q3 rules out; per-request
  scopes are not worth that risk when the trace id already links the context.
- **An OpenTelemetry → Sentry exporter** (collector or SDK): exception span events lack the stack frames/grouping the
  inbox needs, and Sentry's own OTel bridge would make Sentry a second tracing SDK.
- **Sentry self-hosted**: ~20 containers (Kafka, ClickHouse, Snuba), 16 GB+ — does not fit the dev VM (audit table).
- **A separate Postgres container for GlitchTip** (+ ~60–100 MB) and **Valkey for its queue**: the shared Postgres and
  database-backed tasks are enough at dev scale; prod uses a managed Postgres.
- **The `glitchtip` database in `init-databases.sql`**: it would only exist on fresh volumes and would break L1's
  database count; the entrypoint's idempotent create covers both.
- **Opt-in profile**: the owner wants it on by default (2026-11-19), so a regression in the error path shows locally.

## Memory (6 GB dev VM)

Measured with the whole stack up (full infra set incl. GlitchTip, 13 services, gateway, worker, 3 frontends, seeded):
see the PR and the amendment below for the numbers, compared with the 2026-11-19 baseline (VM used 3651 MB of 5911).

## Consequences

- An error is now an issue with an owner, a regression flag and a link to its trace; the Grafana alert rules stay the
  paging path (rates), GlitchTip the triage path (what broke, since which release).
- One more container in the all-or-nothing infra set (and in CI's full set); `INFRA_SET=core` (PR CI) has none — the
  SDKs then fail to deliver silently, as OTLP does without a collector.
- The inbox holds error text after scrubbing; it is ops data, not an audit trail. Retention 14 d dev / 90 d prod.
- Pinned by `ErrorTrackingTests` (unit lane, e2e-verify **A3zq**: off without DSN, options, trace id + route + scrub,
  4xx filter, probe outside Production only, end to end through a fake ingest), the storefront conversion unit tests
  (**A7d**), and live **L20c** (a catalog probe's trace id and release found in the backend project; the storefront's
  SSR and browser probes seen as `runtime:node` / `runtime:browser` issues with a trace id and a release).
- Runbook: `docs/runbooks/observability.md`, "Error inbox (GlitchTip)".
