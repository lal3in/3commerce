# ADR-0009: Local dev = bare `dotnet run` per service; containers for infra only

- **Status:** Accepted
- **Date:** 2026-06-12
- **Source:** PRD design interview (decision log #9, `docs/prd/3commerce/15-appendix.md`)

## Context

The inner dev loop (edit → run → debug) matters more than production realism while building. Six services + Postgres + RabbitMQ can be orchestrated many ways locally.

## Decision

Services run bare (`dotnet run` each, full debugger); only **Postgres and RabbitMQ** run in containers via `docker-compose.infra.yml`. Dockerfiles per service are maintained (CI build-verified) so the later container/k8s step stays cheap. **Kubernetes is deferred** to a post-MVP deployment-learning phase.

## Alternatives considered

- **.NET Aspire** — recommended in the interview (dashboard, F5 multi-service debug, deploy artifacts), declined in favor of the simplest possible loop.
- **Docker Compose for everything** — rejected: slower C# debug loop inside containers.
- **Local k8s day one** — rejected: slowest loop; manifests fight domain design.

## Consequences

- Observability comes from OpenTelemetry wiring, not an orchestrator dashboard.
- Revisit (possibly Aspire or compose) if "N terminals" friction grows; this ADR is cheap to supersede.

## Later changes (recorded 2026-11-27)

The bare-run loop stands (`scripts/dev-up.sh` → `scripts/run-all.sh`); two facts have moved on:

- **More infra runs in containers than Postgres and RabbitMQ.** `docker-compose.infra.yml` adds Valkey (ADR-0044)
  and, in the `portals` profile, Kafka + Kafka UI (ADR-0034), pgAdmin, redis-exporter and GlitchTip (ADR-0067); the
  OTel collector, Prometheus, Grafana, Loki/Tempo/Mimir and Pyroscope are borrowed from `docker-compose.yml`
  (ADR-0065/0068). The whole set is started and stopped all-or-nothing by `scripts/lib/infra.sh`.
- **Kubernetes is no longer deferred** — the containerized path (compose → Helm chart `deploy/helm/3commerce`, CI
  `kind-deploy`) landed under [ADR-0021](./0021-containerized-launch.md), alongside, not instead of, bare-run dev.
