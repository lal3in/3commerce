# Admin UI — backend follow-ups (after PR #14)

PR #14 delivered the achievable admin/storefront UI (items 1,2,3,4,5,7,9-UI,11,12). The three remaining
items are **backend** work and should land as their own PR(s) with tests, not as UI.

## 1. Entity RLS write path (unblocks item 9 + item 10)  — security-critical
**Bug found:** the Entity service has `FORCE ROW LEVEL SECURITY` on its tables but **never applies the
tenant context** for writes, so every insert/update fails as
`new row violates row-level security policy` when connecting as `entity_svc`. Integration tests miss it
because they connect as the Postgres **superuser** (bypasses RLS); bare-run connects as the service role.

**Fix (reuse the tested toolkit in `BuildingBlocks/Infrastructure/Tenancy`):**
- A request middleware that reads the internal claims (`tenant`, `sub`, MasterGlobal) into
  `ITenantContextAccessor.Current` (the AsyncLocal accessor already exists).
- Apply the context per unit of work — either wrap endpoint DB work in `DbContext.RunInTenantScopeAsync`
  (the pattern Identity's `AuthService` already uses) **or** add a `SaveChangesInterceptor` that runs the
  `set_config('app.tenant_id'/'app.principal_id'/'app.is_platform_admin', …, is_local=>true)` SQL inside
  the save transaction. Prefer the interceptor so endpoints don't each have to wrap.
- Register the accessor + middleware in the Entity `Program`. Add an integration test that connects as the
  **service role** (not superuser) so RLS is actually exercised.
- Audit the other FORCE-RLS services (Identity does wrap; check Catalog/Ordering/etc.) for the same gap.

## 2. Master-admin user management (item 8)
New Identity admin endpoints (MasterGlobal-gated), then a small Admin UI:
- `POST /api/identity/admin/users/{id}/reset-password` (issues a reset / sets a temp password).
- `PUT  /api/identity/admin/users/{id}/email` (change the admin user's email; re-verify).
- List admin users. Note: admin accounts are managed by the global master (ADR-0026 MasterGlobal).

## 3. Commerce Ops fully functional (item 10)
Audit the existing `/api/catalog/admin/storefronts*` + offer/pricing/payment-account/payable endpoints;
wire the page so every advertised action works end-to-end (needs the RLS fix above for any tenant-scoped
writes). Add missing endpoints (e.g. payment-account lifecycle) as needed.

## 4. Full CRUD sweep (item 6)
Per-page create/edit/delete where the **domain allows**. Deliberately read-only by design: the **ledger**
(append-only, ADR-0014 — reverse, don't edit/delete) and the **audit** projection (append-only,
hash-chained). For the rest (orders/RMA/Xero/etc.), add the missing admin write endpoints + forms.
