# ADR-0008: One Postgres container, one database per service

- **Status:** Accepted
- **Date:** 2026-06-12
- **Source:** PRD design interview (decision log #8, `docs/prd/3commerce/15-appendix.md`)

## Context

Microservices doctrine: each service owns its data exclusively. The question is enforcement strength versus local-ops simplicity ("simple to run" is a stated goal).

## Decision

A single PostgreSQL 17 container hosts **six logical databases, one per service**. Each service has its own DbContext, migration history, and credentials. **No cross-database joins** — when a service needs another's data for display, it maintains a local copy updated via events.

## Alternatives considered

- **Schema-per-service in one DB** — rejected: isolation by discipline only; one desperate cross-schema join starts the rot.
- **Postgres instance per service** — rejected for now: six containers of RAM and migration juggling for zero extra learning; the upgrade is a connection-string change later.
- **Shared tables** — rejected: destroys service ownership entirely.
- **Polyglot (Mongo catalog, Redis cart)** — rejected: Postgres JSONB covers the catalog case; add engines only at a concrete limit.

## Consequences

- NFR-4: no service may hold another service's connection string.
- Event-fed read copies are the standard pattern for cross-service display data (e.g. product names on orders).
- **Update ([ADR-0022](0022-named-schema-per-service.md)):** each service's tables additionally
  live in a **named schema within its own database** (`<service>.*`, not `public`) as
  defence-in-depth. This is database-per-service **plus** a schema namespace — *not* the
  "schema-per-service in one shared database" option rejected above.

## Later changes (recorded 2026-11-27)

The decision stands; two facts in it have moved on:

- **PostgreSQL 18**, not 17 — upgraded in #136 (2026-08-01): `postgres:18` in `docker-compose.infra.yml` and the
  Helm chart (`deploy/helm/3commerce/values.yaml`).
- **Fourteen databases, not six** — one per DB-owning service (13: the six above plus Entity, ADR-0027, and
  Marketing/Pricing/Audit/Workflow/Entitlement/Usage, ADR-0030) plus `notifications_db` for the Notifications
  worker's delivery log (`notifications.Deliveries`). Each still has its own login; `infra/postgres/init-databases.sql` is the list.
