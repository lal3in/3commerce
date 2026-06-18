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
