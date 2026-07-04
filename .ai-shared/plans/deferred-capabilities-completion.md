# Feature: Deferred capabilities completion — MFA, webhook registry, and the post-launch parking lot

The following plan should be complete, but validate documentation and codebase patterns and task sanity before implementing. Pay special attention to naming of existing utils, types, and models. Import from the right files.

## Feature Description

The rev_13 deferral triage (2026-07-04) turned every documented capability-first deferral into a dated decision. The user has now directed that **all of the code items be completed** — the two scheduled ones (MFA enrollment, webhook secret registry) and the four parked post-launch ones (CLI real auth, analytics events endpoint, publishing persistence, mission-control live stats). Each capability already has its domain half shipped and tested; this program ships the missing enforcement/persistence/wiring half. Out of scope (unchanged from triage): real Stripe/Xero/carrier credential swaps and the external pen test (non-code launch gates), and Pricing/Entitlement/Usage service extraction (parked on scale trigger).

## User Story

As a **platform operator preparing 3commerce for real tenants**
I want **operator accounts protected by a second factor, webhook secrets managed per provider, and the parked convenience/observability surfaces finished**
So that **nothing in the deferred backlog remains between the platform and launch except the external credential/pen-test gates.**

## Problem Statement

Six capabilities exist as tested domain primitives with no service wiring: (1) `MfaPolicy`/`StepUp` model exists but no factor can actually be enrolled or verified — operator accounts guarding money/PII are password-only; (2) `InboundWebhookVerifier` exists but Stripe's signing secret is a single config key (`Stripe:WebhookSecret`) with no registry, rotation, or admin surface; (3) the CLI's command groups print placeholder fidelity — no login, no real HTTP; (4) the storefront consent/analytics batcher (`lib/analytics.ts`) is a deliberate no-op — Marketing has no events endpoint; (5) `PublishableContent` (draft/publish/schedule domain, fully tested) has no persistence, endpoints, scheduler wiring, or preview route; (6) MissionControl.razor's Message Bus section renders a static scaffold — no live queue stats.

## Solution Statement

Ship each capability as its own PR, ordered by launch-criticality: MFA first (pre-launch gate), webhook registry second (scheduled), then the four parked items. Follow every repo invariant: enums numeric on the wire, outbox publish-before-SaveChanges, FORCE RLS on new TenantId tables, OpenAPI + api_contracts_index regeneration in the same PR, `dotnet format` on Infrastructure after `ef migrations add`, tracker updated in the same change.

## Feature Metadata

**Feature Type**: Enhancement (capability completion)
**Estimated Complexity**: Large overall; each PR is Medium and independently shippable
**Primary Systems Affected**: Identity (MFA), Payments (webhook registry), Cli, Marketing + Storefront (analytics, publishing), Admin (MFA UI, mission control), docs/PRD
**Dependencies**: None external. TOTP is implemented in-domain (RFC 6238 over HMAC-SHA1, ~30 lines) — no new package.

---

## CONTEXT REFERENCES

- `src/Services/Identity/Domain/MfaPolicy.cs` — MfaRequirement floor/ceiling model + `StepUp` freshness window (mt6_10). The enforcement must consume exactly this.
- `src/Services/Identity/Api/Endpoints/AuthEndpoints.cs` + `ProfileEndpoints.cs` — login/session shape, claims minting; MFA challenge inserts here.
- `src/BuildingBlocks/Infrastructure/Webhooks/InboundWebhookVerifier.cs` — reusable HMAC verifier (mt6_7); the registry feeds it.
- `src/Services/Payments/Infrastructure/Stripe/StripePaymentProvider.cs:25` — `Stripe:WebhookSecret` config read to be replaced by registry-with-config-fallback.
- `src/Cli/3commerce.Cli/Program.cs` (317 lines) — command groups with placeholder fidelity; gateway base URL context exists.
- `src/Services/Marketing/Api/Endpoints/` (Campaign/ShortLink only) + `src/Services/Marketing/Domain/PublishableContent.cs` + `src/Services/Marketing/tests/StorefrontPublishTests.cs` — publishing domain done; persistence/endpoints missing.
- `src/Storefront/lib/analytics.ts` + `lib/consent.ts` — consent-gated batcher awaiting an endpoint.
- `src/Admin/Components/Pages/MissionControl.razor` — four-section scaffold; Message Bus section needs live data (RabbitMQ management API :15672).
- `docs/prd.md` Appendix B — launch gates list (MFA row to update on def_1 completion).

## Relevant Patterns

Numeric enums over HTTP; `UseApiProblemDetails`; outbox publish-before-save; FORCE RLS `nullif(current_setting('app.tenant_id',true),'')::uuid` for new TenantId tables; `InternalClaimsAuth.CanActForTenant`; `AdminRenderModes.InteractiveServerNoPrerender` for new admin pages; OpenAPI regen via `--no-launch-profile` ephemeral hosts; integration tests `Category=Integration` (Testcontainers).

---

## IMPLEMENTATION PLAN (PR streams, ordered)

### PR A — MFA enrollment + enforcement (def_1, pre-launch gate)
Identity Domain: `Totp` (RFC 6238, HMAC-SHA1, 30s step, ±1 window; RFC test vectors in unit tests) + `MfaEnrollment` on the user (Base32 secret, Enabled flag, hashed recovery codes). Api: `POST /auth/mfa/enroll/begin` (authenticated → secret + `otpauth://` URI), `POST /auth/mfa/enroll/confirm` (code proves possession → Enabled), `POST /auth/mfa/challenge` (completes a pending MFA login), `POST /auth/step-up` (re-verify for sensitive actions per `StepUp.DefaultFreshness`). Login flow: when `MfaPolicy.RequiresMfa(isPrivileged)` and user enrolled → session marked pending-MFA until challenge passes; claims gain `amr` + `auth_time`. Recovery-code login path. Admin UI: profile MFA enrollment section (QR-less: show otpauth URI + secret; authenticator apps accept manual entry) + tenant MFA policy setting where tenant settings live. Migration (+ `dotnet format` Infrastructure). PRD Appendix B: MFA gate → shipped. Tests: TOTP vectors, enrollment lifecycle, login-requires-challenge integration, policy floor.

### PR B — Per-provider webhook secret registry (def_2)
Payments: `WebhookSecret` table (Provider, masked secret-at-rest, Active, CreatedAt/RotatedAt; platform-scoped — webhooks carry no tenant). Admin endpoints: list (masked), create/rotate (returns value once), deactivate. `StripePaymentProvider` resolves the active registry secret, falls back to `Stripe:WebhookSecret` config so dev keeps working. Admin page or CommerceOps card. Migration + OpenAPI + tests (rotation swaps verification; fallback preserved).

### PR C — CLI real auth + HTTP fidelity (def_3)
`3c login` → gateway Identity login, persists session token/cookie to `~/.3commerce/config.json` (0600) + `3c logout`. Command groups issue real gateway HTTP with stored auth; `--tenant` guard maps to the platform-scope header convention. Errors surface problem-details cleanly. Smoke-testable against the dev stack.

### PR D — Analytics events endpoint + track() wiring (def_4)
Marketing: `AnalyticsEvent` persistence + anonymous batch `POST /events` (size-capped, per-IP rate-limited, numeric enum kinds). Storefront: `analytics.ts` batcher posts real events strictly behind Analytics consent; consent-settings page (`/account/privacy` or footer route) using existing `consent.ts` categories. Gateway route for `/api/marketing/events` if not covered. Migration + OpenAPI + integration test (batch accepted; no-consent → no network call is a storefront unit concern).

### PR E — Publishing persistence + endpoints + scheduling + preview (def_5)
Marketing: persist `PublishableContent` (marketing schema, TenantId → FORCE RLS). Admin endpoints: create/save-draft/publish/schedule/list + content read. Scheduled publish via the existing persistent Quartz scheduler pattern (Payments' ScheduledJobRuns precedent). Storefront preview route with a signed/short-lived draft token. Migration + OpenAPI + tests (draft→publish→scheduled-publish fires; preview requires token).

### PR F — Mission-control live bus stats (def_6)
Admin: Message Bus section reads the RabbitMQ management API (queue depth/rates/consumers/DLQ) via a small server-side client (config: management URL + creds; dev defaults for the compose stack). Wiretap timeline scoped to recent message names/timestamps if cheaply available; otherwise present depth/rate cards and keep the Grafana link as the deep-dive. Degrades gracefully when the management API is unreachable.

## STATUS TRACKER ROWS

def_1..def_6 added to `.ai-shared/plans/plan_status_executions.md` as `pending`, flipped in the same change as each PR per the tracker rule.

## VALIDATION

Per PR: `dotnet build 3commerce.sln && dotnet format --verify-no-changes && dotnet test 3commerce.sln`; integration suite `Category=Integration`; storefront `npm run lint && npx tsc --noEmit` when touched; OpenAPI drift check; merge-on-green via required gates (build-test/integration/changes).
