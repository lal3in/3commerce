# Feature: Multi-Tenant Platform Expansion — Phase 5 Marketing/Analytics/Theme/SEO

Parent plan: `.ai-shared/plans/multi-tenant-platform-expansion.md`

## Feature Description

Add dedicated Marketing/Analytics service, campaign management, short links, behavior analytics, consent-aware event collection, attribution, storefront theme/template framework, draft/preview/publish, SEO/agent-friendly metadata, `llms.txt`, and product feeds.

## User Story

As a tenant marketer/admin
I want campaigns, short links, behavior analytics, storefront themes, landing pages, SEO metadata, and product feeds
So that each storefront can be uniquely branded, measurable, search-friendly, and agent-friendly.

## Problem Statement

The current storefront is one SSR Next.js experience with limited SEO/configuration and no first-party marketing analytics. Multi-storefront selling requires configurable experiences, campaign attribution, consent controls, structured metadata, feeds, and safe caching/revalidation.

## Solution Statement

Create Marketing/Analytics as a first-class service and upgrade Storefront rendering to a hybrid model: shared commerce core + configurable templates/design tokens/content blocks + bespoke modules. Add event collector, campaign attribution, short links, consent snapshots, SEO/JSON-LD, product feeds, and ISR revalidation.

## Feature Metadata

**Feature Type**: New Capability / Storefront Enhancement  
**Estimated Complexity**: Very High  
**Primary Systems Affected**: new Marketing/Analytics service, Storefront, Gateway, Catalog, Ordering, Workflow, Admin, CLI, Audit, object storage  
**Dependencies**: Phase 1 tenant/context/PDP, Phase 3 storefront/product model, Phase 6 Workflow can be implemented before scheduled publish/feed jobs if needed.

---

## CONTEXT REFERENCES

### Relevant Codebase Files IMPORTANT: YOU MUST READ THESE FILES BEFORE IMPLEMENTING!

- `docs/reference/components.md` - Storefront SSR/ISR/component rules.
- `src/Storefront/package.json` - Next.js scripts and versions.
- `src/Storefront/app/*`, `components/*`, `lib/*` - current storefront structure.
- `src/Services/Catalog/Api/Endpoints/ProductsEndpoints.cs` - public product data source.
- Gateway Program and domain context from Phase 1.
- Phase 3 storefront/catalog plan.

### New Files to Create

- `src/Services/Marketing/Api/Endpoints/CampaignEndpoints.cs`
- `src/Services/Marketing/Api/Endpoints/AnalyticsCollectorEndpoints.cs`
- `src/Services/Marketing/Api/Endpoints/ShortLinkEndpoints.cs`
- `src/Services/Marketing/Domain/*`
- `src/Services/Marketing/Infrastructure/MarketingDbContext.cs`
- Storefront theme/template/content components and analytics client.
- Storefront `llms.txt`, sitemap/robots/product feed route handlers.

### Relevant Documentation YOU SHOULD READ THESE BEFORE IMPLEMENTING!

- Next.js App Router docs: https://nextjs.org/docs/app
- Google Product structured data: https://developers.google.com/search/docs/appearance/structured-data/product
- Google Merchant Return Policy: https://developers.google.com/search/docs/appearance/structured-data/return-policy
- Schema.org Product: https://schema.org/Product
- Schema.org OfferShippingDetails: https://schema.org/OfferShippingDetails
- Schema.org MerchantReturnPolicy: https://schema.org/MerchantReturnPolicy
- llms.txt proposal: https://llmstxt.org/
- Google Merchant Center Product Data Specification: https://support.google.com/merchants/answer/7052112

### Patterns to Follow

- Storefront server components by default; client analytics at leaf/browser boundary.
- Public/product/content pages static/ISR where possible; cart/checkout/account dynamic.
- Campaign params must not become cache key explosion.
- Analytics events are append-only/reporting, never financial/order truth.

---

## IMPLEMENTATION PLAN

### Phase 5.1: Marketing/Analytics service

**Tasks:** campaigns, targets, links, event collector, conversions, attribution, projections.

### Phase 5.2: Consent and behavior analytics

**Tasks:** visitor/session IDs, consent snapshots, behavior event schema, retention.

### Phase 5.3: Theme/content framework

**Tasks:** design tokens, templates, content blocks, bespoke module seam, draft/preview/publish/scheduled publish.

### Phase 5.4: SEO/agent/feed surface

**Tasks:** JSON-LD, sitemap/robots/canonical/OpenGraph, `llms.txt`, machine-readable policies/product metadata, Merchant feeds.

---

## STEP-BY-STEP TASKS

### mt5_1 CREATE Marketing/Analytics service

- **IMPLEMENT**: Service projects, named schema, RLS, campaigns, targets, links, events, conversions, projections, OpenAPI.
- **PATTERN**: new service pattern from Phase 2 Entity.
- **GOTCHA**: High-volume behavior analytics should use batched ingestion and async projections.
- **VALIDATE**: `dotnet test src/Services/Marketing/tests/3commerce.Marketing.Tests.csproj`

### mt5_2 ADD campaign targeting and attribution

- **IMPLEMENT**: Storefront/product/landing-path targets, safe `cid`, sanitized external UTM/gclid, configurable attribution windows, store all touches, last-click v1.
- **PATTERN**: tenant/storefront config hierarchy from parent plan.
- **GOTCHA**: Invalid campaign IDs must not break page load.
- **VALIDATE**: `dotnet test src/Services/Marketing/tests/3commerce.Marketing.Tests.csproj --filter Attribution`

### mt5_3 ADD short links

- **IMPLEMENT**: platform global short-link domain route, short codes, destination validation, click record before redirect, disabled/expired behavior.
- **PATTERN**: Gateway routing and Marketing endpoints.
- **GOTCHA**: Prevent open redirects; destinations must match registered storefront domains.
- **VALIDATE**: `dotnet test src/Services/Marketing/tests/3commerce.Marketing.Tests.csproj --filter ShortLink`

### mt5_4 ADD analytics event collector

- **IMPLEMENT**: `POST /api/analytics/events` batch endpoint, schema versions, idempotency, consent snapshot, visitor/session/customer context, append-only JSONB raw event table, projections.
- **PATTERN**: minimal API DTO/validation; PostgreSQL JSONB usage in Catalog.
- **GOTCHA**: Do not store raw IP; hash/truncate; no payment/account form data.
- **VALIDATE**: `dotnet test src/Services/Marketing/tests/3commerce.Marketing.Tests.csproj --filter AnalyticsCollector`

### mt5_5 UPDATE Storefront consent and behavior tracking

- **IMPLEMENT**: configurable cookie/consent banner, Necessary/Analytics/Marketing categories, first-party visitor/session ID, event batching.
- **PATTERN**: `docs/reference/components.md`; client boundary at leaf.
- **GOTCHA**: No full session replay v1; no keystroke logging.
- **VALIDATE**: `cd src/Storefront && npm run lint && npx tsc --noEmit`

### mt5_6 UPDATE Storefront theme/template framework

- **IMPLEMENT**: shared commerce core, configurable themes/templates/design tokens/content blocks, bespoke module seam.
- **PATTERN**: Server-first App Router architecture.
- **GOTCHA**: Do not allow arbitrary unsafe tenant code execution.
- **VALIDATE**: `cd src/Storefront && npm run build`

### mt5_7 ADD draft/preview/publish/scheduled publishing

- **IMPLEMENT**: versioned drafts, admin previews, expiring signed share links, publish/rollback, Workflow scheduled publish commands, cache revalidation events.
- **PATTERN**: Workflow typed commands and Storefront ISR rules.
- **GOTCHA**: Preview pages must be noindex and read-only.
- **VALIDATE**: `dotnet test tests/ --filter StorefrontPublish`

### mt5_8 ADD SEO/agent-friendly metadata

- **IMPLEMENT**: canonical, sitemap, robots, OpenGraph/Twitter, JSON-LD Product/Offer/Shipping/Return/Organization/Breadcrumb/WebSite, `llms.txt`, machine-readable policies/products.
- **PATTERN**: `docs/reference/components.md` SEO/rendering rules.
- **GOTCHA**: Do not emit fake ratings/reviews; private storefronts noindex/restricted files.
- **VALIDATE**: `cd src/Storefront && npm run build`

### mt5_9 ADD product feeds per storefront

- **IMPLEMENT**: toggleable product feeds, public/published eligible products only, shipping/return/offer metadata, Workflow schedule generation.
- **PATTERN**: Catalog storefront product visibility and object storage/export patterns.
- **GOTCHA**: No supplier cost/internal margin in feeds.
- **VALIDATE**: `dotnet test tests/ --filter ProductFeed`

---

## TESTING STRATEGY

- Unit: attribution resolution, campaign targeting, short link validation, event schema validation, consent behavior.
- Integration: analytics batch ingestion, projections, redirect tracking, product feed eligibility, scheduled publish.
- Browser/E2E: campaign short link -> storefront -> add to cart -> purchase -> conversion attributed; private storefront noindex; consent banner accessible.

## VALIDATION COMMANDS

```bash
dotnet build 3commerce.sln
dotnet test src/Services/Marketing/tests/3commerce.Marketing.Tests.csproj
cd src/Storefront && npm run lint && npx tsc --noEmit && npm run build
```

## ACCEPTANCE CRITERIA

- [ ] Marketing/Analytics service exists and owns campaigns/events/attribution.
- [ ] Analytics collector accepts batched events and stores append-only JSONB.
- [ ] Consent snapshots included in events.
- [ ] Short links track clicks and prevent open redirects.
- [ ] Storefront supports hybrid theming and draft/preview/publish.
- [ ] Public pages include SEO/agent metadata and product feeds.
- [ ] Private/non-public storefronts restrict indexing and agent files.

## NOTES

PostgreSQL JSONB raw event storage is v1. Keep event schema export-friendly so ClickHouse/BigQuery/Snowflake/Kafka can be adopted later without changing Storefront event tracking.