# Observability capability audit (2026-10)

> **Scope:** static audit (code, config, dashboards, scripts, docs) of 3commerce's observability against a
> modern error-monitoring / APM capability list (the kind Sentry, Datadog or Google Cloud Observability sell).
> No production code was changed and nothing was run against Docker. Baseline: `origin/develop` @ `5382988`.
>
> **Status key:** **Have** = captured, correlated and viewable today · **Partial** = some of it, with named
> gaps · **Missing** = nothing in place · **N/A** = the product has no such surface today.

## Scorecard

| # | Capability | Status | Key evidence | Top gap |
|---|---|---|---|---|
| 1 | Errors and exceptions | **Partial** | Server: ASP.NET exception handler + problem+json `traceId` (`BuildingBlocks/Infrastructure/Web/ProblemDetailsExtensions.cs:13-55`); error-level logs → Loki with `trace_id` (`OtelExtensions.cs:67-77`); MassTransit `_error`/`_skipped` queue depth in Mission Control (`Admin/Services/BusStatsService.cs:49-59`) | No error *grouping/issue* view or alerting; browser + SSR errors are not collected at all (no `instrumentation.ts`, no `error.tsx`/`global-error.tsx`); Admin + SupplierPortal Blazor apps export **no** telemetry |
| 2 | Stack traces and local variables | **Partial** | .NET exceptions logged with full stack via `ILogger` → Loki (OTLP logs, `IncludeScopes`) | Stack traces are a log string, not OTel `exception.*` span events; no local-variable capture; storefront ships no source maps and no browser capture; no SourceLink/commit on PDBs |
| 3 | Breadcrumbs | **Partial** | Server-side: per-trace logs (Tempo↔Loki links, `grafana/provisioning/datasources/lgtm.yml`) act as breadcrumbs; consent-gated first-party analytics events (`Storefront/lib/analytics.ts`) | No client breadcrumbs (clicks, navigation, console, fetch) tied to an error; analytics events are business events, not diagnostics, and are not trace-linked |
| 4 | Performance metrics | **Partial** | HTTP RED metrics per service + p95 (`dashboards/services-red.json`); Tempo span-metrics + service graph (`tempo.yaml` `metrics_generator`); Redis/Valkey + stream meters | No DB query spans/durations (EF/Npgsql **not** instrumented despite the comment in `OtelExtensions.cs:17`), no N+1 detection, no runtime (GC/threadpool) metrics, no MassTransit meter, no Apdex/SLOs, no Web Vitals |
| 5 | Distributed tracing | **Partial** | Gateway + 13 services + Notifications worker export traces; MassTransit `ActivitySource` joins message hops to the HTTP trace (`OtelExtensions.cs:37`); W3C propagation over HttpClient | Trace starts at the gateway — browser and Next.js SSR are dark; Admin/SupplierPortal dark; Postgres/Valkey/Kafka client spans missing; no sampling policy |
| 6 | Session replays | **Missing** | Deliberately none — `lib/analytics.ts:1-3` ("no session replay, no keystroke logging") | Not recommended now: PCI SAQ-A + GDPR; if ever added, error-triggered only, mask-all, Stripe iframe excluded |
| 7 | Profiling | **Missing** | No profiler, no `dotnet-monitor`, no Pyroscope | No CPU/allocation profiles for slow transactions (.NET or Node) |
| 8 | Release and deployment data | **Missing** | Resource is `AddService(name)` only (`OtelExtensions.cs:31`, `Gateway/Program.cs:17`) — no `service.version`; Dockerfiles take no build args/labels | Cannot tell which commit/PR/promotion (ADR-0058) produced an error; no release markers in Grafana |
| 9 | Device and environment metadata | **Partial** | Server spans carry `user_agent.original`, `client.address`, `server.address` from ASP.NET instrumentation; Helm injects `ASPNETCORE_ENVIRONMENT` | No `deployment.environment.name`, host/container/k8s resource detectors; no browser/device context; client IPs land in traces with no redaction or retention policy |
| 10 | AI and LLM observability | **N/A** | No LLM SDKs or AI features in `src/` (only a static `/llms.txt` route, `Storefront/app/llms.txt/route.ts`) | Not applicable today — see §10 for what to add if an AI feature lands |

**Production readiness overall:** the LGTM stack is a **dev-local** stack (filesystem storage, no auth,
48h Tempo retention, Grafana `admin/admin` default). Helm ships an opt-in in-cluster mirror
(`observability.enabled=false` in both `values.yaml` and `values-prod.yaml`, emptyDir storage, **no
Grafana**, and its collector lacks the dev Loki-noise filter). There is no test/prod telemetry backend, no
alerting rule anywhere, and `host-check.sh --logs` only tails provider-managed logs when configured.

_Detailed sections, roadmap and open questions follow._
