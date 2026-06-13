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
