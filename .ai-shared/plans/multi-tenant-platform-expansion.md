# Feature: Multi-Tenant / Multi-Storefront Platform Expansion

The following plan should be complete, but its important that you validate documentation and codebase patterns and task sanity before you start implementing.

Pay special attention to naming of existing utils types and models. Import from the right files etc.

## Feature Description

Expand 3commerce from a single-operator MVP ecommerce platform into a strict-isolation, multi-tenant, multi-storefront commerce platform with tenant-scoped customers, entities/parties, supplier portal, granular PDP/PEP authorization, installable CLI, live carrier integrations, inventory, supplier payables, workflow automation, central audit, marketing/analytics, SEO/agent-friendly storefronts, and phased compliance hardening.

This is not a single implementation sprint. It is a target architecture and phase plan. Phase 1 is foundation-heavy; later phases deliver vertical usable slices. Keep service internals simple and preserve the existing microservice rule: no cross-service database joins, no shared domain logic in BuildingBlocks, and all service-owned data changes plus audit/outbox events must be transactional.

## User Story

As a platform operator / MasterGlobal administrator
I want to manage multiple isolated tenant businesses, their storefronts, admins, suppliers, products, payments, shipping, campaigns, workflows, and audit/compliance controls
So that 3commerce can safely operate many branded ecommerce businesses from one platform while preserving tenant isolation, financial integrity, and automation readiness.

As a tenant owner/admin
I want tenant-scoped Admin, CLI, supplier, storefront, customer, payment, fulfillment, marketing, and reporting capabilities controlled by roles/permissions
So that I can run one legal ecommerce business with multiple storefronts, suppliers, campaigns, and operational teams.

As a shopper
I want one customer account per tenant that works across that tenant's storefronts
So that I can buy from any storefront under the same business and view storefront-filtered or tenant-wide order history where enabled.

## Problem Statement

The current MVP supports one logical operator, two coarse roles (`customer`, `admin`), one public storefront, test-mode Stripe/Xero, static/flat shipping, simple products/variants, and no granular field-level permissions. The requested future platform requires strict tenant isolation, many storefronts per tenant, supplier/courier/warehouse entities, scoped roles, service accounts, CLI automation, maker-checker approvals, audit hash chaining, live Australia Post/DHL shipping, inventory reservations, supplier payables, marketing analytics, behavior tracking, SEO/agent-readiness, webhooks, and region-aware operation. Implementing these ad hoc would create security leaks, service-boundary drift, and brittle APIs.

## Solution Statement

Implement a phased target architecture:

1. Add tenant, region, principal, membership, service account, role/permission, entitlement, PDP/PEP, RLS, domain resolution, API metadata, and CLI/Admin auth foundations in/near Identity and Gateway.
2. Add new Entity, Audit, Workflow, and Marketing/Analytics services; keep Tenancy/Authz in Identity initially.
3. Convert every tenant-owned table and message/API flow to tenant/storefront/correlation/actor-aware operation.
4. Add local service audit tables plus central audit projection; hash-chain local audit logs.
5. Build Admin/CLI/Supplier/Customer portals over shared Admin APIs, with policy-driven field/action capability metadata.
6. Add domain vertical slices for entities, supplier onboarding, catalog/storefront publishing, pricing/promotions, payment/Xero/payout mappings, fulfillment/inventory/carriers, marketing/theme/SEO, exports, webhooks, and workflow automation.

---

## Feature Metadata

**Feature Type**: New Capability / Platform Architecture Expansion
**Estimated Complexity**: Very High
**Primary Systems Affected**: Identity, Gateway, Admin, Storefront, Catalog, Ordering, Payments, Fulfillment, Support, Notifications worker, BuildingBlocks.Contracts, BuildingBlocks.Infrastructure, new Entity service, new Audit service, new Workflow service, new Marketing/Analytics service, tests, docs/api, docs/adr, deploy/config, scripts/e2e-verify.sh
**Dependencies**:
- Existing stack: .NET 10, EF Core 10, Npgsql, PostgreSQL 17, MassTransit 8.x, RabbitMQ, YARP, Next.js 15, React 19, Blazor Server, xUnit/Testcontainers, OpenTelemetry.
- Existing package already available: `MassTransit.Quartz` in `Directory.Packages.props`.
- New dependencies likely, but only add via install/package command when implementing and justify in ADR: Quartz persistence package if needed, CLI command framework if chosen, secret-store adapter(s), image processing library, Australia Post/DHL SDK/client packages if official supported package exists.

---

## Working Brief

**Goal**: Make 3commerce multi-tenant and multi-storefront from the core, with strict tenant isolation now and model-readiness for more advanced cross-tenant/platform behavior later.

**Non-goals for first implementation pass**:
- Cross-tenant product/supplier sharing.
- Full SaaS tenant billing automation.
- Physical multi-region deployment by default; architecture must be region-aware but starts in one physical region unless a tenant requires isolation.
- Entity/customer merge.
- Full B2B customer portal roles.
- Full KYC/KYB sensitive identifiers (TFN/SSN/passport/licence/beneficial owner docs).
- Arbitrary admin-defined SQL/procedure/shell workflow jobs.
- Dedicated analytics warehouse; PostgreSQL JSONB append-only events first.
- Break-glass implementation; document as future hardening.

**Requirements mapped from conversation**:
- Strict tenant isolation; tenant = one legal operator/business; exactly one owner legal entity per tenant; multiple TenantOwner admins, minimum one active.
- Customers are tenant-scoped profiles linked to shared principals; customer email unique per tenant; customer can log into any storefront under tenant; storefront account views filter by storefront, optional tenant-wide dashboard default off.
- Storefronts have public/private/password/invite modes, multiple domains with one canonical, domain resolution through Gateway + Storefront config fetch.
- Identity/Authz owns central PDP, policy catalog, role templates/custom roles, assignments, service accounts, approval policy, entitlements, MFA policy toggles.
- Services are PEPs; field/action decisions batched; high-risk PDP outage fails closed.
- PostgreSQL RLS broadly with `SET LOCAL app.tenant_id` inside transactions; MasterGlobal bypass rare/explicit/audited.
- CLI is .NET global tool, broad mirror only for human MasterGlobal; service accounts narrow explicit permissions.
- Maker-checker from v1 for finance-sensitive changes; human approvers only; MasterGlobal bypasses approval but must supply reason and reveal sensitive fields explicitly.
- New services: Entity, Audit, Workflow, Marketing/Analytics. Tenancy/Authz stay in Identity initially.
- Supplier portal exists from v1, generic platform-branded, permission-based; suppliers can update stock feeds but not shipment tracking/dispatch initially.
- Live carrier quotes/labels/tracking for Australia Post + DHL from v1, with label/tracking automation toggles default off; fake/test carrier still required for tests/dev.
- Inventory by fulfillment source/location plus supplier feed; hybrid reservation during checkout/payment saga.
- Supplier payables configurable by supplier/tenant and posted to ledger when policy condition met.
- Marketing/Analytics dedicated service from v1: campaigns, short links, event batches, behavior analytics, consent snapshots, configurable attribution windows, last-click v1 reporting, PostgreSQL JSONB raw events + projections, future analytics store notes.
- SEO/agent-friendly storefronts mandatory: SSR/ISR, JSON-LD Product/Offer/OfferShippingDetails/MerchantReturnPolicy, sitemaps, robots, canonical, `llms.txt`, product feeds toggleable.

---

## CONTEXT REFERENCES

### Relevant Codebase Files IMPORTANT: YOU MUST READ THESE FILES BEFORE IMPLEMENTING!

- `AGENTS.md` - Why: repository rules, service boundaries, commands, named-schema rules, ADR/API-contract obligations, no cross-service DB joins, no shared domain logic.
- `Directory.Build.props` (lines 7-13) - Why: all projects target `net10.0`, nullable enabled, warnings as errors, root namespace maps `3commerce` to `ThreeCommerce`.
- `Directory.Packages.props` - Why: MassTransit pinned to 8.5.10; EF Core 10; `MassTransit.Quartz` already listed; central package management means packages go here via appropriate command/change during implementation.
- `docs/reference/api.md` - Why: minimal API endpoint organization, `TypedResults`, ProblemDetails, validation, idempotency, OpenAPI/docs/api rule.
- `docs/reference/components.md` - Why: Storefront/Admin UI rules, SSR/ISR, Gateway-only fetch, money formatting, accessibility.
- `docs/prd/3commerce/04-mvp-scope.md` - Why: identifies current scope and items this plan intentionally promotes from deferred/future to target architecture.
- `docs/prd/3commerce/06-architecture.md` - Why: service boundaries, async-first, outbox, data isolation, saga rules.
- `docs/prd/3commerce/09-security-configuration.md` - Why: current auth/session/internal-claims security model and out-of-scope MFA/GDPR items now being phased in.
- `docs/prd/3commerce/10-api-specification.md` - Why: public API shape `/api/{service}/{resource}`, auth conventions, payload examples.
- `docs/prd/3commerce/13-future-considerations.md` - Why: discounts, carrier APIs, analytics, MFA, marketplace capabilities are now intentionally planned rather than accidentally drifting in.
- `src/Services/Identity/Api/Program.cs` (lines 18-24, 40) - Why: existing service registration pattern: OpenAPI, EF DbContext, outbox bus, health, internal claims auth, endpoint mapping.
- `src/Services/Identity/Infrastructure/IdentityDbContext.cs` (lines 9-18, 24-42) - Why: DbSet/model configuration, named default schema, indexes, MassTransit outbox/inbox pattern.
- `src/Services/Identity/Api/Endpoints/AuthEndpoints.cs` - Why: endpoint class pattern, private static handlers, DTO records, cookie flow, generic no-enumeration responses.
- `src/BuildingBlocks/Infrastructure/Auth/InternalClaimsAuth.cs` (lines 17-22, 24-66) - Why: internal claims header, current coarse `Customer`/`Admin` policies that PDP/PEP must replace/extend compatibly.
- `src/Gateway/Program.cs` (lines 27-58, 66-87) - Why: rate limiter, YARP configuration, health filtering, session auth middleware insertion point.
- `src/Services/Catalog/Api/Endpoints/AdminEndpoints.cs` (lines 16-28, 92-143, 148-214, 233-261) - Why: Admin route group, auth, CRUD, validation, event publish through outbox-before-save pattern to preserve/adjust.
- `src/Services/Catalog/Api/Endpoints/ProductsEndpoints.cs` - Why: public catalog/search/detail shape to tenant/storefront-scope and enrich for SEO/product feeds.
- `src/Services/Ordering/Infrastructure/OrderingDbContext.cs` - Why: cart/order/saga DbContext pattern and where CheckoutAttempt/multi-shipment/order snapshots will fit.
- `src/Services/Payments/Infrastructure/PaymentsDbContext.cs` - Why: ledger/payment/refund/Xero/idempotency tables and transaction invariants to extend for tenant, supplier payables, payment accounts, Xero mappings.
- `src/Admin/Program.cs` (lines 15-25, 48-54) - Why: Blazor Server cookie auth, admin policy, GatewayClient pattern, IP allowlist.
- `.ai-shared/plans/plan_status_executions.md` - Why: existing plan status format and previous task IDs.

### New Files/Projects to Create

- `src/Services/Entity/Domain/*` - Central tenant-scoped entity/party model: Entity, identifiers, contacts, addresses, relationships, role profiles.
- `src/Services/Entity/Infrastructure/EntityDbContext.cs` - EF Core named schema `entity`, RLS-ready tenant-owned tables, outbox/inbox.
- `src/Services/Entity/Api/Endpoints/*` - Admin/Supplier-safe entity endpoints.
- `src/Services/Entity/tests/*` - Unit/integration tests.
- `src/Services/Audit/Domain/*`, `Infrastructure/AuditDbContext.cs`, `Api/Endpoints/*` - Central audit projection/search service.
- `src/Services/Workflow/Domain/*`, `Infrastructure/WorkflowDbContext.cs`, `Api/Endpoints/*` - Quartz/MassTransit schedule/orchestration service.
- `src/Services/Marketing/Domain/*`, `Infrastructure/MarketingDbContext.cs`, `Api/Endpoints/*` - Campaigns, short links, analytics event collection/projections.
- `src/Cli/3commerce.Cli/*` - .NET global tool CLI.
- `src/SupplierPortal/*` - Supplier-facing Blazor/Next app or Blazor Server app (choose during phase design; generic platform branding).
- `src/BuildingBlocks/Contracts/Tenancy/*`, `Entity/*`, `Audit/*`, `Workflow/*`, `Marketing/*`, `Fulfillment/*`, `Payments/*` - Additive message contracts carrying tenant/storefront/correlation/actor context.
- `src/BuildingBlocks/Infrastructure/Tenancy/*` - Tenant context, RLS transaction helpers, request/message context propagation.
- `src/BuildingBlocks/Infrastructure/Authz/*` - PDP client abstractions, PEP filters/helpers, field policy DTOs.
- `docs/adr/0023-*.md` onward - ADRs for multi-tenancy/RLS/PDP, new services, audit, workflow, analytics, supplier portal, live carriers as implemented.
- `docs/api/*.json|md` - API contracts per service when endpoints change/add.

### Relevant Documentation YOU SHOULD READ THESE BEFORE IMPLEMENTING!

- [PostgreSQL Row Security Policies](https://www.postgresql.org/docs/current/ddl-rowsecurity.html)
  - Why: Implement RLS policies on tenant-owned tables and understand `USING`/`WITH CHECK`, bypass behavior, and fail-closed semantics.
- [PostgreSQL `SET LOCAL`](https://www.postgresql.org/docs/current/sql-set.html)
  - Why: RLS context must be transaction-scoped to avoid connection-pool tenant leakage.
- [Npgsql EF Core Provider Docs](https://www.npgsql.org/efcore/)
  - Why: EF Core/Npgsql transaction handling, migrations, raw SQL, and named schema compatibility.
- [MassTransit Quartz Scheduler](https://masstransit.io/documentation/configuration/scheduling)
  - Why: Workflow service uses Quartz/MassTransit typed scheduled jobs, not arbitrary SQL/shell.
- [MassTransit EF Outbox](https://masstransit.io/documentation/configuration/middleware/outbox)
  - Why: Domain change + local audit + outbox events must commit atomically.
- [Microsoft Minimal APIs](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis)
  - Why: Existing endpoint conventions are minimal APIs with `TypedResults` and OpenAPI metadata.
- [ASP.NET Core Authorization](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/introduction)
  - Why: PDP/PEP starts as structured in-platform policy engine using ASP.NET authorization + metadata, with OPA/OpenFGA seam later.
- [Australia Post Shipping and Tracking API](https://developers.auspost.com.au/apis/shipping-and-tracking/reference)
  - Why: v1 target carrier for quotes, labels, tracking; requires account/contract readiness.
- [DHL Express MyDHL API](https://developer.dhl.com/api-reference/mydhl-api)
  - Why: v1 target carrier for quotes, labels, tracking.
- [Google Search Product Structured Data](https://developers.google.com/search/docs/appearance/structured-data/product)
  - Why: Product pages/feeds must expose price, availability, shipping, return info truthfully.
- [Schema.org Product](https://schema.org/Product), [OfferShippingDetails](https://schema.org/OfferShippingDetails), [MerchantReturnPolicy](https://schema.org/MerchantReturnPolicy)
  - Why: Required JSON-LD vocabulary for SEO/agent-friendly storefronts.
- [llms.txt proposal](https://llmstxt.org/)
  - Why: Add per-storefront agent-readable files while recognizing the spec is emerging/experimental.
- [Google Merchant Center Product Data Specification](https://support.google.com/merchants/answer/7052112)
  - Why: Storefront product feeds must include required product/offer/shipping/return fields where eligible.
- [OWASP ASVS](https://owasp.org/www-project-application-security-verification-standard/) and [OWASP Cheat Sheets](https://cheatsheetseries.owasp.org/)
  - Why: Authz, session, logging, secrets, file upload, webhook, and tenant isolation hardening.

### Patterns to Follow

**Service Program.cs pattern**

```csharp
builder.Services.AddOpenApi();
builder.Services.AddDbContext<ServiceDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Database"), o =>
        o.MigrationsHistoryTable("__EFMigrationsHistory", "public")));
builder.Services.AddServiceBus<ServiceDbContext>(builder.Configuration);
builder.Services.AddInternalClaimsAuth(builder.Configuration, builder.Environment);
```

Follow `src/Services/Identity/Api/Program.cs` and add new service registrations without dumping endpoint logic into Program.cs.

**DbContext pattern**

```csharp
modelBuilder.HasDefaultSchema("identity");
modelBuilder.AddInboxStateEntity();
modelBuilder.AddOutboxMessageEntity();
modelBuilder.AddOutboxStateEntity();
```

Follow `IdentityDbContext`/`PaymentsDbContext`; new DB-owning services need named schema, migrations history in `public`, outbox/inbox, and RLS migrations.

**Endpoint pattern**

```csharp
var group = app.MapGroup("/admin")
    .WithTags("Admin")
    .RequireAuthorization(InternalClaimsAuth.AdminPolicy);

group.MapGet("/products", ListProducts);
```

Handlers are private static methods with DTO records; return `TypedResults`; include cancellation tokens; use ProblemDetails/validation.

**Messaging pattern**

Publish message contracts from owning services through EF outbox in the same transaction. Contracts are records in `src/BuildingBlocks/Contracts`, additive-only.

**UI pattern**

Storefront uses Next.js App Router SSR/ISR, fetches through Gateway only, and keeps dynamic cart/account/checkout uncached. Admin is Blazor Server with `GatewayClient`, cookie auth, and IP allowlist.

**Anti-patterns to avoid**

- Do not query another service's database.
- Do not put domain logic in BuildingBlocks.
- Do not add speculative generic frameworks where typed jobs/policies suffice.
- Do not expose internal claims header or bypass Gateway from browser/CLI.
- Do not store raw secrets, service account secrets, passwords, card data, or high-risk personal identifiers.
- Do not treat analytics events as financial/order truth.
- Do not allow UI-only authorization or field masking.
- Do not implement arbitrary admin SQL/procedure/shell jobs in Workflow v1.

---

## IMPLEMENTATION PLAN

### Phase 1: Multi-Tenancy, Identity/Authz, Tenant Context, RLS, Gateway, Admin/CLI Foundation

Foundation-heavy. No broad domain feature rewrites until tenant context and policy enforcement exist.

**Tasks:**

1. Create ADR for multi-tenancy/RLS/PDP/PEP target architecture.
2. Extend Identity domain with Principal, Tenant, TenantOwnerLegalEntityRef, TenantMembership, Storefront registry basics, ServiceAccount, Role, Permission, RoleTemplate, CustomRole, PermissionAssignment, Entitlement, MfaPolicy, TenantLifecycle.
3. Add `TenantId`, `Region`, `ScopeType`, `StorefrontId` concepts and central context DTOs/contracts.
4. Implement PDP API in Identity/Authz: action/field/batched decisions, approval requirement metadata, sensitive-read flags, risk levels, decision ID/version.
5. Implement PEP helpers/endpoint filters in BuildingBlocks.Infrastructure for policy calls, DTO field masking/edit checks, sensitive read audit hooks.
6. Implement PostgreSQL RLS helper with transaction-scoped `SET LOCAL app.tenant_id`, `app.principal_id`, `app.is_platform_admin` and tests for connection pool safety.
7. Add RLS policies to Identity first, then prepare migration templates for other services.
8. Extend Gateway domain/storefront resolution: host -> global minimal registry -> tenant/storefront context; attach context headers/internal claims; reject unknown domains.
9. Expand internal claims with principal type, tenant memberships/scopes, selected tenant/storefront, correlation/session context.
10. Implement tenant/service-account auth flows for Admin/CLI; human CLI auth and service account client credentials with hash-only secrets.
11. Create `src/Cli/3commerce.Cli` as .NET global tool skeleton: auth, config, output formats, explicit scope selection, OpenAPI/permission metadata discovery.
12. Update Admin auth/session context to support MasterGlobal, tenant context selector, tenant lifecycle, role-aware navigation.
13. Add initial Admin APIs for tenant creation (MasterGlobal-only), owner legal entity placeholder reference, TenantOwner admin assignment, role/permission assignment, service account creation/rotation/revocation.
14. Enforce minimum one active MasterGlobal and one active TenantOwner per tenant.
15. Add high-risk permission assignment approval hooks, with MasterGlobal bypass requiring reason/audit.
16. Add idempotency support for mutating admin/CLI/service-account operations likely to retry.
17. Add tests: tenant isolation, RLS fail-closed, PDP field decisions, service account auth, MasterGlobal bypass audit, role assignment constraints.

### Phase 2: Entity/Party Service, Supplier Foundation, Supplier Portal, Admin/CLI Entity Management

**Tasks:**

1. Create Entity service (Api/Domain/Infrastructure/tests) with named schema, outbox, RLS, health, OpenAPI, Dockerfile, compose/helm registration.
2. Implement central tenant-scoped Entity model with types: NaturalPerson, Company, Trust, Partnership, SoleTrader, GovernmentBody, NonProfitAssociation, Other.
3. Implement role/profile model: Supplier, Courier, Warehouse, Forwarder, Customer, TenantOwner, PaymentRecipient, BillingContact, etc.
4. Implement immutable/versioned addresses, contact methods, identifiers, verification status, entity relationships, duplicate warnings and permissioned ignore overrides.
5. AU v1 operational fields: Company legal/trading names, ABN, ACN, GST status, registered/business/billing addresses; NaturalPerson First/Middle/Last; Trust minimal fields.
6. Add supplier onboarding lifecycle: Draft, PendingVerification, PendingApproval, Active, Suspended, Archived.
7. Add supplier contacts/principals linked to supplier entity; supplier portal separate generic platform-branded app/session context.
8. Supplier portal v1 permissions: view assigned products/orders/payable visibility; upload/update stock feeds; request user changes; request bank account changes; no content/pricing edits by default; no dispatch/tracking updates initially.
9. Implement request-and-approve supplier user management through Workflow/approvals.
10. Add Admin entity management pages/APIs: create/update/archive entities, link/de-link entity to customer, supplier onboarding/readiness, contacts, identifiers, relationships.
11. Add CLI entity/supplier commands with dry-run/confirmation/reason where destructive/sensitive.
12. Publish entity/supplier events for read projections in Catalog/Fulfillment/Payments/Support.
13. Add tests for entity validation, duplicate warning override, supplier onboarding, supplier portal scope isolation, link/de-link customer entity audit.

### Phase 3: Storefront, Catalog, Product Model, Pricing, Promotions, Payment/Xero/Payout Mappings

**Tasks:**

1. Tenant/storefront model complete: storefront lifecycle Draft/Preview/Active/Paused/Archived, visibility public/private/password/invite, multiple domains/canonical redirects, readiness checks, activation approval for live selling.
2. Add product model upgrades: tenant/storefront scope, parent/variant support, identifiers table, categories/taxonomy tenant-wide with storefront navigation/merchandising, bundles/kits (virtual first, prepacked model-ready), structured restrictions/customs fields.
3. Product publication: invisible until explicit storefront assignment; shipping-ready, SEO-ready, price-ready, inventory projection-ready checks; parent assignment with variant visibility overrides.
4. Pricing: SupplierCost, default SellingPrice, StorefrontProduct override, tax mode Inclusive/Exclusive, one currency per storefront v1, model-ready multi-currency later.
5. Promotions v1: coupon fixed/percentage, automatic product/category/storefront discount, bundle discount, free shipping; best-discount-wins, no complex stacking.
6. Catalog provides price/eligibility data; Ordering owns final cart/checkout calculation and snapshots.
7. Payments: tenant default + storefront override payment accounts; lifecycle Draft/Configured/TestVerified/PendingLiveApproval/Active/Suspended/Archived; live activation approval.
8. Payments: supplier bank accounts/payout instruments and payout instructions with hierarchy: Product+Storefront, Product, Storefront+Supplier, Supplier default; changes require approval; supplier bank details masked/reveal audited.
9. Payments: supplier payable policy configurable by supplier/tenant; ledger postings when policy condition met; manual payout recording model-ready.
10. Xero/accounting: tenant defaults plus storefront/category/supplier/product overrides; summary journals v1; detailed sync model-ready; live approval.
11. Ordering: CheckoutAttempt before payment; Order created/confirmed only after payment succeeds; final revalidation before capture.
12. Customer account: one tenant-scoped profile per email; storefront-specific views; optional tenant-wide dashboard default off with warning + TenantOwner notification.
13. Add policy/legal pages with versioning/publish workflow and order policy snapshots.
14. Admin and CLI CRUD for storefronts/products/pricing/promotions/payment mappings/Xero mappings/payout instructions; field-level permissions for supplier cost/margin/bank/accounting.
15. Tests for storefront domain resolution, product publication readiness, pricing overrides, promotion best-wins, payable ledger postings, payment account resolution/snapshot, customer dashboard toggle.

### Phase 4: Shipping, Inventory, Fulfillment, Carrier Integrations, Returns/RMA Upgrades

**Tasks:**

1. Fulfillment owns inventory/reservations and operational locations linked to Entity warehouse/supplier/forwarder entities/addresses.
2. Inventory by fulfillment source/location, supplier feed updates, reservations: cart/check availability early, hard reserve during checkout/payment saga, release on timeout/failure, commit on payment success.
3. Fulfillment source types: TenantWarehouse, SupplierDirect, ThirdPartyForwarder, Manual/Unassigned; Manual/Unassigned cannot publish.
4. Multi-origin carts: one order, multiple shipment groups; customer selects per shipment group, combined shipping total.
5. Live carrier integrations: Australia Post + DHL; fake/test carrier required for dev/tests; tenant default + storefront override; lifecycle/readiness; quotes required by default unless fallback configured.
6. Carrier capabilities: quotes, label creation, tracking; label/tracking toggles default off; provider mode sandbox/live constrained by environment; live activation approval.
7. Address validation through carrier/provider where available, basic fallback, permissioned manual override.
8. Shipping quote validity: provider expiry else configurable default; final quote revalidation immediately before payment authorization/capture.
9. Shipments support multiple packages/parcels; line quantities splittable across shipments/packages; manual label upload/tracking record when automation off.
10. Order holds: fraud/payment/address/inventory/supplier/compliance before fulfillment; release/cancel with reason/permission.
11. Returns/RMA order-line and shipment-aware; manual inspection/restock after returns; supplier payable reversals/adjustments.
12. Support tickets scoped to tenant/storefront/customer/order/shipment/product/RMA.
13. Tenant admin handles supplier-direct dispatch/tracking in v1; supplier users can update stock only.
14. Tests for inventory race/reservation timeout, quote failures/fallbacks, multi-shipment checkout, carrier fake labels/tracking, manual labels, partial fulfillment, RMA line/shipment awareness.

### Phase 5: Marketing/Analytics, Campaigns, Themes, SEO, Agent-Friendly Storefronts, Feeds

**Tasks:**

1. Create Marketing/Analytics service with campaigns, targets, campaign links/short links, events, conversions, consent snapshots, attribution windows, projections.
2. Analytics ingestion: client batches to `/api/analytics/events` behind Gateway; schema-versioned append-only PostgreSQL JSONB raw events; typed projections; future export to ClickHouse/BigQuery/Snowflake/Kafka notes.
3. Campaigns target storefront/product/landing path; safe internal campaign codes + sanitized external UTM/gclid/etc.; configurable attribution window resolution tenant/storefront/campaign; store all touches, last-click v1 reporting.
4. Short links use platform global short domain v1; validate destination against registered storefront domains; click tracking before redirect; no open redirects.
5. Consent/cookie banner configurable per storefront; categories Necessary/Analytics/Marketing; first-party attribution by default, broader analytics/marketing consent-aware; behavior analytics event-based, no session replay v1.
6. Storefront customization hybrid: shared commerce core, configurable themes/templates/content blocks/design tokens, bespoke modules as controlled developer-implemented escape hatch.
7. Draft/preview/publish/scheduled publishing for themes/content/policy/campaign landing pages; Workflow schedules publish commands; expiring signed preview links supported.
8. Rendering: public pages static/ISR where possible; dynamic SSR for cart/account/checkout; automatic targeted revalidation on publish events.
9. SEO/agent-friendly mandatory: canonical, sitemap, robots, OpenGraph/Twitter, JSON-LD Product/Offer/OfferShippingDetails/MerchantReturnPolicy/Organization/BreadcrumbList/WebSite, accessible semantic HTML, `llms.txt`, machine-readable public policy/product metadata.
10. Product feeds per storefront for merchant/ad platforms toggleable; Workflow scheduled generation; public/published eligible products only; reviews/ratings model-ready but not emitted without real data.
11. Behavior/marketing dashboards and exports with field/permission-aware reporting.
12. Tests for event ingestion idempotency/rate limits, consent snapshots, last-click attribution, short-link redirects, SEO JSON-LD correctness, sitemap/robots/private noindex, feed eligibility, cache revalidation.

### Phase 6: Audit, Workflow, Compliance, Notifications, Webhooks, Exports, Launch Gates

**Tasks:**

1. Create Audit service central projection/search; add local per-service append-only audit tables and audit events via outbox.
2. Hash-chain local audit logs v1; central projection preserves source hashes; external anchoring/WORM future.
3. Audit mutations + sensitive reads/exports + high-risk denied auth attempts; field reveal/reason handling; MasterGlobal high-risk reason/audit.
4. Retention policies: audit default 7 years configurable by tenant/jurisdiction; raw analytics default 13 months configurable with longer aggregated summaries.
5. Workflow service with Quartz.NET + MassTransit typed scheduled jobs, job definitions/runs, retries, status dashboards, admin/CLI APIs; no arbitrary SQL/proc/shell.
6. Approval ownership: Identity/Authz policy, Workflow task orchestration/status, owning service pending change/application; configurable expiry by risk/action.
7. Notifications channel abstraction: email first, in-app/webhook/SMS later; high-risk alert notifications.
8. Outbound tenant webhooks v1 via notification worker/channel: signed deliveries, retries/backoff, delivery logs, event subscriptions; high-volume analytics not webhooked by default.
9. Inbound provider webhooks routed by Gateway to owning services; owning service verifies signatures/idempotency.
10. Import/export pluggable adapters CSV/JSON first; large/sensitive exports async audited jobs; customer self-service export/deletion requests processed by admin workflow.
11. Object storage abstraction for product/theme/campaign assets, imports/exports, labels, reports; local/dev adapter + production object storage path; image variants generation.
12. Security/compliance hardening: context-aware rate limiting, basic bot/fraud controls with CAPTCHA/risk-provider seam, MFA/step-up toggle and tenant policy within platform minimums, WCAG 2.2 AA target.
13. Region-awareness: tenant home region, minimal global domain registry, no tenant region moves initially, region-aware backups/retention; one physical region initially unless required.
14. Define SLOs and monitor critical checkout/payment/shipping/auth/domain/webhook/workflow/analytics surfaces from v1.
15. Launch gates: external penetration test before live payments, load/performance testing for critical storefront checkout/shipping/payment, live integrations approved; deeper vulnerability/container/SAST scanning and DR restore testing later hardening unless policy changes.

### Phase 7: Digital Supply, Entitlements, Usage Metering, Billing (ADR-0028)

Extends the composable supply model (ADR-0028; supply seam landed in Phase 4 as mt4_1b) along the digital axis. See `.ai-shared/plans/multi-tenant-platform-expansion-phase-7-digital-supply-billing.md`.

**Tasks:**

1. Pricing/Billing service + price models (`prices` with `pricing_model`/`billing_period`/currency, `price_tiers`); finish moving price off `Variant.PriceMinor` onto the Offer.
2. Entitlement service: `customer_entitlements` (subscription/license/download/api_access/service_access) + `subscription_products`; issue + activate on digital line confirmation.
3. Subscription billing: `billing_mode = recurring` sets up a subscription at checkout (not single capture); renewals/trials/dunning behind `IPaymentProvider`.
4. Usage Metering service: `usage_plans` (token/transaction/request/minute/seat/storage) + append-only `usage_records` rolled into `usage_balances` per period.
5. Usage-based billing + overage: rate usage vs plan/tiers; `UsageThresholdReached → OverageCharged/AccessLimited`; balances gate access.
6. Digital fulfilment flows + React storefront UX (subscription/usage/download checkout, "my access/usage", mixed-cart one-time+recurring+metered).
7. Docs/e2e-verify/ADR currency for the digital supply axis.

---

## STEP-BY-STEP TASKS

IMPORTANT: Execute every task in order, top to bottom. Each task is atomic and independently testable. Because this is a platform-scale feature, tasks intentionally remain coarse enough for sub-plans. Each task must create/update its own smaller implementation plan if it cannot be completed safely in one coding session.

### task_1 CREATE architecture ADRs and scope baseline

- **IMPLEMENT**: Add ADRs for multi-tenancy/RLS/PDP/new services/analytics/workflow/audit/carriers when implementation begins; update `docs/adr/adr_index.md`.
- **PATTERN**: `docs/adr/0022-named-schema-per-service.md`; `AGENTS.md` ADR rule.
- **GOTCHA**: Do not implement without ADRs because this changes service boundaries and security model.
- **VALIDATE**: `dotnet build 3commerce.sln`

### task_2 UPDATE Identity with tenant/principal/service-account/domain authorization foundation

- **IMPLEMENT**: Principal, Tenant, TenantMembership, TenantOwner rules, ServiceAccount hash-only credentials, Role/Permission/Entitlement/MFA models, lifecycle states.
- **PATTERN**: `src/Services/Identity/Infrastructure/IdentityDbContext.cs:9-42`; `src/Services/Identity/Api/Endpoints/AuthEndpoints.cs`.
- **GOTCHA**: Current unique email is global; new model needs tenant-scoped customer email while principal can access multiple tenants.
- **VALIDATE**: `dotnet test src/Services/Identity/tests/3commerce.Identity.Tests.csproj`

### task_3 CREATE PDP/PEP policy engine and field-level policy metadata

- **IMPLEMENT**: Identity/Authz PDP batched decisions; BuildingBlocks PEP client/helpers; field visible/editable/masked/reveal/reason/approval/sensitive-read decisions.
- **PATTERN**: `src/BuildingBlocks/Infrastructure/Auth/InternalClaimsAuth.cs:21-66` as current policy starting point.
- **GOTCHA**: UI/CLI capability metadata is advisory only; services enforce decisions server-side.
- **VALIDATE**: `dotnet test 3commerce.sln --filter Authz`

### task_4 ADD tenant context propagation and PostgreSQL RLS helpers

- **IMPLEMENT**: request/message tenant context; EF transaction helper sets `SET LOCAL app.tenant_id`, `app.principal_id`, `app.is_platform_admin`; RLS migrations start in Identity.
- **PATTERN**: named schema rules in `AGENTS.md`; DbContext schema pattern in Identity/Payments.
- **GOTCHA**: Use transaction-scoped `SET LOCAL` only; connection pooling can leak session variables if plain `SET` is used.
- **VALIDATE**: `dotnet test tests/ --filter TenantIsolation`

### task_5 UPDATE Gateway for tenant/storefront/domain resolution and context-aware rate limiting

- **IMPLEMENT**: minimal global domain registry lookup, canonical redirects, context headers/internal claims, unknown-domain rejection, rate-limit dimensions.
- **PATTERN**: `src/Gateway/Program.cs:27-58`, `src/Gateway/Program.cs:66-87`.
- **GOTCHA**: Strip/ignore user-supplied context headers; only Gateway mints trusted context.
- **VALIDATE**: `dotnet test tests/ --filter Gateway`

### task_6 CREATE .NET global tool CLI skeleton

- **IMPLEMENT**: `src/Cli/3commerce.Cli`, auth login/service login, config profiles, `--tenant/--storefront`, output table/json/yaml/csv, OpenAPI/permission metadata help, confirmation/reason flags.
- **PATTERN**: Admin `GatewayClient` approach from `src/Admin/Program.cs:13-25`; API contract metadata.
- **GOTCHA**: Broad admin mirror is human MasterGlobal only; service accounts always explicit narrow permissions.
- **VALIDATE**: `dotnet build src/Cli/3commerce.Cli/3commerce.Cli.csproj`

### task_7 CREATE Entity service

- **IMPLEMENT**: service project triad, DbContext, migrations, health, bus, Dockerfile, API endpoints, tests, docker/helm registration.
- **PATTERN**: `src/Services/Identity/Api/Program.cs:18-40`; `IdentityDbContext` outbox pattern.
- **GOTCHA**: Entity owns legal/profile data only; Payments owns bank accounts; Fulfillment owns carrier config/locations.
- **VALIDATE**: `dotnet test src/Services/Entity/tests/3commerce.Entity.Tests.csproj`

### task_8 ADD supplier onboarding and Supplier Portal

- **IMPLEMENT**: supplier lifecycle, supplier contacts, supplier portal session context, stock feed upload/update, request-and-approve user/bank changes, read-only shipment info.
- **PATTERN**: Blazor Admin auth/GatewayClient in `src/Admin/Program.cs`; endpoint conventions in `docs/reference/api.md`.
- **GOTCHA**: Supplier users cannot update dispatch/tracking in v1; product content/pricing disabled by default.
- **VALIDATE**: `dotnet build src/SupplierPortal/3commerce.SupplierPortal.csproj && dotnet test tests/ --filter SupplierPortal`

### task_9 UPDATE Catalog for tenant/storefront product model, variants, bundles, pricing, publication

- **IMPLEMENT**: TenantId/RLS, storefront assignment, variants, identifiers, bundles/kits, product/category taxonomy, SupplierCost/SellingPrice/TaxMode, SEO, publication readiness.
- **PATTERN**: `src/Services/Catalog/Api/Endpoints/AdminEndpoints.cs:16-28`, `92-214`, `233-261`; `ProductsEndpoints.cs` public read shape.
- **GOTCHA**: Product creation stays invisible until explicit storefront publish; Manual/Unassigned fulfillment blocks publish.
- **VALIDATE**: `dotnet test src/Services/Catalog/tests/3commerce.Catalog.Tests.csproj`

### task_10 UPDATE Ordering checkout model and pricing/promotions/tax snapshots

- **IMPLEMENT**: CheckoutAttempt, final revalidation, promotions best-wins, storefront/customer filtering, order number per storefront, campaign attribution references, policy snapshots.
- **PATTERN**: `src/Services/Ordering/Infrastructure/OrderingDbContext.cs`; existing checkout saga tests.
- **GOTCHA**: Order is only customer-visible after successful payment; failed checkout remains CheckoutAttempt.
- **VALIDATE**: `dotnet test src/Services/Ordering/tests/3commerce.Ordering.Tests.csproj`

### task_11 UPDATE Payments for payment accounts, supplier bank/payout/payables, Xero mappings

- **IMPLEMENT**: Tenant/storefront payment resolution, account lifecycle, supplier bank accounts, payout instructions, supplier payable policies/ledger postings, Xero mapping hierarchy.
- **PATTERN**: `src/Services/Payments/Infrastructure/PaymentsDbContext.cs`; ledger invariant tests.
- **GOTCHA**: Ledger remains source of truth; Xero is downstream; bank details masked and approval-protected.
- **VALIDATE**: `dotnet test src/Services/Payments/tests/3commerce.Payments.Tests.csproj`

### task_12 UPDATE Fulfillment for inventory, live carriers, shipment/package model

- **IMPLEMENT**: Inventory locations/reservations, Australia Post/DHL/fake carrier adapters, quote/label/tracking lifecycle/toggles, multi-shipment/package/partial fulfillment, manual labels/tracking.
- **PATTERN**: existing Fulfillment shipment endpoints and integration tests.
- **GOTCHA**: Label/tracking automation default off; fake carrier required for deterministic tests.
- **VALIDATE**: `dotnet test src/Services/Fulfillment/tests/3commerce.Fulfillment.Tests.csproj`

### task_13 UPDATE Support for scoped tickets, RMA line/shipment awareness, view-as only

- **IMPLEMENT**: tenant/storefront/product/shipment/RMA links, partial returns, manual inspection/restock, admin view-as read-only.
- **PATTERN**: existing Support RMA saga and per-line RMA implementation noted in `plan_status_executions.md` BL-8.
- **GOTCHA**: Refunds continue through single Support -> Payments saga path.
- **VALIDATE**: `dotnet test src/Services/Support/tests/3commerce.Support.Tests.csproj`

### task_14 CREATE Workflow service and approval orchestration

- **IMPLEMENT**: Quartz schedules, typed jobs, approval task orchestration, job runs, publish commands via MassTransit, admin/CLI APIs.
- **PATTERN**: MassTransit bus/outbox setup in service Program.cs; no arbitrary SQL/proc/shell.
- **GOTCHA**: Owning service stores pending domain change and applies it; Workflow tracks task/status only.
- **VALIDATE**: `dotnet test src/Services/Workflow/tests/3commerce.Workflow.Tests.csproj`

### task_15 CREATE Audit service and local hash-chained audit framework

- **IMPLEMENT**: per-service local audit tables, hash chaining, central projection, audit search/export, sensitive read/denied attempt audit.
- **PATTERN**: transactional outbox pattern; audit events additive contracts.
- **GOTCHA**: Central audit is projection; local service audit is authoritative.
- **VALIDATE**: `dotnet test tests/ --filter Audit`

### task_16 CREATE Marketing/Analytics service

- **IMPLEMENT**: campaigns, targets, links, short redirects, event batch collector, behavior events, consent snapshots, attribution windows, conversions, projections, reports.
- **PATTERN**: new service pattern; Gateway routing and rate limits.
- **GOTCHA**: Analytics events cannot become order/payment truth; raw event retention default 13 months.
- **VALIDATE**: `dotnet test src/Services/Marketing/tests/3commerce.Marketing.Tests.csproj`

### task_17 UPDATE Storefront for multi-storefront rendering, themes, SEO, consent, analytics

- **IMPLEMENT**: host/context-aware rendering, theme/template framework + bespoke module seam, draft/preview/publish consumption, ISR/revalidation, JSON-LD, `llms.txt`, product feeds, consent banner, analytics batching.
- **PATTERN**: `docs/reference/components.md`; `src/Storefront/package.json` scripts.
- **GOTCHA**: Campaign query params must not explode static cache keys; private storefronts noindex/restrict agent files.
- **VALIDATE**: `cd src/Storefront && npm run lint && npx tsc --noEmit && npm run build`

### task_18 UPDATE Admin for role-aware dashboards, CRUD, approvals, exports, workflows

- **IMPLEMENT**: MasterGlobal dashboard, tenant selector, entity/supplier/catalog/storefront/payment/fulfillment/marketing/audit/workflow dashboards, field-level masking/reveal, dry-run bulk actions.
- **PATTERN**: Blazor Admin `Program.cs` and existing pages; `docs/reference/components.md` Blazor section.
- **GOTCHA**: Admin remains platform-branded; show current tenant/storefront context clearly.
- **VALIDATE**: `dotnet build src/Admin/3commerce.Admin.csproj`

### task_19 UPDATE Notifications and webhooks

- **IMPLEMENT**: channel abstraction email first; outbound tenant webhooks signed/retried/logged; high-risk alerts; inbound provider webhooks via Gateway to owning service.
- **PATTERN**: existing Notifications worker event consumer style.
- **GOTCHA**: High-volume analytics events are not outbound webhook events by default.
- **VALIDATE**: `dotnet test 3commerce.sln --filter Notifications`

### task_20 UPDATE object storage, imports/exports, image variants

- **IMPLEMENT**: object storage abstraction local/dev + production provider seam; import/export adapters CSV/JSON first; async exports; image variants; signed URLs.
- **PATTERN**: existing importer and export/report patterns.
- **GOTCHA**: Metadata ownership stays with owning service; storage abstraction owns bytes/access only.
- **VALIDATE**: `dotnet test 3commerce.sln --filter Storage`

### task_21 UPDATE docs/api, ADRs, AGENTS project structure, e2e-verify

- **IMPLEMENT**: API contract files/index for every endpoint change; ADR index; AGENTS project structure for new services/apps; scripts/e2e-verify.sh coverage checklist for new/renamed tests and live flows.
- **PATTERN**: repo Rules in `AGENTS.md`; existing docs/api and scripts.
- **GOTCHA**: Regression script must remain complete and passing.
- **VALIDATE**: `scripts/e2e-verify.sh`

### task_22 RUN full validation and launch-gate checks

- **IMPLEMENT**: build, format, unit, integration, frontend lint/type/build, Playwright, live smoke where needed; document pen test/load test as launch gates.
- **PATTERN**: `AGENTS.md` Validation section.
- **GOTCHA**: Do not run long live/build commands without operator readiness in normal coding sessions; ask first if required by harness policy.
- **VALIDATE**: `dotnet build 3commerce.sln && dotnet format --verify-no-changes && dotnet test 3commerce.sln && dotnet test tests/ --filter Category=Integration`

---

## TESTING STRATEGY

### Unit Tests

- Domain invariants for Tenant, Principal, PermissionAssignment, ServiceAccount, Entity, Supplier, PaymentAccount, PayoutInstruction, InventoryReservation, Campaign, Promotion, Approval, AuditHashChain.
- Field-level policy decision tests: visible/editable/masked/reveal/approval/sensitive-read outcomes.
- Pricing/promotion/tax calculation tests, including tax-inclusive AU GST default and best-discount-wins.
- Ledger balance and supplier payable reversing entries.
- Carrier adapter fake tests for quotes/expiry/revalidation/labels/tracking.

### Integration Tests

- RLS cross-tenant isolation with Testcontainers Postgres: missing/wrong tenant context fails closed; MasterGlobal bypass explicit/audited.
- Gateway host/domain -> tenant/storefront resolution, canonical redirects, private/noindex behavior.
- Checkout saga: multi-shipment, live quote revalidation, inventory reservation, payment capture, order confirmation, fulfillment events.
- Approval workflow: service creates pending change, Workflow routes task, human approver applies; requester cannot approve; service account cannot approve.
- Audit: domain mutation + local audit + outbox event same transaction; central audit eventually projects.
- Analytics: batched ingestion idempotency, consent snapshot, campaign touch/click/conversion last-click attribution.
- Webhooks: signed outbound delivery/retry logs; inbound provider idempotency.

### E2E / Browser Tests

- MasterGlobal creates tenant, TenantOwner, storefront draft, domain mapping, readiness checks.
- Tenant admin creates entity/supplier, uploads product/stock, assigns product to storefront, publishes, previews.
- Customer browses storefront, consent banner, campaign link/short link, cart, checkout with shipping groups, payment, account order view.
- Supplier portal logs in, views assigned products, uploads stock feed, submits bank/user change request.
- Admin approval queue handles supplier bank change/live activation/customer dashboard toggle.

### Edge Cases

- Removing/demoting last MasterGlobal or TenantOwner is blocked.
- Service account tries to approve high-risk change; denied/audited.
- PDP unavailable: high-risk fails closed; low-risk cached read logs cached decision.
- Shipping quote expires/changes before payment; customer reconfirm required.
- Cart with multiple fulfillment sources, one carrier down, fallback missing -> checkout blocked.
- Tenant suspended: selling/automation disabled but limited remediation read access remains.
- Storefront paused: other storefronts unaffected; campaign link shows paused page.
- Duplicate entity override with reason; no merge.
- Product feed excludes private/unpublished/ineligible products and never exposes supplier cost.
- Sensitive field reveal by MasterGlobal requires reason; non-retrievable secrets never revealed.

---

## VALIDATION COMMANDS

Execute every command to ensure zero regressions and 100% feature correctness.

### Level 1: Syntax & Style

```bash
dotnet build 3commerce.sln
dotnet format --verify-no-changes
cd src/Storefront && npm run lint && npx tsc --noEmit
```

### Level 2: Unit Tests

```bash
dotnet test 3commerce.sln
```

### Level 3: Integration Tests

```bash
dotnet test tests/ --filter Category=Integration
```

### Level 4: Full Regression

```bash
scripts/e2e-verify.sh
```

### Level 5: Live/Browser Validation

Requires stack running and explicit operator readiness:

```bash
scripts/e2e-verify.sh --live
cd src/Storefront && npm run test:e2e
```

### Level 6: Manual Validation

- MasterGlobal tenant creation, TenantOwner assignment, tenant context switching.
- Storefront domain/canonical resolution via Gateway.
- Admin field masking/reveal with reason.
- CLI human MasterGlobal broad command and service account narrow command.
- Supplier portal stock upload.
- Checkout with multi-shipment live fake carrier quotes and payment.
- Audit central search for all above actions.

---

## ACCEPTANCE CRITERIA

- [ ] Tenants are strict-isolated by application checks and PostgreSQL RLS.
- [ ] Tenant-owned data includes TenantId; global/system tables are explicitly justified.
- [ ] Gateway resolves domain -> tenant/storefront and attaches trusted context.
- [ ] Identity/Authz PDP returns batched action/field decisions with approval/sensitive-read metadata.
- [ ] Services enforce PEP decisions server-side for actions, input fields, output fields, exports, and sensitive reads.
- [ ] Human MasterGlobal has broad access without approval, but high-risk actions require reason/audit; sensitive fields masked by default.
- [ ] Service accounts are hash-secret, scoped, revocable, audited, and cannot approve maker-checker.
- [ ] CLI is installable as .NET global tool and supports human/service auth, explicit scopes, output formats, confirmations, idempotency.
- [ ] Entity service supports central tenant-scoped party model and supplier onboarding.
- [ ] Supplier portal exists and is scoped/permissioned.
- [ ] Catalog/storefront support product variants, bundles, storefront assignment, price overrides, publication readiness.
- [ ] Ordering uses CheckoutAttempt before payment and creates Orders only after payment success.
- [ ] Fulfillment supports inventory reservations, multi-shipment/package, Australia Post/DHL/fake carrier quote/label/tracking model with toggles.
- [ ] Payments posts supplier payable obligations into double-entry ledger according to policy.
- [ ] Marketing/Analytics collects batched behavior/campaign events, supports short links, consent snapshots, last-click reporting, and future analytics store path.
- [ ] Storefronts provide SEO/agent metadata, JSON-LD, sitemaps, robots, `llms.txt`, product feeds, and WCAG 2.2 AA target.
- [ ] Audit logs are local authoritative + central projection, append-only, hash-chained, and include sensitive reads/high-risk denied attempts.
- [ ] Workflow service uses typed Quartz/MassTransit schedules; no arbitrary SQL/proc/shell jobs.
- [ ] Admin/CLI exports are permissioned, audited, and async for large/sensitive data.
- [ ] All validation commands pass.
- [ ] API contracts, ADRs, AGENTS project structure, and e2e-verify coverage are updated.

---

## COMPLETION CHECKLIST

- [ ] All phases decomposed into executable sub-plans before coding.
- [ ] All tasks completed in dependency order.
- [ ] Each task validation passed immediately.
- [ ] All validation commands executed successfully.
- [ ] Full test suite passes.
- [ ] No linting/type checking errors.
- [ ] Manual validation confirms critical flows.
- [ ] Acceptance criteria all met.
- [ ] Docs/api, ADRs, AGENTS.md, plan status, and e2e-verify are current.
- [ ] Security-sensitive changes reviewed against ASVS and pen-test launch gate.

---

## NOTES

### Service ownership map

- Identity/Authz: tenants, principals, service accounts, memberships, roles, permissions, PDP, entitlements, MFA policy, tenant/storefront registry basics/global routing metadata.
- Entity: legal entities/parties, identifiers, addresses, contacts, relationships, role/profile metadata.
- Catalog: products, variants, bundles, identifiers, categories, storefront assignments, base/storefront price data, publication/SEO readiness, product availability projection.
- Ordering: carts, checkout attempts, final pricing/tax/promotion orchestration, order creation/snapshots, customer order views.
- Payments: payment accounts, ledger, supplier bank accounts, payout instructions, payable obligations, Xero mappings/sync.
- Fulfillment: inventory locations/reservations, shipping quote/label/tracking integrations, shipments/packages.
- Support: tickets, RMA, support/RMA workflow, customer/supplier/admin support context.
- Audit: central projection/search/export; local audits remain in owning service.
- Workflow: schedules, job runs, approval task orchestration.
- Marketing/Analytics: campaigns, links, events, attribution, behavior analytics, reporting projections, product feed generation schedule integration.
- Notifications: email first, later in-app/webhook/SMS; outbound webhook channel initially.

### Research notes

- PostgreSQL RLS plus application-level authorization is defense-in-depth; RLS alone is not enough for field/action policies.
- `SET LOCAL` inside transactions is the safest fit for pooled connections.
- MassTransit supports scheduler-based scheduling with Quartz; use typed messages/jobs rather than arbitrary admin scripts.
- Google product structured data and Merchant Center feeds can include price, availability, shipping, and return details; do not emit review/aggregate rating data unless real.
- `llms.txt` is an emerging proposal, not guaranteed SEO ranking factor; include it as agent-friendly metadata without relying on it for discovery.
- Australia Post Shipping/Tracking API requires appropriate account/contract access; DHL APIs require tenant/provider account onboarding. Fake carrier is mandatory for dev/test.

### Implementation risk

This plan introduces multiple platform primitives. The biggest risks are authorization leakage, RLS connection-pool mistakes, over-coupled PDP/service calls, checkout/shipping/payment race conditions, event-volume overload, and admin/CLI exposing sensitive fields. Mitigate by implementing Phase 1 first, failing closed on high-risk policies, writing isolation tests early, and keeping each later phase as vertical slices.

### Confidence score

**6/10** for one-pass success if attempted as one giant execution.  
**8/10** if each phase is decomposed into its own implementation plan and validated before moving to the next phase.
