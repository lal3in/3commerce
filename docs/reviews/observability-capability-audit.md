# Observability capability audit (2026-10)

> **Scope:** a static audit of code, config, dashboards, scripts and docs, measuring 3commerce's
> observability against the capability list that error-monitoring and APM products sell (Sentry, Datadog,
> Google Cloud Observability). No production code was changed and nothing was run against Docker.
> Baseline: `origin/develop` at `2c5edc8`. Line references are to that commit.
>
> **Status key:** **Have** means the signal is captured, correlated and viewable today. **Partial** means
> some of it is, with named gaps. **Missing** means nothing is in place. **N/A** means the product has no
> such surface today.

## Scorecard

| # | Capability | Status | Key evidence | Top gap |
|---|---|---|---|---|
| 1 | Errors and exceptions | **Partial** | Server side: the ASP.NET exception handler returns problem+json with `traceId` (`src/BuildingBlocks/Infrastructure/Web/ProblemDetailsExtensions.cs:13-55`). Error logs reach Loki with `trace_id` (`Observability/OtelExtensions.cs:67-77`). MassTransit `_error`/`_skipped` queue depth shows in Mission Control (`src/Admin/Services/BusStatsService.cs:49-59`). | Nothing groups errors into issues and nothing alerts on them. Browser and SSR errors are not collected (no `instrumentation.ts`, no `error.tsx` or `global-error.tsx`). The gateway's logs never reach Loki. Admin and SupplierPortal export no telemetry at all. |
| 2 | Stack traces and local variables | **Partial** | .NET exception stacks with file and line (portable PDBs ship in the `dotnet publish` output) reach Loki through `ILogger` | Stack traces are log text, not OTel `exception.*` span events. No local-variable capture. The storefront has no browser source maps and no capture. Neither SourceLink nor commit metadata is embedded. |
| 3 | Breadcrumbs | **Partial** | Server side, the logs of one trace act as breadcrumbs, with Tempo↔Loki links (`deploy/observability/grafana/provisioning/datasources/lgtm.yml`). The storefront has consent-gated first-party analytics events (`src/Storefront/lib/analytics.ts`). | No client breadcrumbs (clicks, navigation, console output, fetches) are tied to an error. The analytics events are business events, not diagnostics, and carry no trace link. |
| 4 | Performance metrics | **Partial** | HTTP RED metrics per service with p95 (`dashboards/services-red.json`). Tempo span-metrics and a service graph (`deploy/observability/tempo.yaml` `metrics_generator`). Custom Redis/Valkey and stream meters. | No DB query spans or durations: EF/Npgsql is **not** instrumented, although the comment at `OtelExtensions.cs:17` says it is. No N+1 detection, no runtime (GC/threadpool) metrics, no MassTransit meter, no SLO/Apdex panels, no Web Vitals. |
| 5 | Distributed tracing | **Partial** | The gateway, 13 services and the Notifications worker export traces. The MassTransit `ActivitySource` joins message hops to the HTTP trace (`OtelExtensions.cs:37`). W3C context propagates over HttpClient and YARP. | Traces start at the gateway, so the browser and Next.js SSR are dark. Admin and SupplierPortal are dark. Postgres, Valkey and Kafka have no client spans. There is no sampling policy. |
| 6 | Session replays | **Missing** (by design) | Deliberately absent: `src/Storefront/lib/analytics.ts:1-3` says "no session replay, no keystroke logging" | Not recommended now because of PCI SAQ-A and GDPR. If it is ever added: replay only on error, mask everything, exclude the Stripe iframe. |
| 7 | Profiling | **Missing** | No profiler, no `dotnet-monitor`, no Pyroscope | No CPU or allocation profiles of slow transactions, for .NET or Node |
| 8 | Release and deployment data | **Missing** | The resource is `AddService(name)` only (`OtelExtensions.cs:31`, `src/Gateway/Program.cs:17`): no `service.version`. Dockerfiles take no build args and set no labels. ADR-0058 promotions carry no version stamp. | You cannot tell which commit, PR or promotion produced an error. Grafana has no release markers. *(2026-10-15: release stamping done, ADR-0064; release markers = P2-2.)* |
| 9 | Device and environment metadata | **Partial** | Server spans carry `user_agent.original`, `server.address` and `url.*` from the ASP.NET instrumentation. Helm sets `ASPNETCORE_ENVIRONMENT`, and Loki/Grafana split by `service_name`. | No `deployment.environment.name` and no host, container or k8s resource detectors, so test and prod telemetry cannot be told apart *(2026-10-15: `deployment.environment.name` + `service.instance.id` added, ADR-0064)*. No browser or device context. Client IPs are logged in places, with no redaction or retention policy. |
| 10 | AI and LLM observability | **N/A** | `src/` has no LLM SDKs and no AI features. The only related item is a static `/llms.txt` route (`src/Storefront/app/llms.txt/route.ts`). | Not applicable today. §10 lists what to add if an AI feature lands. |

**Score:** 0 Have · 6 Partial · 3 Missing · 1 N/A. The backend is a solid OpenTelemetry/LGTM foundation
(three signals, cross-linked in Grafana and tested end to end), but it is a **dev-only** foundation. The
browser/SSR/Blazor edge, release metadata, DB-level spans, alerting and every production backend are absent.

**Production readiness overall:** the LGTM stack runs on **local dev only**. It uses filesystem storage,
has no auth, keeps Tempo data for 48 h, and Grafana defaults to `admin/admin`. Helm ships an opt-in
in-cluster copy (`observability.enabled: false` in both `values.yaml` and `values-prod.yaml`) that uses
emptyDir storage, has **no Grafana**, and whose collector lacks the dev Loki-noise filter. The
containerized launch exports nothing by default: `OTEL_EXPORTER_OTLP_ENDPOINT` is commented out in
`deploy/.env.dev:11`. There is no test or prod telemetry backend and no alert rule anywhere.
`host-check.sh --logs` only tails provider-managed logs when `CW_LOG_GROUP`/`GCP_PROJECT`/`AZ_WORKSPACE`
are set.

---

## 0. What exists today (the baseline)

| Layer | What it does | Evidence |
|---|---|---|
| .NET services + Notifications worker | `AddServiceTelemetry(name)`. **Traces:** ASP.NET Core, HttpClient, and the `MassTransit` ActivitySource. **Metrics:** ASP.NET Core, HttpClient, plus the `3commerce.streams` and `3commerce.redis` meters. **Logs:** the OTLP log exporter (formatted message, scopes, parsed state). Exports over OTLP only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. Otherwise traces go to the console and metrics and logs are not exported. | `src/BuildingBlocks/Infrastructure/Observability/OtelExtensions.cs:25-80`; called from every `src/Services/*/Api/Program.cs` and `src/Workers/Notifications/Program.cs:14` |
| Gateway (YARP) | Has its own setup: ASP.NET Core and HttpClient traces, ASP.NET Core and Redis metrics. **No OTLP logs.** | `src/Gateway/Program.cs:16-38` |
| Admin, SupplierPortal (Blazor Server) | **No OpenTelemetry at all.** Errors go to the console log only (`.run/admin.log`, `.run/supplier-portal.log`). | `src/Admin/Program.cs` and `src/SupplierPortal/Program.cs` contain no `OpenTelemetry` reference |
| Storefront (Next.js 15) | **No telemetry.** No `instrumentation.ts`, no OTel or RUM packages (`src/Storefront/package.json`), no error boundaries, and no source-map config (`next.config.ts`). It reads the problem+json `traceId` (`lib/gateway.ts:337-345`) but never shows it to anyone. | as listed |
| Collector | OTLP gRPC/HTTP receivers plus a redis_exporter scrape. A `filter/loki_noise` processor drops background EF/MassTransit logs that have no trace. Fan-out: traces → Tempo, logs → Loki `/otlp`, metrics → Prometheus scrape and Mimir remote-write. No redaction processor and no tail sampling. | `deploy/observability/otel-collector-config.yaml` |
| Backends | Tempo 2.6.1 (48 h block retention, metrics-generator writing service-graphs and span-metrics with exemplars to Mimir), Loki 3.3.2 (filesystem, no explicit retention), Mimir 2.14.1 (filesystem), Prometheus 2.54.1, Grafana 11.2.0 on `:3001` | `deploy/observability/*.yaml`, `docker-compose.yml:231-335` |
| Grafana | Datasources cross-linked: log→trace (derived field), trace→logs, trace→metrics, service map, exemplars. Four dashboards: Services (RED), Traces (Tempo), Logs (Loki), Redis/Valkey. No alerting provisioning. | `deploy/observability/grafana/provisioning/**` |
| Mission Control (admin) | Business KPIs plus live bus stats (RabbitMQ management API: queue depth, unacked messages, dead-letter `_error`/`_skipped`) plus observability counts (Loki log lines and error lines in the last hour, Tempo trace count, Mimir service count). Links out to Grafana Explore. | `src/Admin/Components/Pages/MissionControl.razor`, `src/Admin/Services/BusStatsService.cs`, `src/Admin/Services/ObservabilityStatsService.cs:22-47` |
| Tests | `e2e/observability-portals.spec.ts`: Grafana is healthy with its datasources, Loki/Tempo/Mimir are ready, **a real checkout lands real-service logs in Loki and traces in Tempo**, and every dashboard panel returns data. It runs on the full infra set (pushes to `test`/`main`, ADR-0058). | `src/Storefront/e2e/observability-portals.spec.ts:39-160` |
| Ops scripts | `doctor.sh` and `host-check.sh` (health, bus, infra logs, Colima OOM, and `--logs` for CloudWatch/GCP/Azure) | `scripts/host-check.sh:9,128-138` |
| SLOs | ADR-0029: availability ≥ 99.5 %, p95 < 500 ms for search and product detail, 5xx < 1 %. "Alerting wiring is launch-gated." | `docs/adr/0029-phase-6-compliance-ops-primitives.md:38-46` |

---

## 1. Errors and exceptions: **Partial**

**Captured today**
- **HTTP services.** `UseApiProblemDetails` (`ProblemDetailsExtensions.cs:39-55`) installs `UseExceptionHandler`. The framework's `ExceptionHandlerMiddleware` logs every unhandled exception at `Error` with the full exception, and that record reaches Loki through the OTLP log exporter with `trace_id`/`span_id`. The response is problem+json carrying `traceId`, so a client-reported error can be looked up in Tempo. The ASP.NET Core instrumentation marks the server span `status=Error` for 5xx.
- **Message consumers and sagas.** `UseMessageRetry(Incremental(3, 1 s, 5 s))` (`Messaging/MassTransitExtensions.cs:61,96`). Once retries are exhausted, MassTransit moves the message to `<queue>_error`, logs the fault at `Error` with the exception (in Loki, trace-linked), and marks its consume activity as failed. The dead-letter depth is visible in Mission Control (`BusStatsService.cs:49-59`) and through `host-check.sh` bus state. The stream lane counts `stream_events_dead_lettered_total` (`docs/runbooks/messaging-observability.md:13`).
- **Dashboards.** "Error rate (5xx)" by service (`services-red.json`), "Error-level volume" by service (`logs-overview.json`), and "Failed traces (status = error)" (`traces-overview.json`).

**Gaps**
- **No issue grouping, first-seen/regressed state, assignment or alerting.** Errors exist only as log lines and red spans, and nobody is notified. ADR-0029's "5xx < 1 %" SLO has no alert rule (none in `deploy/observability`, and Grafana has no alerting provisioning).
- **Browser errors are not collected:** uncaught exceptions, unhandled promise rejections and failed `fetch` calls in the storefront are invisible. **SSR errors** go only to the Next server's stdout (`.run/storefront.log` in dev, container stdout elsewhere). With no `app/error.tsx` or `app/global-error.tsx`, a render error shows Next's default error page, with no `traceId` for support to quote.
- **Blazor circuits.** Admin and SupplierPortal register `UseExceptionHandler("/Error")` (`src/Admin/Program.cs:77`), but circuit exceptions (`Microsoft.AspNetCore.Components.Server.Circuits`) go only to the console logger, because those apps have no OTel. No `<ErrorBoundary>` wraps the pages.
- **The gateway** is the public edge, but its logs (YARP proxy errors, auth-introspection failures, rate-limit decisions) are not exported to Loki. Its traces are.
- **Retry policy.** MassTransit retries immediately, with no delayed redelivery. No `Fault<T>` consumers exist (`grep IConsumer<Fault<` finds none), so a dead-lettered business message (for example a failed `OrderConfirmed` projection) raises no business-level signal. The only signals are a red count on Mission Control and an `Error` log.

**Production readiness:** every collection path depends on `OTEL_EXPORTER_OTLP_ENDPOINT`. Bare-run
defaults it to `localhost:4317` (`scripts/run-all.sh:58`). The containerized launch leaves it commented out
(`deploy/.env.dev:11`). Helm sets it only with `observability.enabled=true`, which is off in prod. In prod
today, errors would exist only in container stdout, read through `host-check.sh --logs`.

---

## 2. Stack traces and local variables: **Partial**

- **.NET.** `dotnet publish -c Release` (for example `src/Gateway/Dockerfile:7`) emits portable PDBs next to the assemblies, and the Dockerfiles copy the whole `/app`. Logged stack traces therefore carry **file and line**. They land in Loki as part of the log record's exception attributes (`exception.type`, `exception.message`, `exception.stacktrace` on the log), not as span events. The AspNetCore instrumentation does not record the exception on the span (`RecordException` is not set), so the Tempo span shows status=Error without the stack. You have to hop to Loki.
- **No local-variable or frame capture.** Neither OTel nor .NET captures locals. Only Sentry/Datadog-style agents do, which would mean a new dependency. Given the domain (money, PII), **not capturing locals is the safer default.**
- **No SourceLink and no build metadata.** `Directory.Build.props` sets neither `ContinuousIntegrationBuild`, `SourceRevisionId` nor `InformationalVersion`, so a trace cannot point at the exact source revision.
- **Storefront.** Browser bundles are minified, and `productionBrowserSourceMaps` is off (the default; `next.config.ts` does not set it). Without a collector, a browser stack would be unreadable anyway. Server (SSR) stacks are readable in stdout.

**Risk:** an exception message or a structured-log state value can contain PII, such as an email in a
validation message. Nothing in the pipeline redacts it (see §9).

---

## 3. Breadcrumbs: **Partial** (server side only)

- **Server side the trace *is* the breadcrumb trail.** The OTLP logs carry `trace_id`, and Grafana links them both ways (`lgtm.yml` `derivedFields` and `tracesToLogsV2`). From a failed span you can read every log line across gateway → services → consumers in the same trace, except the gateway's own logs (§1). In-request EF SQL logs are kept; background polling noise is dropped (`otel-collector-config.yaml:26-34`).
- **The client side has no breadcrumbs.** The storefront's `track()` (`lib/analytics.ts`) sends **business events**: consent-gated, batched to `/api/analytics/events`, with no trace id, and dropped on failure. These cannot answer "what did the shopper click before the 500?". Admin (Blazor) has nothing.
- **Recommendation:** Grafana Faro (P1, §Roadmap) records navigation, console, fetch/XHR and user-interaction breadcrumbs and attaches them to the error. It also injects `traceparent` into fetches so a browser breadcrumb links to the backend trace. Breadcrumbs must honour the existing consent model (`lib/consent.ts`): either gate collection on an operational-telemetry decision or limit it to strictly-necessary error diagnostics with no identifiers (open question Q3).

---

## 4. Performance metrics: **Partial**

**Have**
- HTTP server and client RED metrics per service (`http_server_request_duration_seconds_*`), with request rate, 5xx rate and p95 on `services-red.json`
- Tempo span-metrics (`traces_spanmetrics_*`) and service-graph metrics, with exemplars back to traces (`tempo.yaml:29-49`, `lgtm.yml` `tracesToMetrics`/`exemplarTraceIdDestinations`)
- Redis/Valkey fast-path metrics and server metrics (`redis-overview.json`); stream-lane counters (`StreamMetrics`)
- RabbitMQ queue depth and throughput through Mission Control (polled, not stored as a time series)

**Missing**
- **DB query durations.** `OtelExtensions.cs:17` says "Traces for HTTP in/out, EF, and MassTransit", but no EF Core or Npgsql instrumentation is registered: there is no `AddEntityFrameworkCoreInstrumentation`, no `AddNpgsql()`, and no `AddSource("Npgsql")`. The only DB timing is EF's `Executed DbCommand (N ms)` log line, at Information, in Loki. **N+1 detection** is impossible without DB spans.
- **Runtime metrics** (GC, heap, threadpool queue, exceptions/sec): `AddRuntimeInstrumentation` is absent. .NET 9+ also exposes the built-in `System.Runtime` meter, which could be added with `AddMeter("System.Runtime")`, a no-new-package option.
- **MassTransit metrics.** MassTransit 8 publishes a `MassTransit` meter (consume/publish counts, durations, faults, in-flight messages), and it is not added.
- **Apdex/SLO.** ADR-0029's SLOs have no recording rules, burn-rate panels or Apdex.
- **Frontend.** No Web Vitals (LCP, INP, CLS, TTFB). No Next.js server timings. Blazor has no circuit or latency metrics.
- **Kafka** consumer lag depends on an external exporter that is not deployed (the runbook names it as future).

---

## 5. Distributed tracing: **Partial**

| Hop | Status | Evidence / gap |
|---|---|---|
| Browser → Next.js | **Missing** | No browser tracer, so no `traceparent` is sent |
| Next.js SSR / server actions → gateway | **Missing** | No `instrumentation.ts`. `lib/gateway.ts:47-54` calls `fetch` without a `traceparent` header. |
| Gateway → services (YARP) | **Have** | ASP.NET Core + HttpClient instrumentation. YARP propagates W3C context. The gateway is the trace root today. |
| Service → service (HTTP read queries) | **Have** | HttpClient instrumentation |
| Service → bus → consumer / saga (incl. EF outbox) | **Have** | `AddSource("MassTransit")`. MassTransit writes the trace context into message headers, and the outbox persists the headers, so the delivery joins the original trace. The live checkout test proves logs and traces from real services land (`observability-portals.spec.ts:85-130`). |
| Kafka stream lane | **Partial** | Stream metrics exist. No producer or consumer spans from the Kafka client (Confluent.Kafka has no built-in OTel). Whether the stream envelope carries `traceparent` was not checked (open question Q6). |
| → Postgres | **Missing** | No Npgsql/EF spans (§4) |
| → Valkey | **Missing** | Custom metrics only. No StackExchange.Redis spans. |
| Admin / SupplierPortal → gateway | **Missing** | No OTel in either Blazor app, so admin-initiated actions start a fresh trace at the gateway |
| Quartz jobs | **Partial** | A job's own work has no root span. Messages it publishes start a new trace. |

**Sampling:** none is configured, so the SDK default `ParentBased(AlwaysOn)` keeps 100 % of traces. That
is fine for dev. In prod it is a volume and cost risk with nothing to dial down. The collector has no
`tail_sampling` processor, which is the usual fix: keep all errors and slow traces, and sample the rest.

---

## 6. Session replays: **Missing (by design — keep it that way for now)**

`lib/analytics.ts:1-3` rules out session replay and keystroke logging on purpose. That fits the
constraints:
- **PCI DSS SAQ-A:** the platform's eligibility depends on card data never touching our systems (Stripe Payment Element in a Stripe iframe). A DOM recorder cannot see inside a cross-origin iframe. But one that records input events on our own pages, such as address or email, would be collecting **personal data**. A misconfigured recorder on a future non-iframe payment method would put SAQ-A at risk.
- **GDPR / ePrivacy:** replay is not "strictly necessary". It needs consent under the existing `lib/consent.ts` model, and a DPIA.

**If it is ever wanted:** use an OSS, self-hosted recorder (rrweb-based: Faro has an experimental replay
add-on, or OpenReplay). Enable it **only on error or rage-click** with a short buffer, mask all text and
inputs by default (`maskAllInputs`, `maskTextSelector: '*'`), block checkout and account routes and the
Stripe iframe entirely, set short retention, and gate it on analytics consent. Rated **P2/optional**,
and only after the owner decides (Q4).

---

## 7. Profiling: **Missing**

There is no continuous or on-demand profiler: no `dotnet-monitor` sidecar, no Pyroscope, and no Node
`--cpu-prof`. A slow trace shows *which* span is slow, never *why* (CPU, allocations, lock contention).

**Recommendation (P2):** Grafana Pyroscope as a fifth LGTM backend. For .NET, use the Pyroscope .NET
profiler (a CLR profiler attached through `CORECLR_ENABLE_PROFILING` env vars plus a native library in
the image) with span-profile linking (`Pyroscope.OpenTelemetry`), so Tempo shows "profile for this span".
That is a **new dependency** (a native agent in every image) to be justified against image size and
overhead (~2–5 % CPU). A cheaper first step that adds nothing: `dotnet-counters`/`dotnet-trace` on demand
via `dotnet-monitor` in the Helm chart for prod triage. For Node, Pyroscope's `@pyroscope/nodejs` (new
dependency) or `--cpu-prof` on demand.

---

## 8. Release and deployment data: **Missing**

> **Update 2026-10-15:** release stamping (P0-1) is implemented — [ADR-0064](../adr/0064-release-identity-on-telemetry.md).
> Every .NET resource now carries `service.version`, `vcs.ref.head.revision`, `deployment.environment.name` and a
> shared `service.instance.id`; every image carries OCI labels. Promotion annotations (P2-2) are still open. The
> findings below describe the `2c5edc8` baseline.

- The resource is `AddService(serviceName)` with no `serviceVersion` (`OtelExtensions.cs:31`, `Gateway/Program.cs:17`, `OtelExtensions.cs:71`), so every span, metric and log has `service.version` unset.
- No Dockerfile takes an `ARG GIT_SHA`/`VERSION` or sets OCI labels (`org.opencontainers.image.revision`). `scripts/build-images.sh` and CI pass no `--build-arg`. Neither the assembly `InformationalVersion` nor `SourceRevisionId` is set.
- ADR-0058's flow (develop → `test` → `main` by fast-forward of a tested SHA) is ideal for release tracking, because the release identity *is* the commit SHA. But `scripts/promote.sh` creates no tag and records no deployment event, and Grafana has no annotations.
- Result: when an error rate jumps, you cannot tell whether a promotion caused it, or which PR.

**Recommendation (P0, small):** stamp the build. In CI and `build-images.sh`, pass `--build-arg
GIT_SHA=$(git rev-parse HEAD)` and `VERSION`. Dockerfiles set `ENV OTEL_RESOURCE_ATTRIBUTES=service.version=$VERSION,vcs.ref.head.revision=$GIT_SHA`
plus OCI labels. Alternatively, `dotnet publish -p:SourceRevisionId=$GIT_SHA`, and `AddServiceTelemetry`
reads `AssemblyInformationalVersionAttribute` into `serviceVersion`. The SDK honours
`OTEL_RESOURCE_ATTRIBUTES` automatically, so the code change is minimal. Then have `promote.sh` post a
Grafana annotation (`POST /api/annotations`, tags `release,test|main`, text = SHA + PR list) and push a
lightweight tag `release/<stage>/<date>-<sha7>`. Dashboards then overlay release markers.

---

## 9. Device and environment metadata: **Partial**

**Have:** ASP.NET Core server spans carry `http.request.method`, `url.path`, `http.route`,
`user_agent.original`, `server.address`/`port` and the response status. Query-string values are redacted
by default in current OTel .NET instrumentation. Logs and metrics carry `service.name` (`service_name` in
Loki/Mimir).

**Missing**
- `deployment.environment.name` (dev/test/prod). Once a shared backend exists, test and prod telemetry would be indistinguishable. Helm sets `ASPNETCORE_ENVIRONMENT`, but that never reaches the resource.
- Resource detectors: `service.instance.id`, `host.*`, `container.id`, `k8s.pod.name`/`namespace` (OTel's `k8sattributes` processor in the collector, or `OpenTelemetry.Resources.Container`/`.Host`, a new package)
- Browser and device context (OS, browser, viewport, connection): needs Faro (§3)

**Privacy / PII risks found**
- **(Addressed 2026-10-15, see P0-2.)** `src/Workers/Notifications/Email/LoggingEmailSender.cs:13-15` logs **recipient, subject and full body** at `Information`. It is the only `IEmailSender` (`Notifications/Program.cs:24-29`), so every customer email, including verification and password-reset links (bearer secrets), lands in console logs and in **Loki**. That is acceptable in dev, but it must not ship to a shared backend. **P0:** drop the body or the log level, or redact in the collector, before any non-dev backend is connected.
- `src/Admin/Services/IpAllowlistMiddleware.cs:29` logs the denied client IP (an IP is personal data under GDPR. Legitimate interest is plausible for security logs, but it needs a retention period).
- `src/Services/Identity/Api/DevAdminSeeder.cs:74` logs the seeded admin email (dev only).
- **(Addressed 2026-10-15, see P0-2.)** No collector-side redaction (`attributes`/`transform`/`redaction` processor). No retention is set for Loki (the default keeps everything) or Mimir. Tempo keeps 48 h.
- Grafana defaults to `admin/admin` (`docker-compose.yml:322-323`) and binds `:3001`. Helm ships no Grafana.

**Secrets:** OTLP has no auth (plain gRPC on the compose and cluster network). Any non-local backend
needs TLS plus an auth header. The EF connection strings in `appsettings.json` are dev credentials and are
not logged.

---

## 10. AI and LLM observability: **N/A today**

The code has no LLM SDKs (`openai`, `anthropic`, Semantic Kernel, `Microsoft.Extensions.AI`, LangChain),
no MCP server or client, and no AI feature. `src/Storefront/app/llms.txt/route.ts` is a **static**,
agent-readable store summary (mt5_8): content for external crawlers, not an AI workload. The CLI
(`src/Cli`) is a plain .NET tool.

**If an AI feature is added** (product copy generation, support triage, a shopping assistant):
instrument it with the **OTel GenAI semantic conventions** (`gen_ai.request.model`,
`gen_ai.usage.input_tokens`/`output_tokens`, `gen_ai.operation.name`, tool-call spans). `Microsoft.Extensions.AI`
emits these through `UseOpenTelemetry()`, and they flow to Tempo/Mimir with no new backend. Add a
cost-per-tenant metric, keep prompt and completion **content capture off by default** (it is PII-heavy and
must be consent- and retention-scoped), trace MCP tool calls as child spans, and add evaluation signals
(refusals, latency, error rate) to a dashboard. OSS options if deeper LLM tracing is needed: Langfuse
(self-hosted) or OpenLLMetry. Both are new dependencies.

---

## Roadmap (prioritised)

Effort: **S** ≤ 1 day · **M** 2–4 days · **L** 1–2 weeks. Everything stays on OpenTelemetry + LGTM unless
noted. Rows marked **(new dep)** need the AGENTS.md justification.

### P0: do before any shared or production backend exists

| # | Item | Effort | Notes |
|---|---|---|---|
| P0-1 | **Stamp releases:** `service.version` + `vcs.ref.head.revision` (commit SHA) + `deployment.environment.name` on every resource, via build args → `OTEL_RESOURCE_ATTRIBUTES` (or `AddService(name, serviceVersion: …)` + `AddAttributes`). Add OCI image labels. | S | §8/§9. No new package. Unlocks "which release broke it". **Addressed 2026-10-15 — [ADR-0064](../adr/0064-release-identity-on-telemetry.md)** (all four attributes + OCI labels; `service.instance.id` shared by all signals; Admin/SupplierPortal/storefront OTel stays P1-4/P1-2, annotations P2-2). |
| P0-2 | **Stop logging email bodies** (`LoggingEmailSender`). Add a collector `redaction`/`transform` processor for emails, tokens and `Authorization`/cookie values, and set Loki/Mimir retention. | S | §9. Privacy and secret-leak blocker. **Addressed 2026-10-15** (`fix/email-log-leak-and-log-retention`): full email bodies are logged in Development only (metadata + recipient hash elsewhere), `EmailSenderGuard` refuses a non-Development boot without `Notifications:Email:AllowLoggingSender=true`, collector `transform/redact` (tokens, passwords, bearer, emails, Authorization/Cookie attributes; dev + Helm), explicit retention: Loki 30 d, Tempo 48 h dev / 14 d Helm, Mimir 30 d dev / 13 months prod, Prometheus 15 d. Open: the prod backend itself (P0-5). |
| P0-3 | **Record exceptions on spans:** `AddAspNetCoreInstrumentation(o => o.RecordException = true)` and the same for HttpClient, so Tempo error spans carry `exception.type/message/stacktrace` events. Bring the gateway's OTLP **logs** pipeline in line, ideally by making the gateway call `AddServiceTelemetry`. | S | §1/§2. Removes a hop and closes the gateway log gap. |
| P0-4 | **Alerting** on ADR-0029 SLOs: Grafana-provisioned alert rules for 5xx rate > 1 %, p95 latency, `_error` queue depth > 0 (needs RabbitMQ metrics in Mimir, see P1-5), error-log spikes, and Valkey/collector down. Add a contact point (email/Slack/webhook). | M | ADR-0029 says alerting is "launch-gated". Make it a launch gate item explicitly. |
| P0-5 | **Prod telemetry backend decision** (see Q1): managed Grafana Cloud (OTLP endpoint, free tier) vs self-hosted upstream LGTM Helm charts on object storage, plus Helm values to point `OTEL_EXPORTER_OTLP_ENDPOINT` and headers at it. Re-enable OTLP in `deploy/.env.dev`, and bring the Helm collector config in line with the dev one (noise filter). | M | There is no prod observability today. |

### P1: high value

| # | Item | Effort | Notes |
|---|---|---|---|
| P1-1 | **DB spans:** add `Npgsql.OpenTelemetry` (`AddNpgsql()`) and/or `OpenTelemetry.Instrumentation.EntityFrameworkCore` (beta). Add an N+1 view: a TraceQL/Tempo query or a span-metrics panel counting `db` spans per trace, alerting on more than N same-statement spans. Fix the misleading comment at `OtelExtensions.cs:17`. | M | **(new dep)**, justified: DB time is the dominant latency source and is invisible today. |
| P1-2 | **Storefront RUM + errors:** **Grafana Faro Web SDK** (`@grafana/faro-web-sdk` + `faro-web-tracing`) sending to a collector `faro` receiver or Grafana Alloy. Captures uncaught errors and rejections, console, fetch, Web Vitals, page views and breadcrumbs, and injects `traceparent` so browser → gateway traces join. Upload source maps (Faro CLI or Alloy source-map store), with `productionBrowserSourceMaps` built but **not served publicly**. Consent: see Q3. | M | **(new dep)**. Fits the LGTM direction better than Sentry (below). |
| P1-3 | **Next.js SSR tracing:** `instrumentation.ts` with `@vercel/otel` (or `@opentelemetry/sdk-node`) exporting OTLP. Next 15 emits built-in spans for render, route and fetch, and `fetch` propagates `traceparent` to the gateway. Add `app/error.tsx` + `app/global-error.tsx` that show the `traceId` / digest and log server-side. | M | **(new dep)**. Closes the SSR → gateway gap. |
| P1-4 | **Blazor Admin + SupplierPortal telemetry:** neither project references BuildingBlocks today, so add the OTel `PackageReference`s already pinned in `Directory.Packages.props` (no new dependency for the solution) and the same tracing/metrics/logs setup, or extract a light `AddServiceTelemetry` that does not drag in MassTransit/EF. Then wrap layouts in `<ErrorBoundary>` that logs. Optionally add a `CircuitHandler` that logs circuit open/close/unhandled events with tenant and user ids (no PII). | S | §1/§5 |
| P1-5 | **Messaging metrics:** `AddMeter("MassTransit")`, the RabbitMQ Prometheus plugin (`rabbitmq_prometheus`, built into the image) scraped by the collector, and a "Messaging" dashboard (consume rate, fault rate, `_error` depth, redeliveries). Consider `UseDelayedRedelivery` before `_error` and a generic `Fault<T>` observer that emits a metric per message type. | M | Makes dead letters alertable (P0-4). |
| P1-6 | **Runtime metrics:** `AddMeter("System.Runtime")` (built into .NET 9+, no package) plus a ".NET runtime" dashboard (GC pause, heap, threadpool queue, exceptions). | S | |
| P1-7 | **Sampling policy:** collector `tail_sampling` (keep errors, latency > p95 and 100 % of checkout/payment routes; sample the rest at 10–20 % in prod), plus `ParentBased(TraceIdRatio)` in the SDK as a guard. | S | Cost and volume control before prod |

### P2: later / nice to have

| # | Item | Effort | Notes |
|---|---|---|---|
| P2-1 | **Continuous profiling:** Grafana Pyroscope backend + .NET profiler, with span↔profile links | M | **(new dep)**, native agent in images |
| P2-2 | **Release annotations:** `promote.sh` posts Grafana annotations and pushes `release/*` tags. Add a "deploys" panel overlay. | S | Builds on P0-1 |
| P2-3 | **SLO dashboards:** recording rules + burn-rate panels (or Grafana SLO / Sloth-generated rules) for ADR-0029 targets. Apdex panel from span-metrics. | M | |
| P2-4 | **Valkey/Kafka spans:** StackExchange.Redis instrumentation (`OpenTelemetry.Instrumentation.StackExchangeRedis`, beta) and manual Kafka producer/consumer spans with `traceparent` in the stream envelope | M | **(new dep)** for Redis |
| P2-5 | **Resource detectors / k8s attributes:** collector `k8sattributes` + `resourcedetection` processors in the Helm collector | S | |
| P2-6 | **Session replay (only if approved):** error-triggered, mask-all, checkout and Stripe excluded, consent-gated | M | See §6 and Q4 |
| P2-7 | **GenAI conventions** when an AI feature lands | S | §10 |

### Faro + LGTM vs a Sentry-compatible tracker (brief comparison)

| | **Grafana Faro + existing LGTM** | **GlitchTip (self-hosted, Sentry-API-compatible)** | **Sentry self-hosted** |
|---|---|---|---|
| Fit | Same Grafana pane as traces, logs and metrics. Faro traces join backend traces. | Separate UI. Errors only (plus basic uptime and perf). | Separate UI. Full feature set (issues, perf, replay, profiling). |
| Issue grouping / triage | Weak: errors are logs or Faro events. Grouping uses LogQL `pattern`/fingerprint dashboards. No assign/resolve workflow. | **Strong:** fingerprinted issues, first-seen, regressions, assign/resolve, email alerts | **Strongest** |
| Browser + .NET SDKs | Faro (web). .NET stays OTel. | Uses Sentry SDKs (`@sentry/nextjs`, `Sentry.AspNetCore`): new deps, a second instrumentation path | Same SDKs |
| Ops cost | Low (one more receiver) | Low–medium (Django + Postgres + Redis/Valkey) | **High** (~20 containers: Kafka, ClickHouse, Snuba; 16 GB+ RAM). Does not fit the dev VM. |
| Licence | AGPL backends, Apache SDK | MIT | FSL (source-available) |

**Suggestion:** stay on OTel + Faro for collection (one instrumentation path, already tested). If the
owner wants Sentry-style **issue triage**, add GlitchTip as a light *sink*: the Sentry SDKs can coexist,
or exceptions can be forwarded later through an OTel→Sentry exporter. Skip Sentry self-hosted for its
footprint. Grafana Cloud's free tier (Faro/Frontend Observability, Tempo, Loki, Mimir, Pyroscope) is the
low-effort managed path for test/prod if self-hosting is not wanted (Q1).

---

## Open questions for the owner

1. **Q1: prod telemetry backend.** Managed Grafana Cloud (OTLP plus an API key, data leaves our infra and goes to the US or EU region), or self-hosted upstream LGTM Helm charts on object storage (we operate them)? This decides P0-5 and the retention and GDPR posture.
2. **Q2: retention targets.** Suggested: traces 7–14 days, logs 30 days (security logs longer?), metrics 13 months. Is there a legal hold for audit-adjacent logs? The business audit trail stays in the Audit service.
3. **Q3: consent model for operational browser telemetry.** Is frontend error and performance telemetry "strictly necessary" (collected without consent, but with no identifiers, no IP storage and no replay), or should it be gated on the Analytics consent category like `track()`?
4. **Q4: session replay.** Keep it permanently out (the current stance), or allow error-triggered, masked replay on non-checkout routes after a DPIA?
5. **Q5: issue triage.** Are Grafana dashboards and alerts enough, or do you want an issue inbox (GlitchTip) with assign/resolve and regression detection?
6. **Q6: Kafka trace propagation.** Should the stream envelope carry `traceparent` so durable-stream consumers join the trace? It is an additive contract change (ADR-0034 lane).
7. **Q7: new-dependency approval** for P1-1 (Npgsql/EF OTel), P1-2 (Faro), P1-3 (`@vercel/otel`) and P2-1 (Pyroscope). P0-1/P0-3/P1-4/P1-6 need **no** new package.
8. **Q8: alert routing.** Where should alerts go (email, Slack, PagerDuty/Opsgenie, the existing mt6_5 SecurityAlert channel)? Who is on call before launch?
