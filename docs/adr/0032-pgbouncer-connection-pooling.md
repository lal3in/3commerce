# ADR-0032: PgBouncer connection pooling before broad app scale-out

- **Status:** Accepted
- **Date:** 2026-06-28
- **Source:** Production platform architecture roadmap (`.ai-shared/plans/production-platform-architecture-roadmap.md`, `pplat_2`)

## Context

The platform now has thirteen DB-owning services, each with its own PostgreSQL database and login (ADR-0008) plus named schemas (ADR-0022) and RLS tenant scopes (ADR-0024). Scaling service replicas horizontally multiplies PostgreSQL backend connections quickly. Npgsql already pools connections inside each process, but app-local pooling does not cap total database connections across many replicas, workers, and rolling deploys.

PgBouncer provides a lightweight Postgres connection-pooling layer between services and PostgreSQL. It should be introduced before broad app replica increases, while keeping migrations and operational runbooks explicit.

## Decision

Add **PgBouncer** as an optional connection-pooling layer for application runtime traffic.

1. Runtime services may connect through PgBouncer on `pgbouncer:6432` when the optional compose overlay/profile is used.
2. EF migration bundles and administrative schema-change paths continue to connect directly to PostgreSQL unless a migration-specific PgBouncer mode is deliberately validated later.
3. The initial pooling mode is **transaction pooling** for runtime services.
4. Tenant isolation remains transaction-scoped: every request or unit of work that needs tenant data must keep using `BeginTenantScopeAsync` / `RunInTenantScopeAsync`, which set `app.tenant_id` with transaction-local scope.
5. PgBouncer is an infrastructure optimization only. It does not change database ownership, service boundaries, RLS, migrations, or cross-service data rules.
6. Production credentials and PgBouncer auth must be provisioned through the environment's secret manager. The committed compose config is dev-only and uses the existing development database passwords on the private Docker network.

## Alternatives considered

- **Rely on Npgsql process-local pooling only** — acceptable for small dev/test deployments, but insufficient as the number of service replicas grows because each process owns its own pool.
- **PgBouncer session pooling** — safest for session state but less effective at reducing backend connection pressure. Keep it as a fallback if transaction pooling reveals an incompatibility.
- **Pgpool-II** — broader feature set than needed here; higher operational complexity for the current goal.
- **Direct app-to-Postgres forever** — simplest, but delays a known production scaling control until connection pressure is already a problem.

## Consequences

- Runtime connection strings gain a PgBouncer override path for compose and future deploy targets.
- Migrations remain direct-to-Postgres, avoiding PgBouncer limitations around schema-management sessions and prepared/session state.
- RLS behavior must be regression-tested through transaction-scoped tenant operations whenever PgBouncer is enabled.
- Operators get a clear switch: direct Postgres for migrations/admin, PgBouncer for runtime app traffic.
- Helm/managed-cloud deployments can either use a managed pooler or add an equivalent PgBouncer sidecar/deployment later; the app contract is only the host/port in the connection string.
