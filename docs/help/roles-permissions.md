# Users, roles & permissions

Who can sign in and what they can do — accurate to the code. See
[roles-permissions.html](./roles-permissions.html) for the full tables + the admin screen.

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
   internal-claims JWT (`sub · role · sid · tenant · email`).
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
