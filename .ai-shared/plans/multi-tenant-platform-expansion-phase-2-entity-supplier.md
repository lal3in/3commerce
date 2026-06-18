# Feature: Multi-Tenant Platform Expansion — Phase 2 Entity/Supplier

Parent plan: `.ai-shared/plans/multi-tenant-platform-expansion.md`

## Feature Description

Add the central tenant-scoped Entity/Party service, supplier onboarding, supplier portal, and Admin/CLI entity management. Entities represent natural persons, companies, trusts, suppliers, couriers, warehouses, forwarders, customers, tenant owners, and payment recipients via role/profile extensions.

## User Story

As a tenant admin
I want to manage legal entities, suppliers, contacts, addresses, identifiers, relationships, and supplier onboarding
So that tenant operations have a clean master-data foundation for catalog, payments, fulfillment, and support.

As a supplier user
I want a permission-scoped supplier portal
So that I can view assigned operational data and upload stock feeds without accessing unrelated tenant data.

## Problem Statement

Suppliers, couriers, warehouses, customers, legal entities, and tenant owners cannot be modeled safely as scattered service-specific tables. A central Party model is needed while preserving bounded ownership: Entity owns legal/profile identity, Payments owns bank accounts, Fulfillment owns operational locations/carrier config.

## Solution Statement

Create a new Entity service and a generic platform-branded Supplier Portal. Implement entity types, role profiles, versioned addresses, identifiers with verification status, contact methods, relationships, duplicate warnings, supplier onboarding lifecycle, and request/approval workflows for supplier user/bank changes.

## Feature Metadata

**Feature Type**: New Capability  
**Estimated Complexity**: High  
**Primary Systems Affected**: new Entity service, Identity/Authz, Admin, Supplier Portal, CLI, Payments, Fulfillment, Catalog, Workflow, Audit  
**Dependencies**: Phase 1 tenant context/PDP/RLS foundation.

---

## CONTEXT REFERENCES

### Relevant Codebase Files IMPORTANT: YOU MUST READ THESE FILES BEFORE IMPLEMENTING!

- `.ai-shared/plans/multi-tenant-platform-expansion-phase-1-foundation.md` - Phase 1 prerequisites.
- `AGENTS.md` - new service structure and named-schema rules.
- `src/Services/Identity/Api/Program.cs` - service registration pattern.
- `src/Services/Identity/Infrastructure/IdentityDbContext.cs` - DbContext/outbox pattern.
- `docs/reference/api.md` - endpoint conventions.
- `src/Admin/Program.cs` - Admin gateway/auth pattern.

### New Files to Create

- `src/Services/Entity/Api/3commerce.Entity.Api.csproj`
- `src/Services/Entity/Domain/3commerce.Entity.Domain.csproj`
- `src/Services/Entity/Infrastructure/3commerce.Entity.Infrastructure.csproj`
- `src/Services/Entity/tests/3commerce.Entity.Tests.csproj`
- `src/Services/Entity/Api/Endpoints/EntityEndpoints.cs`
- `src/Services/Entity/Api/Endpoints/AdminEntityEndpoints.cs`
- `src/Services/Entity/Infrastructure/EntityDbContext.cs`
- `src/SupplierPortal/*`
- Entity and supplier message contracts under `src/BuildingBlocks/Contracts/Entity/*`

### Relevant Documentation YOU SHOULD READ THESE BEFORE IMPLEMENTING!

- ABN Lookup / Australian Business Register documentation: https://abr.business.gov.au/Documentation
- PostgreSQL RLS docs: https://www.postgresql.org/docs/current/ddl-rowsecurity.html
- ASP.NET Minimal APIs: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis

### Patterns to Follow

- One DB per service, default schema `entity`, migration history in `public`.
- Entity service owns legal/profile data only.
- Payments owns supplier bank accounts/payout instructions.
- Fulfillment owns operational locations/carrier configs linked to entity IDs/address versions.

---

## IMPLEMENTATION PLAN

### Phase 2.1: Entity service skeleton

**Tasks:** create service projects, DbContext, health, outbox/inbox, Dockerfile, registration, tests.

### Phase 2.2: Entity model

**Tasks:** implement entity types, role profiles, identifiers, contacts, addresses, relationships, duplicate warnings.

### Phase 2.3: Supplier lifecycle and portal

**Tasks:** supplier onboarding states/readiness, supplier contacts/principals, portal auth/session, stock upload/update.

### Phase 2.4: Admin/CLI entity management

**Tasks:** entity CRUD/archive, customer link/de-link, supplier user/bank requests, permissioned overrides, audits.

---

## STEP-BY-STEP TASKS

### mt2_1 CREATE Entity service

- **IMPLEMENT**: Add Api/Domain/Infrastructure/tests projects, named schema, DbContext, health, MassTransit outbox/inbox, Program.cs, Dockerfile, compose/helm wiring.
- **PATTERN**: `src/Services/Identity/Api/Program.cs`; `IdentityDbContext`.
- **GOTCHA**: Add API contracts and ADR entry when endpoints/service are added.
- **VALIDATE**: `dotnet test src/Services/Entity/tests/3commerce.Entity.Tests.csproj`

### mt2_2 ADD central tenant-scoped Entity model

- **IMPLEMENT**: EntityType values NaturalPerson, Company, Trust, Partnership, SoleTrader, GovernmentBody, NonProfitAssociation, Other; EntityRole/Profile model.
- **PATTERN**: Domain entities in existing services; no infrastructure dependency in Domain.
- **GOTCHA**: Avoid TFN/SSN/passport/licence/DOB unless specific legal requirement appears.
- **VALIDATE**: `dotnet test src/Services/Entity/tests/3commerce.Entity.Tests.csproj --filter EntityModel`

### mt2_3 ADD addresses, identifiers, contacts, relationships

- **IMPLEMENT**: immutable/versioned addresses, ABN/ACN/GST verification status, contact methods with purpose/verification, typed relationships.
- **PATTERN**: EF indexing/configuration in existing DbContexts.
- **GOTCHA**: Historical orders/shipments must snapshot address versions later, not mutable current addresses.
- **VALIDATE**: `dotnet test src/Services/Entity/tests/3commerce.Entity.Tests.csproj --filter EntityDetails`

### mt2_4 ADD duplicate detection warnings and overrides

- **IMPLEMENT**: Warn on duplicate ABN/ACN/legal/trading/contact; no merge; permissioned ignore/override with reason/audit.
- **PATTERN**: validation response style from Catalog Admin endpoints.
- **GOTCHA**: No general validation framework in v1; keep override feature-specific.
- **VALIDATE**: `dotnet test src/Services/Entity/tests/3commerce.Entity.Tests.csproj --filter Duplicate`

### mt2_5 ADD supplier onboarding lifecycle

- **IMPLEMENT**: Draft, PendingVerification, PendingApproval, Active, Suspended, Archived; readiness checks.
- **PATTERN**: existing enum/state handling in Support/RMA and Ordering saga domains.
- **GOTCHA**: Supplier active status affects product publication, fulfillment, and payables later.
- **VALIDATE**: `dotnet test src/Services/Entity/tests/3commerce.Entity.Tests.csproj --filter SupplierOnboarding`

### mt2_6 CREATE Supplier Portal

- **IMPLEMENT**: Generic platform-branded portal, supplier session context, permissioned views, stock feed upload/update, user/bank change requests.
- **PATTERN**: Admin Blazor/GatewayClient pattern unless a deliberate portal tech ADR chooses otherwise.
- **GOTCHA**: Supplier users cannot edit product content/pricing by default and cannot update dispatch/tracking in v1.
- **VALIDATE**: `dotnet build src/SupplierPortal/3commerce.SupplierPortal.csproj`

### mt2_7 UPDATE Admin/CLI entity and supplier management

- **IMPLEMENT**: Admin pages/API and CLI commands for entity CRUD/archive, supplier onboarding, customer entity link/de-link, supplier user request approval.
- **PATTERN**: Admin uses Gateway only; CLI explicit scope/reason/confirmation.
- **GOTCHA**: Field-level PDP rules apply to every input/output field.
- **VALIDATE**: `dotnet build src/Admin/3commerce.Admin.csproj && dotnet build src/Cli/3commerce.Cli/3commerce.Cli.csproj`

---

## TESTING STRATEGY

- Unit: entity type validation, AU company fields, natural person names, trust minimal fields, address immutability, relationship history.
- Integration: Entity RLS isolation, duplicate warning override, supplier onboarding, customer link/de-link audit, supplier portal scope denial.
- E2E: tenant admin creates supplier entity, supplier user logs into portal, uploads stock feed request.

## VALIDATION COMMANDS

```bash
dotnet build 3commerce.sln
dotnet format --verify-no-changes
dotnet test src/Services/Entity/tests/3commerce.Entity.Tests.csproj
dotnet test tests/ --filter Entity
```

## ACCEPTANCE CRITERIA

- [ ] Entity service created with named schema, outbox/inbox, RLS.
- [ ] Entity types/profiles/addresses/identifiers/contacts/relationships implemented.
- [ ] Supplier lifecycle/readiness implemented.
- [ ] Supplier portal exists and enforces supplier-scoped permissions.
- [ ] Admin/CLI entity management honors field-level policy and audit.
- [ ] No high-risk personal identifiers stored by default.

## NOTES

This phase creates master data only. Do not move bank accounts into Entity; Payments owns payout instruments.