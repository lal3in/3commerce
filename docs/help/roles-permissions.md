# Users, roles & permissions

Who can sign in and what they can do — accurate to the code. See
[roles-permissions.html](./roles-permissions.html) for the full tables + the admin screen.
For sales or compliance conversations, pair this with [Selling information](./selling-information.md)
so RBAC, service accounts, and MasterGlobal override are positioned with the right caveats.

## Principals (user types)

| Principal | What it is | App | Gate |
|---|---|---|---|
| Customer | Storefront shopper (guest or registered); `customer` role | Storefront `:3000` | CustomerPolicy |
| Operator | Back-office user; `admin` role claim + RBAC roles via the PDP/PEP | Admin `:5200` | AdminPolicy + PDP |
| Supplier | Supplier Portal user; supplier session | Supplier Portal `:5300` | Supplier session |
| Service account | Non-human principal for automation/CLI (ADR-0026); rotatable, revocable | Gateway / CLI | Internal claims · `--tenant` |
| MasterGlobal | Cross-tenant platform operator; may override maker-checker + bypass tenant RLS — audited | Platform | Audited override |

## Authorization pipeline

1. Gateway gets the opaque `3c_session` cookie (ADR-0012).
2. It introspects the session → `(UserId, TenantId, Role, Email)` and mints a short-lived ES256
   internal-claims JWT (`sub · role · sid · tenant · email`, plus `amr` — `pwd` or `pwd otp` — and
   `auth_time` for step-up decisions). A session that is **pending an MFA challenge** fails
   introspection: the gateway mints **no claims** — completing the challenge or logging out are
   the only moves.
3. The service applies the coarse endpoint policy — `CustomerPolicy` (customer|admin) or `AdminPolicy` (admin).
4. For fine-grained action/field checks, the service (PEP) asks the Identity PDP
   (`PolicyDecisionService.DecideAsync`), which resolves effective permissions from memberships → roles →
   permissions (ADR-0025).

## Built-in roles

`admin` (all 24 permissions), `ops`, `finance`, `support`, `merchandiser`, and the system `customer`
role (no back-office permissions). Roles are **data** — operators edit a role's permission set or clone
it; built-in roles can't be deleted. The 24 permissions are **code-defined** (`PermissionRegistry`,
Low/Medium/High risk) and not operator-editable. Manage on the admin **Roles & permissions** screen or
`/api/identity/admin/rbac`.

## Multi-factor authentication (TOTP)

Any account can enroll a **TOTP second factor** (RFC 6238; enrollment issues **8 one-time recovery
codes**, stored hashed and shown once). Enrolled logins return `{ mfaRequired }` and must complete
`POST /api/identity/mfa/challenge` (TOTP or recovery code; wrong codes feed the password lockout
counter). Operators enroll on the Admin **Security** page; storefront login shows a code input when
challenged. Admins set a **tenant MFA policy** (`GET/PUT /api/identity/mfa/policy`); the effective
policy is `max(platform floor Mfa:PlatformMinimum, tenant policy)`. A required-but-unenrolled admin
is nagged, not locked out (the bootstrap admin can't be locked out); a confirmed factor cannot be
reset via the session — that is a support flow.

## Tenant isolation

Tenant-scoped tables in Identity and Entity are protected by PostgreSQL **FORCE row-level security**
(child tables without a `TenantId` are transitively isolated); Catalog/Ordering/Payments/Fulfillment/
Support use deliberate app-level tenant filters (ADR-0024 addendum). Cross-tenant administration is
gated: only a **platform-scope master role** passes `InternalClaimsAuth.CanActForTenant` for a foreign
tenant — admin-user endpoints return 403 for a foreign `tenantId` otherwise, and master-role overrides
are audited.
