# 0065 — Production telemetry: self-hosted LGTM, collector 0.165, security-log retention, email alerting and anonymised browser RUM

Status: Accepted — implemented (phase 1 code + local binary validation; live stack verification in the PR)
Area: Observability / deployment / privacy
Extends: [0029](./0029-phase-6-compliance-ops-primitives.md) (SLOs), [0064](./0064-release-identity-on-telemetry.md) (release identity), [0044](./0044-self-hosted-redis-cache-ratelimit-idempotency.md) (Valkey metrics)
Source: observability capability audit (`docs/reviews/observability-capability-audit.md`) roadmap **P0-4, P0-5, P1-7, P2-5** and open questions **Q1–Q8** — owner decisions 2026-11-15 (final)

## Context

The audit found telemetry that is solid in dev and absent everywhere else: no production backend (P0-5), no alert
rule anywhere (P0-4), no sampling policy (P1-7), no Kubernetes resource attributes (P2-5), and eight open owner
questions. The collector was pinned at contrib 0.110.0 (2024-09). PR #303 had added the `transform/redact`
processor and explicit retention (Loki 30 d, Tempo 48 h dev / 14 d Helm, Mimir 30 d dev / 13 months prod).

## Owner decisions (2026-11-15, final)

| Q | Decision |
|---|---|
| Q1 backend | **Self-hosted LGTM**: the upstream Loki / Tempo / Mimir Helm charts on object storage, plus Grafana, operated by us. No managed Grafana Cloud. |
| Q2 retention | Traces **14 d**, logs **30 d**, metrics **13 months**; **security logs** (authentication, permission denied) **12 months**. The business audit trail stays in the Audit service. |
| Q3 browser consent | Browser error/performance telemetry is **strictly necessary and anonymised**: no consent gate, **no identifiers, no IP, no replay**. |
| Q4 replay | **No session replay** (P2-6 is closed, not deferred). |
| Q5 triage | **GlitchTip**, built later by another agent (not here). |
| Q6 Kafka | Stream envelope carries `traceparent` — another agent. |
| Q7 deps | Npgsql/EF OTel, Grafana Faro, `@vercel/otel` and Pyroscope **approved**. |
| Q8 routing | Alerts go by **email**. The address is not known yet: it is **configuration only** (`ALERT_EMAIL_TO` + SMTP settings), no real address is committed. |
| — | Run the **latest otel-collector-contrib**: **v0.165.0** (released 2026-11-07). |

## Decision

### 1. One collector, latest contrib, identical processors in dev and Helm

`otel/opentelemetry-collector-contrib:0.165.0` in `docker-compose.yml` and Helm `observability.collector.image`.
Every config is validated with that exact binary (`otelcol-contrib validate`, plus a local run against sample
payloads with a debug exporter: zero deprecation warnings). The shared processors (`transform/faro_anonymise`,
`transform/redact`, `filter/loki_noise`, `transform/security_category`, `groupbyattrs/log_category`) are verbatim
the same in `deploy/observability/otel-collector-config.yaml` and `deploy/helm/3commerce/templates/observability.yaml`
— the Helm copy now also carries the dev noise filter it lacked (audit P0-5).

Breaking/deprecated since 0.110 and fixed here:

- Component ids are snake_case: exporters `otlp` → `otlp_grpc`, `otlphttp` → `otlp_http`,
  `prometheusremotewrite` → `prometheus_remote_write`; processors `k8sattributes` → `k8s_attributes`,
  `resourcedetection` → `resource_detection`. The old ids still load as deprecated aliases (warning per component).
- `resource_to_telemetry_conversion` is deprecated in both Prometheus exporters → `resource_constant_labels:
  {included: ["*"]}` (same labels, so dashboards keep `service_name`, `deployment_environment_name`, …).
- OTTL paths need their context prefix (`log.body`, `log.attributes`, `span.attributes`, `resource.attributes`);
  unprefixed paths are rewritten with a warning. `transform` statements are now flat lists (context inferred).
- `filter` processor: `logs.log_record` → `log_conditions`.
- Changed defaults that matter: the Faro translator emits `deployment.environment.name` (not the v0
  `deployment.environment`); `k8s_attributes` emits the v1 k8s semantic conventions; the Prometheus exporters'
  `translation_strategy` replaces `add_metric_suffixes` (default unchanged: underscore + suffixes, so
  `http_server_request_duration_seconds_*` keeps its name).
- `health_check` extension added (`:13133`) — host-check.sh already probed it; it now answers.

Loki 3.3.2, Tempo 2.6.1, Mimir 2.14.1 and Grafana 11.2.0 (dev) need **no** bump for this collector: OTLP/HTTP into
Loki, OTLP/gRPC into Tempo and Prometheus remote-write 1.0 into Mimir are unchanged; Loki 3.3 already has
`otlp_config` and `retention_stream`. They stay pinned. Production runs the newer images of the upstream charts.

### 2. Production backend (Q1, P0-5): the self-hosted LGTM install set

`deploy/helm/observability/` holds a **documented install set**, not chart dependencies (a dependency would make
`helm lint`/kind-deploy download four large charts and couple their lifecycles to the app's):

| Release | Chart (pinned) | Mode |
|---|---|---|
| `loki` | grafana-community/loki 21.3.1 (Loki 3.7.10) | Monolithic, S3, multi-tenant, gateway |
| `tempo` | grafana-community/tempo 3.2.1 (Tempo 3.1.1) | single binary, S3, multi-tenant, metrics-generator → Mimir |
| `mimir` | grafana/mimir-distributed 6.2.2 (Mimir 3.2.3) | upstream default (ingest storage), S3, no Mimir alertmanager/ruler |
| `grafana` | grafana-community/grafana 14.5.0 (Grafana 13.3.1) | repo dashboards + alert rules mounted from ConfigMaps |

(`grafana/grafana` and `grafana/tempo-distributed` are deprecated upstream; the maintained charts live in the
grafana-community repo.) `install-lgtm.sh` installs them into namespace `observability` (`--template` renders all
four offline-of-cluster for review); S3 endpoint/region come from `S3_ENDPOINT`/`S3_REGION`, credentials from the
Secret `lgtm-object-storage` or workload identity. The app chart selects the backend with
`observability.backend: inChart | external`: `inChart` keeps the single-binary emptyDir stack for dev/kind;
`external` (values-prod.yaml, now `observability.enabled: true`) exports to `observability.external.*` with
`X-Scope-OrgID: 3commerce`. Services keep exporting to the in-chart collector (`observability.otlp.endpoint`,
optional `headersSecret` → `OTEL_EXPORTER_OTLP_HEADERS`). `deploy/.env.dev` re-enables OTLP for containerized dev.

### 3. Retention and the 12-month security-log stream (Q2)

Collector `transform/security_category` sets the log attribute `log.category=security` on records whose ILogger
category (the OTLP instrumentation scope) is an authentication/authorization one — Identity's auth flows
(`AuthService`, MFA, session, password, lockout, step-up, token), `BuildingBlocks…Authz.*` (the PermissionGate PEP,
#326), the gateway's `Auth.*` and `RateLimiting.*`, the Admin `IpAllowlistMiddleware`, SupplierPortal auth, and
`Microsoft.AspNetCore.Authentication/Authorization`. A service may set `log.category=security` itself (kept).
`groupbyattrs` lifts it onto the resource; Loki `otlp_config` indexes it as the stream label `log_category`, and
`retention_stream {log_category="security"}` keeps it **8760h** while everything else keeps `retention_period`
720h. Same in dev `loki.yaml`, the in-chart Loki and `values-loki.yaml`. Tempo 14 d (`retention.traces`), Mimir
395 d in prod.

**Known gap (owned by the .NET instrumentation agent):** today Identity logs no failed login/lockout and the
PermissionGate logs only "PDP unavailable" (denials are audited, not logged), and the gateway exports no OTLP logs
(audit P0-3). The stream labels whatever those categories log; the log lines themselves belong in `src/`.

### 4. Alerting (P0-4, Q8)

Grafana-managed rules provisioned from `deploy/observability/grafana/provisioning/alerting/rules.yml` (dev mounts the
folder; production mounts the same files via the `grafana-alerting` ConfigMap — one source): 5xx rate > 1 % per
service over 5 min (ADR-0029, ignoring near-idle services), catalog read p95 > 500 ms and other reads p95 > 800 ms
(ADR-0029/NFR-5), messages in any MassTransit `_error` queue (RabbitMQ per-queue metrics arrive with P1-5 on another
branch; no data = OK until then), error-log spike (> 20 error/critical records per service in 5 min), Valkey down
(`redis_up`) and Valkey unreachable from services (`redis_unavailable_total`), and the collector heartbeat (its own
`otelcol_process_uptime` pushed through itself into Mimir — absent 5 min = down). One email contact point,
`addresses: $ALERT_EMAIL_TO`; SMTP from `GF_SMTP_*` (compose: `GRAFANA_SMTP_*`, default off; prod: Secret
`grafana-alerting`). No address is committed (dev default `alerts@example.invalid`). Each rule links its section of
`docs/runbooks/observability.md`.

### 5. Sampling (P1-7)

Helm collector `tail_sampling` (on in values-prod.yaml): keep every trace with an ERROR span, every trace slower
than 800 ms (the ADR-0029 read p95), every trace touching a checkout/payment/refund route (`http.route`, `url.path`
or span name), and 15 % of the rest; dev and kind keep 100 % (no sampler). Tail sampling needs all spans of a trace
on one collector, so `replicas: 1` — scale-out needs a load-balancing exporter tier keyed on trace id. Tempo's
service-graph/span-metrics are post-sampling; the SLO alerts use the SDK's unsampled HTTP metrics. The SDK
`ParentBased(TraceIdRatio)` guard is **not** added: every trace already passes one collector, and BuildingBlocks
telemetry is owned by the .NET instrumentation agent (left to it if head sampling is ever wanted).

### 6. Kubernetes resource attributes (P2-5)

Helm collector `k8s_attributes` (namespace-scoped Role: pods + replicasets in the release namespace; pod, uid,
deployment, namespace, node) on the OTLP input pipelines only, and `resource_detection` (`env` by default, cloud
detectors per deployment; `observability.collector.clusterName` → `k8s.cluster.name`), `override: false`.

### 7. Browser RUM receiver (Q3, P1-2 server side)

A `faro` receiver on `0.0.0.0:8027` (contrib **alpha** for logs and traces at 0.165.0) feeds `traces/faro` and
`logs/faro` pipelines that run `transform/faro_anonymise` — dropping `user_*`/`session_*`/`geo_*` keys from the logfmt
body, `user.*`/`session.*`/`client.*`/`*.ip`/`network.peer.*`/user-agent attributes, and query strings from page and
request URLs — then hand off through `forward` connectors to the main pipelines, so `transform/redact`, security
labelling and tail sampling apply to browser and server data as one trace. The storefront relays from its
same-origin `/rum` route server-side to `http://otel-collector:8027/` (compose/k8s) or `http://localhost:8027/`
(bare-run): no browser reaches the collector and no CORS is configured. Port 8027 is published in dev and is a
ClusterIP-only Service port in Helm (never on the ingress).

## Scope left out

GlitchTip (Q5), Kafka `traceparent` (Q6), .NET instrumentation (EF/Npgsql, gateway logs, security log lines), the
storefront Faro SDK + `/rum` route + `@vercel/otel`, RabbitMQ metrics (P1-5) and Pyroscope — other branches.
Grafana SSO/ingress for production Grafana (reached by port-forward until then).

## Consequences

- Production has a telemetry backend and alerts; nothing is committed that names a real person or address.
- The collector must be validated with the pinned binary on every config change; aliases removed in a future
  release would otherwise fail at boot.
- Running Mimir's ingest-storage Kafka and Loki/Tempo on object storage is now ours to operate (runbook).
- `doctor.sh`, `host-check.sh` and the infra readiness probe check the collector's health endpoint, the Faro
  receiver and the count of provisioned alert rules.
