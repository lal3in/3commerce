# Feature: Multi-Tenant Platform Expansion — Phase 1 Foundation

Parent plan: `.ai-shared/plans/multi-tenant-platform-expansion.md`

## Feature Description

Build the foundation for strict multi-tenancy, Identity/Authz, tenant context propagation, PostgreSQL RLS, domain/storefront resolution, Admin context, and installable CLI scaffolding. This phase must complete before broad Catalog/Ordering/Payments/Fulfillment rewrites.

## User Story

As a MasterGlobal/platform administrator
I want tenants, principals, roles, policies, service accounts, domain resolution, and CLI/Admin foundations
So that later services can safely enforce tenant isolation and scoped permissions.

## Problem Statement

The current platform has coarse `admin`/`customer` claims and no tenant context. Adding multi-storefront features without a foundation would create cross-tenant leakage and inconsistent authorization.

## Solution Statement

Extend Identity/Authz as the central PDP and tenant/principal authority; add service-side PEP helpers, RLS transaction context, Gateway domain resolution, Admin tenant context, and CLI skeleton.

## Feature Metadata

**Feature Type**: New Capability / Foundation  
**Estimated Complexity**: High  
**Primary Systems Affected**: Identity, Gateway, Admin, BuildingBlocks.Infrastructure, BuildingBlocks.Contracts, new CLI, tests, docs/adr, docs/api  
**Dependencies**: PostgreSQL RLS, EF Core/Npgsql transaction handling, ASP.NET authorization, YARP, existing session/internal claims auth.

---

## CONTEXT REFERENCES

### Relevant Codebase Files IMPORTANT: YOU MUST READ THESE FILES BEFORE IMPLEMENTING!

- `AGENTS.md` - repo rules, named-schema rules, ADR/API docs obligations.
- `Directory.Build.props` - .NET 10, nullable, warnings-as-errors, root namespace mapping.
- `Directory.Packages.props` - central package management and MassTransit 8.x pin.
- `docs/reference/api.md` - minimal API endpoint conventions.
- `docs/prd/3commerce/06-architecture.md` - service boundaries and outbox rules.
- `docs/prd/3commerce/09-security-configuration.md` - current auth/session/internal-claims model.
- `src/Services/Identity/Api/Program.cs` - Identity service registration pattern.
- `src/Services/Identity/Infrastructure/IdentityDbContext.cs` - schema/index/outbox pattern.
- `src/Services/Identity/Api/Endpoints/AuthEndpoints.cs` - endpoint and DTO pattern.
- `src/BuildingBlocks/Infrastructure/Auth/InternalClaimsAuth.cs` - current auth policies to extend.
- `src/Gateway/Program.cs` - rate limiting, YARP, session middleware.
- `src/Admin/Program.cs` - Admin auth and GatewayClient pattern.

### New Files to Create

- `src/BuildingBlocks/Infrastructure/Tenancy/*`
- `src/BuildingBlocks/Infrastructure/Authz/*`
- `src/BuildingBlocks/Contracts/Tenancy/*`
- `src/Cli/3commerce.Cli/*`
- Identity domain/infrastructure/API files for tenants, principals, service accounts, roles, policies, entitlements.
- ADR files for multi-tenancy/RLS/PDP/PEP.

### Relevant Documentation YOU SHOULD READ THESE BEFORE IMPLEMENTING!

- PostgreSQL Row Security Policies: https://www.postgresql.org/docs/current/ddl-rowsecurity.html
- PostgreSQL SET / SET LOCAL: https://www.postgresql.org/docs/current/sql-set.html
- Npgsql EF Core Provider: https://www.npgsql.org/efcore/
- ASP.NET Core Authorization: https://learn.microsoft.com/en-us/aspnet/core/security/authorization/introduction
- Minimal APIs: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis

### Patterns to Follow

- Service registration mirrors `Identity/Api/Program.cs`.
- DbContext schema/outbox mirrors `IdentityDbContext`.
- Endpoints live in static `Endpoints/*` classes with private static handlers and DTO records.
- Message contracts are additive records in `BuildingBlocks.Contracts`.
- Services remain PEPs; Identity/Authz is PDP.

---

## IMPLEMENTATION PLAN

### Phase 1.1: ADR and policy architecture baseline

**Tasks:**
- Document tenant/RLS/PDP/PEP decisions.
- Update ADR index.

### Phase 1.2: Identity tenant/principal/service-account model

**Tasks:**
- Add Principal, Tenant, TenantMembership, TenantOwner rule, ServiceAccount, Role, Permission, Entitlement, MFA policy, lifecycle states.
- Enforce minimum one active MasterGlobal and TenantOwner.

### Phase 1.3: PDP/PEP and field-level policy

**Tasks:**
- Implement batched PDP decisions.
- Add service-side PEP helpers for action/input/output/export/sensitive-read decisions.

### Phase 1.4: Tenant context and RLS

**Tasks:**
- Add tenant context propagation.
- Add transaction-scoped `SET LOCAL` helpers.
- Add Identity RLS migrations and isolation tests.

### Phase 1.5: Gateway/Admin/CLI foundation

**Tasks:**
- Gateway domain/storefront context resolution.
- Admin tenant context selector and role-aware shell.
- .NET global tool CLI skeleton.

---

## STEP-BY-STEP TASKS

### mt1_1 CREATE architecture ADRs and scope baseline

- **IMPLEMENT**: ADRs for strict multi-tenancy, RLS via `SET LOCAL`, PDP/PEP, MasterGlobal semantics, service accounts, and CLI surface.
- **PATTERN**: `docs/adr/0022-named-schema-per-service.md`; ADR index.
- **GOTCHA**: This phase changes security architecture; do not code without ADRs.
- **VALIDATE**: `dotnet build 3commerce.sln`

### mt1_2 UPDATE Identity tenant/principal/service-account/domain authorization foundation

- **IMPLEMENT**: Principal, Tenant, TenantMembership, ServiceAccount hash-only secrets, Role/Permission, entitlements, lifecycle states, MasterGlobal/TenantOwner invariants.
- **PATTERN**: `src/Services/Identity/Infrastructure/IdentityDbContext.cs`; `AuthEndpoints.cs`.
- **GOTCHA**: Email uniqueness changes from global to tenant/customer scoped while principal can span tenants.
- **VALIDATE**: `dotnet test src/Services/Identity/tests/3commerce.Identity.Tests.csproj`

### mt1_3 CREATE PDP/PEP policy engine and field-level metadata

- **IMPLEMENT**: Identity PDP API, batched action/field decisions, field mask/reveal/edit metadata, approval/risk/sensitive-read output, BuildingBlocks PEP client/helpers.
- **PATTERN**: `src/BuildingBlocks/Infrastructure/Auth/InternalClaimsAuth.cs`.
- **GOTCHA**: Admin/CLI UI metadata is advisory only; service APIs enforce again.
- **VALIDATE**: `dotnet test 3commerce.sln --filter Authz`

### mt1_4 ADD tenant context propagation and PostgreSQL RLS helpers

- **IMPLEMENT**: Trusted tenant context headers/claims/messages, EF transaction helper, Identity RLS policies.
- **PATTERN**: named schema/outbox rules in `AGENTS.md` and `IdentityDbContext`.
- **GOTCHA**: Use `SET LOCAL` inside transactions only; never plain pooled `SET`.
- **VALIDATE**: `dotnet test tests/ --filter TenantIsolation`

### mt1_5 UPDATE Gateway for domain resolution and contextual rate limits

- **IMPLEMENT**: host -> tenant/storefront lookup, canonical redirect behavior, unknown domain rejection, trusted context attachment, rate-limit dimensions.
- **PATTERN**: `src/Gateway/Program.cs` rate limiter and YARP setup.
- **GOTCHA**: Strip user-supplied tenant/storefront/context headers.
- **VALIDATE**: `dotnet test tests/ --filter Gateway`

### mt1_6 CREATE .NET global tool CLI skeleton

- **IMPLEMENT**: CLI project, human login, service account login, config profiles, output formats, explicit scope flags, confirmation/reason flags, OpenAPI/permission metadata discovery.
- **PATTERN**: Admin `GatewayClient` calls Gateway only.
- **GOTCHA**: Broad mirror only for human MasterGlobal; service accounts must be explicitly permissioned.
- **VALIDATE**: `dotnet build src/Cli/3commerce.Cli/3commerce.Cli.csproj`

---

## TESTING STRATEGY

- Unit: Principal/Tenant/Role/Permission/ServiceAccount/MasterGlobal/TenantOwner invariants.
- Integration: RLS fail-closed, cross-tenant denial, PDP decisions, gateway context, service account auth.
- E2E smoke: MasterGlobal creates tenant and TenantOwner; CLI logs in and lists tenant context.

## VALIDATION COMMANDS

```bash
dotnet build 3commerce.sln
dotnet format --verify-no-changes
dotnet test src/Services/Identity/tests/3commerce.Identity.Tests.csproj
dotnet test tests/ --filter TenantIsolation
```

## ACCEPTANCE CRITERIA

- [ ] Identity owns tenant/principal/service-account/role/permission foundation.
- [ ] PDP returns batched action/field decisions.
- [ ] Services have PEP helper path.
- [ ] RLS uses transaction-scoped tenant context and fails closed.
- [ ] Gateway attaches trusted tenant/storefront context.
- [ ] CLI skeleton builds and uses Gateway APIs only.
- [ ] MasterGlobal/TenantOwner minimum-active invariants enforced.

## NOTES

Phase 1 must be completed before introducing tenant-scoped data into other services. If this phase is rushed, later feature work will create data leaks.

---

## Addendum — admin RBAC grill (2026-06-18)

Refines the Role/Permission/PDP tasks (mt1_2, mt1_3) per the storefront/admin design grill. Decision: RBAC is **fully dynamic and admin-defined**, not a fixed code catalog.

### mt1_7 ADD dynamic, admin-defined roles + permissions

- **IMPLEMENT**: A permission **registry** (every enforceable action/field permission self-registers at startup); admin-created **custom roles** mapping to any subset of registered permissions; effective-permission resolution carried in internal claims; **claim cache invalidation / session re-evaluation** when a role or membership changes (a revoked permission must take effect promptly, not only at next login).
- **PATTERN**: PDP/PEP from mt1_3; internal claims in `InternalClaimsAuth.cs`.
- **GOTCHA**: Permissions are code-defined and discoverable; *roles* are data. Never let a role grant a permission absent from the registry. `customer` stays a built-in role; staff roles are tenant-scoped data.
- **VALIDATE**: `dotnet test 3commerce.sln --filter Authz`

### mt1_8 ADD admin role/permission management surface

- **IMPLEMENT**: Admin + CLI to create/edit roles, assign permissions, assign roles to staff principals, and preview a principal's effective permissions. Backed by the PDP; field-level rules apply.
- **PATTERN**: Admin GatewayClient; CLI explicit scope/reason.
- **GOTCHA**: Changing a role re-evaluates affected sessions (mt1_7); audit every role/permission change (Phase 6).
- **VALIDATE**: `dotnet build src/Admin/3commerce.Admin.csproj`

Acceptance additions:
- [ ] Roles are admin-defined data over a code-defined permission registry.
- [ ] Role/membership changes invalidate/re-evaluate affected sessions.
- [ ] Admin/CLI can manage roles, permissions, and preview effective permissions.