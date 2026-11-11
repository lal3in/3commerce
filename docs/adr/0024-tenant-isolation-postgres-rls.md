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

- Enable **PostgreSQL Row-Level Security** on every tenant-owned table, **with `FORCE ROW LEVEL
  SECURITY`** — the service connects as the database/table *owner* (e.g. `identity_svc` owns
  `identity_db`), and an owner bypasses RLS unless it is forced. Policies use a session GUC:
  `USING ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid)` with a matching
  `WITH CHECK` on writes. No tenant context set → `current_setting(..., true)` is null → no rows
  (fail closed). Service roles must be **non-superuser and non-`BYPASSRLS`** (superusers and
  `BYPASSRLS` roles ignore RLS entirely).
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
- **Secret-keyed tables are exempt by design.** Rows accessed by an unguessable cryptographic
  secret rather than by tenant — Identity's `Sessions` and `EmailTokens` (looked up by a global
  `TokenHash`, with the tenant unknown at lookup time) — are isolated *cryptographically*, not by
  RLS, and carry no `TenantId`. Identity applies FORCE RLS to `Users` (tenant-owned PII); its
  `AuthService` runs each operation under the right scope (tenant scope for register/login/
  reset-request; **platform scope** for the cross-tenant, secret-keyed introspect/verify/
  confirm-reset paths). A non-superuser test (`IdentityUsersRlsTests`) proves this end to end,
  since superuser-connected tests and the anonymous CI smoke jobs would not catch an RLS break.

## Addendum (2026-07-04, review remediation rev_8): coverage expansion + app-level-filter posture

**RLS coverage now:** Identity `Users` and `Addresses` (the Addresses policy has shipped since
`AddressTenantScope` — the mt1_4 note calling it deferred was stale; the actually-missing piece was
the dedicated non-superuser test, now `IdentityAddressesRlsTests`); every tenant-scoped Entity
table with a `TenantId` column (`Entities` since aui_9, plus `EntityRelationships`,
`DuplicateWarnings`, `SupplierOnboardings`, `SupplierChangeRequests`, `CustomerEntityLinks` via
`EntityTenantTablesRls`). Children keyed only by their RLS'd parent (`EntityProfiles`,
`EntityAddresses`, `EntityIdentifiers`, `EntityContactMethods`) carry no `TenantId` and are
deliberately un-policied — isolation is transitive through the parent.

**Recorded posture — app-level filtering elsewhere.** Catalog, Ordering, Payments, Fulfillment, and
Support intentionally rely on application-level tenant filtering (queries carry `TenantId`
predicates; admin surfaces are gateway-authenticated), NOT database RLS. Rationale: these services'
write paths are dominated by bus consumers and sagas that run outside a per-request tenant scope,
so FORCE RLS would require scoping every consumer transaction for marginal benefit while their
data is commerce state rather than direct PII (the PII-bearing stores — Identity, Entity — are the
RLS'd ones). Revisit trigger: any of these services gaining direct-PII tables, or tenant-facing
self-service query surfaces that bypass the gateway's tenant claims.

**Cross-tenant admin authorization** is a separate layer: admin endpoints taking an explicit
`tenantId` must verify it against the caller's `tenant` claim unless the caller holds the
platform-scope `master` role (`InternalClaimsAuth.CanActForTenant`, first applied to the
master-admin user endpoints — rev_9).

## Amendment (2026-11-07, mem_2): Payments' saved cards go under FORCE RLS

mem_2 gives each `payments."SavedPaymentMethods"` row its own billing address (name + street + city + region +
postcode + country). That is the first direct PII in Payments, which is the revisit trigger named above. **Owner
decision 2026-11-07 (final): the table goes under FORCE ROW LEVEL SECURITY like every other tenant-owned PII store.
App-level owner scoping alone is not enough.** The first draft proposed keeping app-level filtering and was rejected.

- **Policy.** Migration `SavedPaymentMethodsRls` enables and FORCEs RLS. It uses the same `TenantIsolation_*` policy
  shape as Entity's: platform scope bypasses, and an empty `app.tenant_id` reads as NULL, so it matches no row. The
  service connects as `payments_svc`, which owns the table and is non-superuser and non-`BYPASSRLS`
  (`infra/postgres/init-databases.sql`); that is why the policy must be FORCEd.
- **Every reader and writer runs in a tenant scope** through `Payments.Infrastructure.SavedCardTenantScope`:
  - **Customer endpoints** (`/payment-methods` list / save / remove / make-default / billing-address): the tenant
    comes from the request's `tenant` claim. The work, including its `SaveChanges`, runs in a tenant-scoped
    transaction. A card saved by `POST` is written in its payment customer's tenant.
  - **`SubscriptionService.PayAsync`** (the customer paying a past-due renewal on another saved card): the tenant
    comes from the call. Off-session renewals and dunning charge the instrument already pinned on the subscription
    and never read this table.
  - **`AuthorizePaymentConsumer`** (checkout's saved-card charge): `AuthorizePayment` carries no tenant, so the
    consumer uses the tenant on the member's `PaymentCustomer` row. Every saved card hangs off that row, and with no
    customer there is no card. The consumer already runs inside MassTransit's EF outbox transaction, so the scope
    applies the transaction-local settings to that transaction instead of opening a second one. They end with it,
    so a pooled connection still never carries them on.
  - The EF InMemory provider in unit tests has no RLS and no transactions, so there the work simply runs.
- **Pending work on other branches must follow this.** Any new reader, such as the usage auto-load charge on a saved
  card (`feature/usage-autoload-saved-card`), must go through `SavedCardTenantScope` with the tenant from its message
  or row. Otherwise it reads nothing under FORCE RLS.
- **Proof.** `SavedPaymentMethodsRlsTests` connects as a non-superuser role and applies the real migrations. The
  owning tenant writes, reads and updates a card. Another tenant can neither read, update nor insert for it. With no
  scope a read is empty and an insert is rejected. Platform scope reads. Inside an open transaction the scope applies
  to that transaction, and it is gone after the commit. Superuser-connected fixtures, such as Phase3's, still do not
  exercise RLS; the live run against the dev stack (`payments_svc`) does.

`PaymentCustomers` (provider customer references, no address) stays app-level filtered for now. The billing address
never enters the charge path: `SnapshotForCharge` does not carry it, and a unit test pins that.

