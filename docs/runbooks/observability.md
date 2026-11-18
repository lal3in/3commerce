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

Tenant: production backends are multi-tenant; everything is written and read with `X-Scope-OrgID: 3commerce`.

## Health checks

```bash
scripts/doctor.sh                 # local: collector health, Faro receiver, Grafana, provisioned alert rules (expect 8)
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

Changing the alert recipient: update `ALERT_EMAIL_TO` in the `grafana-alerting` Secret (dev: the env var) and restart
Grafana — provisioning re-reads it at start. Test it from Grafana → Alerting → Contact points → *ops-email* → Test.

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
(the RED *Running releases* table shows which `service_version` started failing → roll back).

### p95 latency
Catalog reads > 500 ms, other reads > 800 ms for 10 min. Find the route in the alert label, open a slow trace in
Tempo (latency > threshold — prod keeps every one of them via tail sampling), look for the long span (DB query,
downstream HTTP, lock wait). Check DB load and the PgBouncer pool (ADR-0032) if many routes are slow at once.

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
