# ADR-0022: Named schema per service (within each service database)

- **Status:** Accepted
- **Date:** 2026-06-18
- **Amends:** [ADR-0008](0008-database-per-service-single-postgres.md) (one database per service)

## Context

ADR-0008 gives each service its own **database** and **login** in a single Postgres
container, and it explicitly *rejected* "schema-per-service in one shared database"
(isolation by discipline only — one cross-schema join starts the rot). This ADR does **not**
revisit that: database-per-service and login-per-service stand. What it adds is *defence in
depth* — each service's tables live in a **named schema inside its own database** (e.g.
`identity.*`, `ordering.*`) rather than `public`. The rejected option was many services
sharing one database via schemas; this is one named schema **per already-isolated database**.

## Decision

Every service sets `modelBuilder.HasDefaultSchema("<service>")`, so **all** of its tables —
domain tables **and** the MassTransit transactional outbox, inbox, and saga-state tables —
are created in the service's named schema. `public` holds only database-level objects:
the EF `__EFMigrationsHistory` table (metadata) and the `citext`/`pg_trgm` extensions.

Implementation rules (any service that owns a database must follow these):

- **`HasDefaultSchema("<service>")`** in `OnModelCreating` (before the `AddInboxStateEntity`/
  `AddOutboxMessageEntity`/`AddOutboxStateEntity` calls).
- **`MigrationsHistoryTable("__EFMigrationsHistory", "public")`** on `UseNpgsql` — EF creates
  the history table before any migration runs, i.e. before the schema exists, so it must live
  in `public`.
- **`PostgresLockStatementProvider(enableSchemaCaching: false)`** on the EF outbox and on each
  saga's `EntityFrameworkRepository` (see the gotcha below).
- **Raw SQL must schema-qualify** every table reference (the EF query pipeline qualifies
  automatically; raw SQL does not). This covers the Catalog FTS provider and the Payments
  ledger trigger functions, which use `catalog."Products"` / `payments."JournalLines"`.
- **Clean migrations only** — generate the schema baked in (`EnsureSchema` + `CreateTable` in
  the named schema). Never relocate existing `public` tables with a `SET SCHEMA` / `RenameTable`
  move: it corrupts the MassTransit `OutboxMessage` identity sequence.

## The MassTransit gotcha (why naïve attempts fail)

MassTransit's `SqlLockStatementProvider` reads the table schema from the EF model
(`entityType.GetSchema()`, which `HasDefaultSchema` populates) **but caches the result in a
static map keyed by the shared `OutboxState`/saga types**. In production each service is its
own process, so the cache only ever sees one schema — correct. But the **in-process
integration tests** run all six services in one process, so the first service to query caches
its schema and the others inherit it, producing errors like *"relation `ordering.OutboxState`
does not exist"* logged against the **Catalog** host. Setting `enableSchemaCaching: false`
resolves the schema from the model on every call and fixes it; the per-call cost is negligible.

## Alternatives considered

- **Leave tables in `public`** (the prior state) — works, but the named schema is cheap
  defence-in-depth and makes ownership explicit at the SQL level.
- **Keep the MassTransit plumbing in `public`, only domain tables in the schema** — simpler,
  but leaves outbox/inbox/saga tables in `public`; rejected once `enableSchemaCaching: false`
  made full isolation work.
- **`SET SCHEMA` move migration** — rejected: corrupts the outbox identity sequence and is
  fragile around EF's `schema=null` resolution.
- **Connection `search_path`** instead of qualifying — rejected: interacts badly with the
  deferred ledger trigger at COMMIT and with multi-host test processes.

## Consequences

- Stronger isolation than db+login alone; a stray unqualified cross-service query fails loudly.
- New raw SQL must be schema-qualified (build-time discipline, surfaced by the FTS/ledger code).
- The `enableSchemaCaching: false` requirement is a real footgun for anyone adding a service —
  captured here and in `AGENTS.md`.
- Verified by the full 35-test integration suite and the containerized launch (compose + kind).
