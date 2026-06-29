# ADR-0033: PostgreSQL index changes require measured query evidence

- **Status:** Accepted
- **Date:** 2026-06-29
- **Source:** Production platform architecture roadmap (`.ai-shared/plans/production-platform-architecture-roadmap.md`, `pplat_3`)

## Context

PostgreSQL is the primary data platform for all DB-owning services. The system already uses PostgreSQL features such as full-text search, `pg_trgm`, JSONB, constraints, triggers, named schemas, and tenant isolation through RLS. As the platform grows, query latency will matter most on catalog/search, checkout/cart, admin orders/payments, audit/workflow listings, and usage/subscription reads.

Indexes are not free: every extra index costs storage, slows writes, and increases migration/maintenance work. Adding broad or speculative indexes would be especially harmful in this architecture because every service owns its own database and writes often publish outbox messages in the same transaction.

## Decision

PostgreSQL index changes must be **evidence-first**.

1. New partial, expression, multicolumn, GIN, GiST, or trigram indexes require a named query/use case and before/after plan evidence.
2. Evidence must use `EXPLAIN (ANALYZE, BUFFERS)` against seeded or representative data.
3. Query predicates must match the proposed index shape. Expression indexes are allowed only when the application query uses the exact expression or a documented equivalent.
4. Tenant/RLS predicates must be considered part of the query shape. Tenant-scoped hot paths should usually lead with `TenantId` or another tenant/storefront partitioning column when that matches the query.
5. Partial indexes are preferred for common filtered states such as active/published/pending/open records when the predicate is stable and selective.
6. Every index must be created in the owning service's migration, inside that service's named schema. Cross-service database reads remain forbidden.
7. Do not add an index just because a table has a foreign key or a column is filterable. The query must be measured.

## Workflow

Use `scripts/postgres-index-audit.sh` to capture repeatable `EXPLAIN (ANALYZE, BUFFERS)` plans from a running seeded dev stack. Store noteworthy output in the implementation PR description or in a service-specific design note when adding an index.

Minimum review data for each proposed index:

- service/database/schema/table;
- endpoint or worker path that issues the query;
- seeded/representative row counts;
- before plan;
- proposed index DDL;
- after plan;
- write-path impact assessment;
- rollback/removal note if the index proves harmful.

## Alternatives considered

- **Add broad indexes now across likely columns** — rejected. This creates write amplification and maintenance debt without proving user value.
- **Rely only on ORM-generated indexes** — rejected. EF-generated relationship indexes are useful, but hot paths often need domain-specific partial/expression/composite indexes.
- **Defer all index work until production incidents** — rejected. A repeatable audit workflow lets us evaluate query plans before traffic grows, without committing speculative schema changes.

## Consequences

- `pplat_3` establishes the audit workflow and query inventory but does not add speculative schema migrations.
- Future index migrations must cite the measured query and plan evidence.
- The regression command remains unchanged; query-plan capture is an operator/developer workflow that runs against a seeded stack.
- The same evidence rule applies before adopting more advanced Postgres features such as materialized views or partitioning.
