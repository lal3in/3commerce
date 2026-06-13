# Feature: Phase 2 — Identity & Catalog (custom auth end-to-end, catalog + importer + search, storefront v0)

> **PRE-EXECUTION NOTE:** written before Phase 1 executed. Before implementing, verify Phase 1 artifacts exist (BuildingBlocks helpers, ports, namespace convention `ThreeCommerce.*`) and adjust file paths/names to reality.

The following plan should be complete, but its important that you validate documentation and codebase patterns and task sanity before you start implementing.

Pay special attention to naming of existing utils types and models. Import from the right files etc.

## Feature Description

Make users exist and products findable: full custom authentication (register/login/sessions/verification/reset, Argon2id) flowing browser → gateway → internal claims; Catalog with neutral schema, `ISupplierImporter` seeding ≥ 10k SKUs, Postgres FTS + pg_trgm search behind `ISearchProvider`; and a first SSR Next.js storefront (category/product/search pages) through the gateway.

## User Story

As a shopper
I want to register, verify my email, log in, and find products via typo-tolerant search among thousands of SKUs
So that I can manage my details and locate what to buy (checkout follows in Phase 3).

## Problem Statement

Phase 1 delivered plumbing only — no identity, no products, no UI. Auth is the security-critical custom build (ADR-0012 conditions are binding); catalog scale (10k SKUs) must prove search performance (NFR-5).

## Solution Statement

Identity service implements sessions + Argon2id behind `IAuthService`; the gateway gains its session-validation middleware (cookie → introspection with ≤60s cache → short-lived internal JWT). Catalog implements the neutral product schema with a deterministic sample-data importer and SQL-native search. Storefront renders SSR pages per `docs/reference/components.md`.

## Feature Metadata

**Feature Type**: New Capability
**Estimated Complexity**: High (security-critical auth + search performance)
**Primary Systems Affected**: Identity, Gateway, Catalog, Notifications worker, Storefront (new)
**Dependencies**: Konscious.Security.Cryptography (Argon2id), Microsoft.IdentityModel.JsonWebTokens, Next.js + Tailwind + shadcn/ui, an email provider sandbox (behind `IEmailSender`)

---

## CONTEXT REFERENCES

### Relevant Codebase Files IMPORTANT: YOU MUST READ THESE FILES BEFORE IMPLEMENTING!

- `docs/adr/0012-custom-auth-opaque-cookie-internal-claims.md` — Why: binding token design + prohibitions (no hand-rolled crypto, `IAuthService` seam).
- `docs/adr/0013-guest-checkout-optional-accounts.md` — Why: v1 auth feature scope; what NOT to build (MFA/social).
- `docs/adr/0004-neutral-catalog-schema-supplier-importer.md` + `docs/adr/0020-postgres-fts-search.md` — Why: catalog schema and search design.
- `docs/prd/3commerce/09-security-configuration.md` — Why: full security checklist (lockout, enumeration, revocation ≤60s, header stripping).
- `docs/prd/3commerce/10-api-specification.md` — Why: Identity + Catalog endpoint contracts.
- `docs/reference/api.md` and `docs/reference/components.md` — Why: mandatory endpoint and component standards.
- `src/BuildingBlocks/Infrastructure/**` (from Phase 1) — Why: reuse `AddServiceBus`, `AddServiceTelemetry`, ProblemDetails, health helpers — do not reinvent.
- `src/Gateway/Program.cs` (from Phase 1) — Why: contains the marked `// PHASE2: session validation middleware` insertion point.

### New Files to Create

- Identity: `Domain/{User,Session,EmailToken}.cs`, `Domain/IAuthService.cs` + `Infrastructure/AuthService.cs` (Argon2id via library), `Api/Endpoints/{Auth,Profile,Address}Endpoints.cs`, `Api/Internal/IntrospectionEndpoint.cs` (internal-only), migrations
- Gateway: `Auth/SessionValidationMiddleware.cs`, `Auth/InternalClaimsMinter.cs` (≤5 min JWT, key from config), positive-cache (in-memory, 60s TTL)
- Catalog: `Domain/{Product,Variant,Category,ImportRun}.cs`, `Domain/ISupplierImporter.cs`, `Infrastructure/Importers/SampleDataImporter.cs` (deterministic seed, ≥10k SKUs, includes invalid rows to exercise rejections), `Domain/ISearchProvider.cs` + `Infrastructure/Search/PostgresSearchProvider.cs` (tsvector + pg_trgm migration with GIN indexes), `Api/Endpoints/{Products,Categories,AdminCatalog,AdminImports}Endpoints.cs`
- Notifications: `IEmailSender` + sandbox implementation + templates (verify, reset)
- Contracts: `Catalog/ProductUpserted.cs`, `Catalog/ProductPriceChanged.cs`, `Identity/UserRegistered.cs`
- Storefront: `src/Storefront/` Next.js app — `app/(shop)/{page,products/[slug]/page,categories/[slug]/page,search/page}.tsx`, `app/(account)/{login,register,account}/...`, `components/ui/` (shadcn init), `components/catalog/{ProductCard,SearchBar,FilterPanel}.tsx`, `lib/{gateway.ts,money.ts}`, Server Actions for auth forms

### Relevant Documentation YOU SHOULD READ THESE BEFORE IMPLEMENTING!

- [OWASP Password Storage Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html) — Why: Argon2id parameter selection (binding per ADR-0012).
- [Konscious.Security.Cryptography](https://github.com/kmaragon/Konscious.Security.Cryptography) — Why: Argon2id API usage.
- [Postgres full-text search](https://www.postgresql.org/docs/current/textsearch-controls.html) + [pg_trgm](https://www.postgresql.org/docs/current/pgtrgm.html) — Why: tsvector weighting (`setweight`), `websearch_to_tsquery`, trigram GIN index + `similarity()` fallback for typos.
- [Next.js — Server and Client Components](https://nextjs.org/docs/app/getting-started/server-and-client-components) and [Server Actions](https://nextjs.org/docs/app/getting-started/updating-data) — Why: storefront standards baseline.
- [shadcn/ui installation (Next.js)](https://ui.shadcn.com/docs/installation/next) — Why: `components/ui` setup.

### Patterns to Follow

- Endpoint classes + MapGroup + TypedResults + built-in validation (`docs/reference/api.md` — definition-of-done checklist applies to every endpoint).
- Outbox for every write+publish (`ProductUpserted` from importer batches; `UserRegistered` → Notifications sends verification email).
- 404-not-403 for ownership misses; generic auth errors (no enumeration).
- Components: Server Components default; auth forms via Server Actions; money via `lib/money.ts`; URL-as-state for search/filters.

---

## IMPLEMENTATION PLAN

### Phase 1: Foundation
Identity domain + Argon2id auth service + sessions; Catalog schema + migrations (incl. FTS columns/indexes).

### Phase 2: Core Implementation
Auth endpoints + email flows; gateway session middleware + claims minting; importer + search provider; admin catalog endpoints.

### Phase 3: Integration
Storefront SSR pages + auth UI through the gateway; ProductUpserted event flow; rate limits at gateway turned real.

### Phase 4: Testing & Validation
ASVS-aligned auth tests, search perf measurement (NFR-5), revocation timing test (NFR-8).

---

## STEP-BY-STEP TASKS

### 1. CREATE Identity domain + AuthService (Argon2id)
- **IMPLEMENT**: `User` (id UUIDv7, email citext unique, hash, verified flag), `Session` (opaque 256-bit token **hash** stored, expiry, revoked), `EmailToken` (purpose, single-use, expiry). `AuthService` behind `IAuthService`: register/login/logout/verify/reset; Argon2id params per OWASP (document chosen m/t/p in code constant with comment); progressive lockout counters.
- **GOTCHA**: store only the SHA-256 of session tokens (DB leak ≠ session theft); constant-time comparisons; CSPRNG (`RandomNumberGenerator`) for tokens.
- **VALIDATE**: `dotnet test src/Services/Identity --filter FullyQualifiedName~AuthService`

### 2. CREATE Identity endpoints + internal introspection
- **IMPLEMENT**: per PRD §10 (`/register`, `/login`, `/logout`, `/verify-email`, `/password-reset/*`, `/me`, `/me/addresses`); login sets `Secure; HttpOnly; SameSite=Lax` cookie. Introspection endpoint on a **separate internal-only port/listener** (not gateway-routed).
- **VALIDATE**: `curl -i -X POST localhost:5101/register -d '{...}'` → 201 + verification event in Notifications log

### 3. ADD gateway session validation + internal claims
- **IMPLEMENT**: at the marked insertion point — read cookie → 60s positive cache → introspect → mint ≤5 min JWT (`sub`, `role`, `session_id`) into `X-Internal-Claims`; strip the header from all inbound requests **before** middleware; real rate limits (per-IP on auth routes).
- **GOTCHA**: cache must key on token hash and honor revocation ≤60s (NFR-8 test); JWT signing key from config — services get only the public key.
- **VALIDATE**: integration test: login → call protected route → revoke session → within 60s same call → 401

### 4. ADD BuildingBlocks internal-claims validation for services
- **IMPLEMENT**: `AddInternalClaimsAuth()` extension (JWT bearer from header, role policies "Customer"/"Admin"); apply to Identity `/me*` and Catalog `/admin*` groups.
- **VALIDATE**: `curl localhost:5102/admin/products` direct without header → 401; via gateway with admin session → 200

### 5. CREATE Catalog schema + FTS migration
- **IMPLEMENT**: Product/Variant/Category/ImportRun per ADR-0004 (JSONB `attributes`, `supplier_ref`); migration adds generated `search_vector` tsvector column (weighted title A / brand B / description C), GIN index, `pg_trgm` extension + trigram index on title.
- **GOTCHA**: enable extension in migration (`CREATE EXTENSION IF NOT EXISTS pg_trgm`) — requires superuser in the init script grant.
- **VALIDATE**: `dotnet ef database update ...` then `psql ... -c "\d products"` shows indexes

### 6. CREATE ISupplierImporter + SampleDataImporter (≥10k SKUs)
- **IMPLEMENT**: interface per ADR-0004; deterministic faker-style generator (seeded RNG) producing ~10.5k rows with ~0.4% invalid (missing price etc.) to exercise rejection paths; persists ImportRun counts; publishes `ProductUpserted` per batch via outbox.
- **VALIDATE**: `curl -X POST localhost:8080/api/catalog/admin/import-runs` (admin) → run row with accepted≈10000, rejected>0

### 7. CREATE ISearchProvider + PostgresSearchProvider + public endpoints
- **IMPLEMENT**: `websearch_to_tsquery` primary, trigram `similarity()` fallback when zero hits; JSONB attribute filters; category scoping; pagination per api.md. Endpoints `/products`, `/products/{slug}`, `/categories`.
- **VALIDATE**: `curl 'localhost:8080/api/catalog/products?q=blutooth'` returns bluetooth items; `time` it at <500ms

### 8. CREATE Storefront (Next.js) v0
- **IMPLEMENT**: `create-next-app` (TS, Tailwind, App Router) in `src/Storefront`; shadcn init; SSR pages: home (featured), category browse, product detail (ISR), search (dynamic, URL-state); register/login/account pages via Server Actions calling the gateway (cookie passthrough per components.md §2); `lib/money.ts` formatter.
- **GOTCHA**: Server Action fetches must forward `Set-Cookie` back to the browser response — use the cookies() API, never expose gateway internals client-side.
- **VALIDATE**: `cd src/Storefront && npm run lint && npx tsc --noEmit && npm run build`; manual: register→verify (sandbox link)→login→see account page

### 9. ADD search performance measurement + NFR tests
- **IMPLEMENT**: integration tests: search p95 over 100 queries < 500ms at seeded scale (NFR-5); revocation ≤60s (NFR-8); enumeration-resistance (register existing email → same response shape).
- **VALIDATE**: `dotnet test tests/3commerce.IntegrationTests --filter Category=Integration`

### 10. UPDATE docs per repo Rules
- **IMPLEMENT**: export Identity/Catalog OpenAPI contracts → `docs/api/` + index; AGENTS.md structure additions (Storefront now real); ADR only if deviations.
- **VALIDATE**: `ls docs/api/ | grep -c json` ≥ 2

---

## TESTING STRATEGY

### Unit Tests
AuthService (hashing roundtrip, lockout progression, token single-use), SampleDataImporter (deterministic counts, rejection reasons), search query builder (SQL shape).

### Integration Tests
Full auth lifecycle (register→verify→login→me→logout→401); gateway middleware (cache, revocation timing, header stripping); importer→`ProductUpserted`→consumer; search relevance + perf.

### Edge Cases
Duplicate registration (no enumeration); expired/reused verification token; login during lockout; search with SQL-meta characters (`'; drop`), emoji, >100 char queries; importer rerun idempotence (upsert, not duplicate).

---

## VALIDATION COMMANDS

### Level 1: Syntax & Style
```bash
dotnet build 3commerce.sln && dotnet format --verify-no-changes
cd src/Storefront && npm run lint && npx tsc --noEmit
```
### Level 2: Unit Tests
```bash
dotnet test 3commerce.sln --filter Category!=Integration
```
### Level 3: Integration Tests
```bash
dotnet test tests/3commerce.IntegrationTests --filter Category=Integration
```
### Level 4: Manual Validation
```bash
# infra + Identity + Catalog + Gateway + Notifications + storefront dev server
# Browser: register → sandbox verify link → login → account page shows profile
# Search "blutooth hedphones" → relevant results; filter by attribute; paginate
curl -s 'localhost:8080/api/catalog/products?q=headphones&pageSize=5' | jq '.[]|.title'
```

---

## ACCEPTANCE CRITERIA

- [ ] FR-1, FR-2, FR-8 demonstrably pass (see `docs/prd/3commerce/11-success-criteria.md`)
- [ ] NFR-5 (search p95 <500ms @10k), NFR-6 (cookie/hash checks), NFR-8 (revocation ≤60s) test-enforced
- [ ] No MFA/social/passkeys built (ADR-0013 scope discipline)
- [ ] Argon2id parameters documented; no custom crypto anywhere (grep audit: no `SHA` on passwords)
- [ ] Storefront pages SSR (view-source shows product HTML)
- [ ] All endpoint/component definition-of-done checklists (reference docs) honored

## COMPLETION CHECKLIST

- [ ] All tasks completed in order; validations passed immediately
- [ ] Full suite green; lint/typecheck clean (C# + TS)
- [ ] Manual auth + search flows confirmed in browser
- [ ] `docs/api/` contracts + indexes updated
- [ ] `.ai-shared/plans/plan_status_executions.md` updated per task

## NOTES

- Email provider: pick any transactional sandbox (kept behind `IEmailSender`); do not couple templates to provider.
- Defer admin UI for imports to Phase 4 (Blazor) — Phase 2 admin surface is API-only.
- Confidence: 7/10 — auth middleware timing/cache subtleties and FTS relevance tuning are the likely friction points.
