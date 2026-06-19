# ADR-0023: Strict multi-tenancy — tenant = legal operator, principals span tenants

- **Status:** Accepted
- **Date:** 2026-06-18
- **Source:** Multi-tenant platform expansion plan (`.ai-shared/plans/multi-tenant-platform-expansion-phase-1-foundation.md`), design grill 2026-06-18.
- **Amends:** [ADR-0006](0006-service-decomposition-capability-seams.md) (single-operator service decomposition), [ADR-0013](0013-guest-checkout-optional-accounts.md) (identity model).

## Context

The MVP assumes one logical operator, one public storefront, and two coarse roles
(`customer`, `admin`). The platform must now run **many isolated businesses** (tenants),
each with multiple storefronts, staff, suppliers, and customers, from one deployment —
without cross-tenant leakage and without a per-tenant deploy.

## Decision

- A **Tenant** is one legal operating business. Tenant-owned rows carry a `TenantId`; global
  tables (the domain registry, the platform `MasterGlobal` operators, the permission registry)
  are the explicit, justified exceptions.
- A **Principal** is one human or machine identity and may hold **memberships** in several
  tenants (a person can be staff in tenant A and a customer in tenant B). Authentication
  resolves a principal; **authorization is always within a selected tenant scope**.
- **Customers are tenant-scoped profiles** linked to a shared principal. Customer email is
  unique **per tenant**, not globally; a customer can sign in to any storefront under that
  tenant. Storefront account views filter by storefront; a tenant-wide view is opt-in (default
  off).
- **Storefronts** belong to a tenant (public/private/password/invite; multiple domains, one
  canonical). Domain → tenant/storefront resolution happens at the Gateway (ADR-0011), which
  mints the trusted tenant/storefront context; browsers/CLI never supply it.
- **MasterGlobal** is the platform operator scope (cross-tenant). At least one active
  MasterGlobal and, per tenant, at least one active **TenantOwner** must exist at all times.
- The architecture is **region-aware** (each tenant has a home region) but ships in one
  physical region; cross-region tenant moves are out of scope.

## Alternatives considered

- **Schema- or database-per-tenant** — rejected at platform scale: thousands of schemas/DBs,
  migration fan-out, and connection sprawl. Row-level isolation (ADR-0024) scales better and
  keeps one migration path per service (named schema per service, ADR-0022, is unchanged).
- **Globally-unique customer email** — rejected: a person must be able to be an independent
  customer of two unrelated tenants. Email is unique per tenant; the principal is the join.
- **Tenant context from the client** — rejected: trusting a header/cookie for tenant identity
  is a cross-tenant escalation. Only the Gateway derives it from the resolved domain/session.

## Consequences

- Every tenant-owned table, message contract, and API flow becomes tenant/correlation/actor
  aware; this is the precondition for all later phases.
- Isolation is enforced in depth: application checks **and** PostgreSQL RLS (ADR-0024).
- Tenancy and authorization stay in **Identity** initially; a dedicated Authz service is a
  later option behind the same PDP seam (ADR-0025).
