# ADR-0025: Central PDP + service-side PEP, field/action policy, dynamic admin-defined RBAC

- **Status:** Accepted
- **Date:** 2026-06-18
- **Source:** Multi-tenant platform expansion plan (phase 1 foundation), design grill 2026-06-18.
- **Amends:** [ADR-0012](0012-custom-auth-opaque-cookie-internal-claims.md) (auth: opaque cookie at edge, signed claims internally).

## Context

The MVP authorizes with two hard-coded policies (`Customer`, `Admin`) checked via internal
claims (ADR-0012). The platform needs **tenant-scoped, fine-grained** authorization: which
*actions* a principal may take and which *fields* they may see/edit/reveal, with maker-checker
on sensitive changes — and operators must be able to **define their own roles** without a code
release.

## Decision

- **Policy Decision Point (PDP)** lives in Identity/Authz. Services are **Policy Enforcement
  Points (PEP)**: they call the PDP (batched per request) for action + field decisions and
  enforce the result server-side. A BuildingBlocks PEP client/endpoint-filter does the call,
  masks/strips disallowed output fields, rejects disallowed input fields, and emits
  sensitive-read audit hooks.
- **Permissions are code-defined; roles are data.** Every enforceable action/field permission
  **self-registers in a permission registry** at startup (so permissions are discoverable and
  type-checked). **Roles are admin-defined data** that map to any subset of registered
  permissions — fully dynamic RBAC, no code change to add a role. A role may never grant a
  permission absent from the registry. `customer` stays a built-in role; staff roles are
  tenant-scoped data.
- **Default role catalog, then editable.** Each tenant is **seeded with a default set of roles**
  (e.g. Admin, Ops, Finance, Support, Merchandiser, + built-in Customer), each pre-mapped to a
  curated default permission set drawn from the registry. From there operators **add/remove
  individual permissions** on any role and **clone (duplicate) a role into a new role** to use
  it as a starting template — all as data. The seed gives a working baseline on day one; the
  registry bounds what any role (seeded or custom) may reference. Built-in roles
  (e.g. `customer`, the last-resort tenant-owner role) are protected from deletion/lockout
  (the ≥1 active TenantOwner invariant, ADR-0023, still holds).
- **Effective permissions** ride in the internal signed claims (ADR-0012). On a role or
  membership change, affected sessions are **invalidated / re-evaluated** so a revoked
  permission takes effect promptly, not only at next login.
- **Field-level decisions** are first-class: visible / editable / masked / reveal-with-reason,
  plus per-action **approval-required**, **risk level**, and **sensitive-read** flags, each
  decision carrying an id + policy version.
- **Fail closed:** if the PDP is unavailable, high-risk decisions deny; only explicitly
  cacheable low-risk reads may use a short cached decision (and log that they did).
- **Maker-checker:** finance/identity-sensitive changes require a *different human* approver
  (requester cannot approve; service accounts cannot approve). MasterGlobal may bypass with a
  mandatory reason + audit. The PDP owns the *policy* (is approval required?); Workflow owns
  the *orchestration* and the owning service applies the pending change (Phase 6).
- **UI/CLI capability metadata is advisory only** — it hides what you cannot do for UX, but the
  service re-decides and enforces every action/field server-side.

## Alternatives considered

- **Keep coarse `Customer`/`Admin`** — rejected: cannot express per-tenant staff scopes,
  field masking, or maker-checker.
- **Fixed (locked) code-defined role catalog** — rejected: operators explicitly want to compose
  their own roles. We instead ship the default catalog as *editable seed data* (add/remove
  permissions, clone-as-template) and keep only the *permissions* code-defined for safety.
- **Embed an external engine (OPA/OpenFGA) now** — deferred: start with an in-platform policy
  engine over ASP.NET authorization + the registry; keep a seam to adopt OPA/OpenFGA later.
- **RLS as the authorization layer** — rejected: RLS isolates rows by tenant (ADR-0024); it
  does not do field/action/role policy.

## Consequences

- Every sensitive endpoint goes through the PEP path; "UI-only" authorization or masking is a
  defect.
- The PDP call is on the hot path → decisions are batched per request and cached only where
  explicitly safe; over-chatty PDP calls are an anti-pattern.
- Role/membership changes must invalidate claims; the permission registry is the single source
  of truth for what roles may reference.
