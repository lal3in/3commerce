# ADR-0024: Tenant isolation via PostgreSQL RLS with transaction-scoped `SET LOCAL`

- **Status:** Accepted
- **Date:** 2026-06-18
- **Source:** Multi-tenant platform expansion plan (phase 1 foundation).
- **Builds on:** [ADR-0023](0023-strict-multi-tenancy.md) (strict multi-tenancy), [ADR-0022](0022-named-schema-per-service.md) (named schema per service).

## Context

Application-layer `WHERE TenantId = @tenant` filters are easy to forget on one query, and a
single miss is a cross-tenant data breach. We want a database-enforced backstop that fails
closed even when application code is wrong.

## Decision

- Enable **PostgreSQL Row-Level Security** on every tenant-owned table. Policies use a session
  GUC: `USING (tenant_id = current_setting('app.tenant_id')::uuid)` with a matching
  `WITH CHECK` on writes. No tenant context set → no rows (fail closed).
- The tenant context is set **per transaction** with `SET LOCAL app.tenant_id = …` (plus
  `app.principal_id` and `app.is_platform_admin`), executed at the start of the unit of work.
  `SET LOCAL` is rolled back at transaction end, so a **pooled connection cannot leak** one
  tenant's context into the next request. Plain `SET` is forbidden.
- A BuildingBlocks EF helper opens the transaction, applies the `SET LOCAL`s from the ambient
  tenant context (ADR-0023), and runs the work; the DbContext interceptor refuses to execute
  tenant-owned queries with no context outside an explicit platform-admin path.
- **MasterGlobal bypass** is explicit and rare: `app.is_platform_admin = true` enables a
  permissive policy branch, is never the default, and every high-risk cross-tenant read/write
  it performs is reason-tagged and audited (ADR-0025, Phase 6).
- RLS lands in **Identity first** (the tenancy authority); a migration template carries the
  same pattern to each service as it becomes tenant-aware.

## Alternatives considered

- **Application filters only** — rejected: no backstop; one missing predicate leaks tenants.
- **Connection-per-tenant / role-per-tenant** — rejected: defeats pooling and explodes role
  management; the GUC approach keeps one pooled role per service.
- **`SET` (session) instead of `SET LOCAL`** — rejected outright: pooled connections would
  carry a stale `app.tenant_id` into another tenant's request. This is the central RLS hazard.

## Consequences

- RLS is **defense in depth, not a replacement** for authorization: it isolates *rows by
  tenant*; it does not do field/action/role policy — that is the PDP/PEP layer (ADR-0025).
- All data access must run inside the tenant-context transaction helper; ad-hoc `DbContext`
  use that skips it is a review red flag.
- Connection-pool safety is covered by a dedicated isolation test (set tenant A, run, return
  connection, assert tenant B cannot see A's rows).
