# Feature: Recurring-billing purchase gating, direct-debit rails, SCA/3DS2, and full dispute/chargeback lifecycle

The following plan should be complete, but it is important that you validate documentation and codebase
patterns and task sanity before you start implementing. Pay special attention to naming of existing
utils/types/models — import from the right files. This is the **Payments** service: money and auth-sensitive.
Every ledger change must preserve the invariants in `[[3commerce-ledger-posting-invariants]]` (every
`JournalLine` sets `Currency`; net-zero reversal posts no revenue line — `ck_line_one_side`).

## Feature Description

Harden the recurring-payments surface (the "Usage / period reset / auto-billing" open caveat) into a
production-grade billing + dispute pipeline:

1. **Purchase gating** — subscription / periodic-payment products are *viewable* by guests but only
   *purchasable* by a **verified member** who has a usable payment instrument (card **or** direct-debit
   mandate) applicable to the storefront currency.
2. **Direct-debit rails** — ACH (USD), SEPA (EUR), BACS (GBP), BECS (AUD), ACSS (CAD) mandates, selected
   by storefront currency, via Stripe/Polar. Full domain model now; **real provider calls gated** behind
   credentials/mode (mirrors the existing mock/sandbox/production posture).
3. **SCA / 3DS2 under PSD2** — surface the `RequiresAction` next-step for card setup/authorization, and
   support **importing existing 3DS-verified provider tokens** for off-session (merchant-initiated)
   charges so renewals don't re-trigger SCA.
4. **Full dispute/chargeback lifecycle** — listen to and normalize the complete Stripe/Polar event set,
   track dispute sub-status on the payment's gateway-transaction-status field, and on a **lost** dispute
   set the payment to `CHARGEBACK` and create a **void payment record**.
5. **Webhook endpoint re-sync** — when a payment-account profile is activated/deactivated, the provider
   webhook URL/secret can change; re-register + rotate so notifications never silently drop.

## User Story

As a **storefront operator**
I want recurring products to be purchasable only by verified members with a valid card or bank mandate,
and every payment/dispute outcome to be tracked accurately end-to-end,
So that **subscription revenue is collectible, SCA-compliant, and disputes/chargebacks reconcile cleanly
in the ledger.**

## Problem Statement

Today: subscriptions exist and renew, but (a) anyone can attempt a recurring purchase, (b) only cards are
supported — no bank-debit mandates, (c) 3DS/SCA state and token import are not first-class, (d) the webhook
parser drops every non-`PaymentIntent` event so **real** `charge.dispute.*` notifications never arrive
(chargebacks only fire via the dev simulate route), and (e) `PaymentStatus` has no `Chargeback`/`Voided`
terminal states nor any dispute sub-status. Activating/deactivating a payment account does not re-sync the
provider webhook registration.

## Solution Statement

Deliver in four reviewable phases against the existing `IPaymentProvider` seam and `PaymentEventProcessor`,
keeping Postgres the exactly-once source of truth (Redis dedupe stays a fast-path) and reusing the existing
ledger `Sale`/`Refund`/`Chargeback`/`ReverseOf` posting helpers.

## Feature Metadata

**Feature Type**: Enhancement (production hardening of an existing capability)
**Estimated Complexity**: High
**Primary Systems Affected**: Payments (domain, infra, api, migrations), Ordering (checkout gate), Storefront
(purchase UX), Admin (billing management), Identity (verified-member claim), Notifications (mandate emails)
**Dependencies**: Stripe.net (already referenced), Polar adapter, StackExchange.Redis (existing), MassTransit
contracts. Live provider credentials are a **launch gate**, not a code dependency (real calls are mode-gated).

---

## CONTEXT REFERENCES

### Relevant Codebase Files — READ THESE BEFORE IMPLEMENTING

- `src/Services/Payments/Domain/IPaymentProvider.cs` — Why: the provider seam; `PaymentWebhookEvent` record
  and `PaymentWebhookKind` enum (line 54, only 3 kinds) live here. Phase 1 extends both. `ParseWebhook`
  contract (rotation-safe secrets).
- `src/Services/Payments/Infrastructure/PaymentEventProcessor.cs` (line 20) — Why: the single place a payment
  outcome becomes ledger truth; `ProcessAsync` switch on `ev.Kind`. Idempotent via `WebhookInbox` + Redis
  dedupe. Phase 1 adds the new event branches here. Currently handles `PaymentSucceeded`, `PaymentFailed`,
  `ChargebackOpened` only.
- `src/Services/Payments/Infrastructure/Providers/Stripe/StripePaymentProvider.cs` (lines 115-152) — Why:
  `ParseWebhook` maps only `payment_intent.succeeded|payment_failed` and **returns null unless the event
  object is a `PaymentIntent`** — so `charge.dispute.*` and `payment_intent.canceled` are silently dropped.
  Phase 1's core fix. Also `AuthorizeAsync` (SetupFutureUsage/OffSession/PaymentMethod already wired — lines
  36-47) and `CreateSetupIntentAsync` (line 78) are the SCA/token-import seams for Phase 3.
- `src/Services/Payments/Infrastructure/Providers/Polar/PolarPaymentProvider.cs` — Why: mirror all
  provider-side changes here (Polar is the second real rail per the spec).
- `src/Services/Payments/Domain/Payment.cs` (line 5) — Why: `PaymentStatus` enum
  (`Pending|Succeeded|Failed|Refunded|Disputed`) — Phase 1 adds `Chargeback`, `Voided` and a new dispute
  sub-status field. Payment already carries `Provider`, `ProviderCustomerId`, `ProviderPaymentMethodId`,
  `RefundedMinor`, `MethodKind`, `StorefrontId`.
- `src/Services/Payments/Domain/Ledger/Ledger.cs` — Why: reuse `Sale` (line 15), `Refund` (110),
  `Chargeback` (186), `ReverseOf` (363). Chargeback already posts a proportional reversal + provider
  chargeback-fee account. Phase 1's "funds reinstated" (won) uses `ReverseOf` against the prior chargeback
  entry.
- `src/Services/Payments/Api/Endpoints/WebhookEndpoints.cs` — Why: `/webhooks/{provider}` route,
  `ProviderWebhook` handler, dev `SimulateChargeback`. Phase 1 extends the simulate route to the new kinds.
- `src/Services/Payments/Api/Endpoints/CustomerPaymentMethodEndpoints.cs` — Why: `SetupIntent`/`Save`/
  `GetOrCreateCustomerAsync` (provider hardcoded `"stripe"` at line 150 — generalize for Polar/DD in P2/P3).
  `SavedPaymentMethod` is card-only today (Brand/Last4/Exp) — P2 adds mandate fields.
- `src/Services/Payments/Domain/Subscription.cs` + `SubscriptionRenewal.cs` — Why: recurring aggregate; first
  period at checkout, `Renew()` charges via provider. P1's `payment_intent.payment_failed` on a renewal must
  drive `MarkPastDue`.
- `src/Services/Payments/Infrastructure/SubscriptionService.cs` + `Consumers/SubscriptionRequestedConsumer.cs`
  — Why: renewal charge path; P3 off-session charge must use the imported/mandate `ProviderPaymentMethodId`.
- `src/Services/Payments/Api/Endpoints/PaymentAccountAdminEndpoints.cs` (lines 26-37) — Why: `activate`/
  `suspend`/`archive` transitions call `Transition(...)`. P4 hooks webhook re-sync into activate/suspend.
- `src/Services/Payments/Infrastructure/WebhookSecretService.cs` — Why: rotation-safe active-secrets store
  (`GetActiveSecretsAsync`); P4 rotates/registers through here.
- `src/Services/Payments/Domain/PaymentRequest.cs` — Why: already has `SetupFutureUsage`, `PaymentMode`
  (LocalMock/Sandbox/Production), `PaymentProviderMode` (Test/Live), `PaymentOutcome.RequiresAction`,
  `PaymentErrorCode.AuthenticationRequired`, `MockScenario.Requires3ds`, `PaymentAccountSnapshot`. Phase 3
  reuses these — do NOT reinvent SCA state.
- `src/BuildingBlocks/Contracts/Payments/PaymentDisputed.cs` + `src/Services/Ordering/Infrastructure/Consumers/OrderStatusConsumer.cs`
  + `src/Services/Ordering/Infrastructure/Migrations/20260802095718_OrderDisputed.cs` — Why: `PaymentDisputed`
  already flips `Order.Disputed`. P1 adds a terminal `PaymentChargedBack` contract for the lost outcome.

### New Files to Create

- `src/Services/Payments/Domain/Mandate.cs` — direct-debit mandate aggregate (scheme, status, provider ref).
- `src/Services/Payments/Domain/DirectDebitScheme.cs` — enum + currency→scheme applicability map.
- `src/Services/Payments/Domain/VoidPayment.cs` (or extend Payment) — the void record for a lost dispute.
- `src/Services/Payments/Api/Endpoints/MandateEndpoints.cs` — create/confirm/list mandates (customer-scoped).
- `src/BuildingBlocks/Contracts/Payments/PaymentChargedBack.cs` — terminal lost-dispute event.
- Migrations (one per phase, `dotnet ef migrations add ...` then `dotnet format` the Infrastructure csproj —
  see `[[3commerce-ef-migration-format]]`): `DisputeStatusAndTerminalStates`, `Mandates`,
  `ImportedPaymentMethods`, `PaymentAccountWebhookRegistration`.
- Test files mirroring existing ones under `src/Services/Payments/tests/` and
  `tests/3commerce.IntegrationTests/`.

### Relevant Documentation — READ BEFORE IMPLEMENTING

- Stripe Disputes lifecycle & events — https://docs.stripe.com/disputes — Why: exact semantics of
  `charge.dispute.created` (funds withdrawn at creation), `funds_withdrawn`, `funds_reinstated`, `updated`,
  `closed` (`status = won|lost`).
- Stripe Bank Debits (ACH/SEPA/BACS/BECS/ACSS) & mandates — https://docs.stripe.com/payments/bank-debits —
  Why: mandate acceptance, `payment_intent` `processing` state (bank debits settle asynchronously), and
  which `payment_method_types` map to which currency.
- Stripe SCA / 3DS2 / off-session — https://docs.stripe.com/strong-customer-authentication and
  https://docs.stripe.com/payments/save-during-payment — Why: `setup_future_usage=off_session`, `off_session`
  charges, `authentication_required` decline code on MIT.
- ADR-0014 (payment-rail seam), ADR-0039 (payment mode/method kind), ADR-0044 (Redis fast-path) —
  `docs/adr/` — Why: keep the new work consistent with the established money architecture.

### Patterns to Follow

- **Provider-agnostic normalization**: consumers never see Stripe types. Extend `PaymentWebhookEvent` /
  `PaymentWebhookKind`; each adapter's `ParseWebhook` translates its own event names. (`IPaymentProvider.cs`.)
- **Exactly-once**: `WebhookInbox` (Postgres) is the guarantee; Redis dedupe is a positive cache primed only
  *after* commit (`PaymentEventProcessor.cs` top comment). New branches must keep this ordering.
- **Ledger posting**: use `Ledger.*` factories; never hand-build journal lines. Every line carries currency;
  net-zero reversal posts no revenue line. Proportional tax/shipping slice uses banker's rounding
  (`MidpointRounding.ToEven`) — see the existing Chargeback branch in `PaymentEventProcessor.cs`.
- **Mode gating**: real provider calls are guarded by `PaymentMode`/`PaymentProviderMode`; mock/sandbox
  adapters give deterministic behavior for tests (see `MockScenario`, `FakePaymentProvider`).
- **Idempotent consumers**: `SubscriptionRequestedConsumer` pattern — thin consumer delegating to a service.
- **Migrations**: after `dotnet ef migrations add`, run `dotnet format` on the Infrastructure csproj
  (`[[3commerce-ef-migration-format]]`); cap Npgsql pool + container max_connections for integration tests
  (`[[3commerce-integration-test-conn-pools]]`).

---

## IMPLEMENTATION PLAN

### Phase 1 — Dispute/chargeback lifecycle + terminal states (self-contained; no external creds)

Normalize the full event set, add dispute sub-status + `Chargeback`/`Voided`, implement the lost→void rule.
**Accounting model** (recommended, matches Stripe fund movement): post the ledger reversal when funds are
actually withdrawn (`charge.dispute.created`/`funds_withdrawn`), set `Status=Disputed` + `DisputeStatus`
tracking; on `funds_reinstated`/`closed(won)` post a `ReverseOf` the chargeback entry and restore
`Status=Succeeded`; on `closed(lost)` set `Status=Chargeback`, create the void payment record, publish
`PaymentChargedBack`. Keep today's behavior (reversal at open) as the funds-withdrawn branch so no double
reversal occurs.

### Phase 2 — Direct-debit mandates + multi-currency gating (full model, real calls gated)

Supported storefront/settlement currencies for direct debit: **USD, EUR, GBP, AUD, CAD** — each maps to its
rail (USD→ACH, EUR→SEPA, GBP→BACS, AUD→BECS, CAD→ACSS). These five must be first-class *options* end-to-end:
storefront currency selection, checkout, mandate creation, and the ledger (which already carries per-line
`Currency` — `[[3commerce-ledger-posting-invariants]]`). Any other currency falls back to card-only.

`Mandate` aggregate + `DirectDebitScheme` (currency map). Mandate creation returns a provider setup/mandate
intent; `payment_intent` `processing` (async settlement) maps to a new `Processing`/pending settlement path.
Mock/sandbox adapters simulate mandate confirmation deterministically.

### Phase 3 — SCA/3DS2 + token import + verified-member purchase gate

Surface `RequiresAction` to the storefront; add an import path for an existing 3DS-verified provider token
(store as `SavedPaymentMethod`/mandate flagged off-session-ready). Enforce "recurring ⇒ verified member with
a usable instrument in the storefront currency" at checkout (Ordering) and in storefront UX.

### Phase 4 — Webhook endpoint re-sync on account activate/deactivate

On `activate`/`suspend`, (re)register the provider webhook endpoint and rotate the signing secret via
`WebhookSecretService` so the URL change never drops notifications. Record the registration on the account.

---

## STEP-BY-STEP TASKS

Execute in order. Each task is atomic and independently testable.

### Phase 1

1. **UPDATE `src/Services/Payments/Domain/Payment.cs`** — ADD `Chargeback = 6`, `Voided = 7` to
   `PaymentStatus`; ADD `DisputeStatus` enum (`None|Created|UnderReview|FundsWithdrawn|FundsReinstated|Won|Lost`)
   and a `public DisputeStatus DisputeStatus { get; set; } = DisputeStatus.None;` property (the
   "gateway-transaction-status field"). Optionally `public string? ProviderDisputeId { get; set; }`.
   VALIDATE: `dotnet build src/Services/Payments/Api`.
2. **UPDATE `src/Services/Payments/Domain/IPaymentProvider.cs`** — EXTEND `PaymentWebhookKind` with
   `DisputeCreated`, `DisputeUpdated`, `DisputeFundsWithdrawn`, `DisputeFundsReinstated`, `DisputeClosedWon`,
   `DisputeClosedLost`, `PaymentVoided`. ADD to `PaymentWebhookEvent` an optional `string? ProviderDisputeId`
   and `string? DisputeStatusRaw`. GOTCHA: keep existing numeric values stable (wire invariant).
3. **UPDATE `StripePaymentProvider.ParseWebhook`** — handle `payment_intent.canceled`→`PaymentVoided`; and
   for `charge.dispute.*` events (object is `Dispute`, NOT `PaymentIntent`) resolve the `PaymentIntent` id
   from `dispute.PaymentIntent`/`dispute.Charge`, and map created/updated/closed(status)/`funds_reinstated`/
   `funds_withdrawn`. GOTCHA: the current early-return `if (... is not PaymentIntent) return null;` drops
   disputes — restructure to branch on `stripeEvent.Type` first. MIRROR into `PolarPaymentProvider`.
4. **UPDATE `PaymentEventProcessor.ProcessAsync`** — ADD switch branches:
   `PaymentVoided when Status==Pending → Status=Voided` (publish `PaymentFailed`/a void notice);
   `DisputeCreated`/`DisputeFundsWithdrawn` → existing chargeback reversal + `Status=Disputed`,
   `DisputeStatus=FundsWithdrawn` (dedupe so only one reversal posts);
   `DisputeFundsReinstated`/`DisputeClosedWon` → `Ledger.ReverseOf` the prior chargeback entry,
   `Status=Succeeded`, `DisputeStatus=Won`;
   `DisputeClosedLost` → `Status=Chargeback`, `DisputeStatus=Lost`, create void payment record, publish
   `PaymentChargedBack`; `DisputeUpdated` → update `DisputeStatus` only. Preserve WebhookInbox/Redis ordering.
5. **CREATE `src/BuildingBlocks/Contracts/Payments/PaymentChargedBack.cs`** — `(Guid OrderId, string
   PaymentIntentId, long AmountMinor)`. UPDATE `OrderStatusConsumer` to react if a terminal order state is
   needed beyond `Disputed`.
6. **CREATE void payment record** — add a `VoidPayment` row (or `Payment` with `Status=Voided`,
   `AmountMinor` negative-of-original semantics documented) tied to the original `OrderId`/intent. Decide in
   review; default: dedicated `VoidPayment { OriginalPaymentId, OrderId, AmountMinor, Reason, CreatedAt }`.
7. **MIGRATION** `DisputeStatusAndTerminalStates` — `dotnet ef migrations add DisputeStatusAndTerminalStates
   -p src/Services/Payments/Infrastructure -s src/Services/Payments/Api` then `dotnet format` the
   Infrastructure csproj (`[[3commerce-ef-migration-format]]`).
8. **UPDATE dev `SimulateChargeback`** in `WebhookEndpoints.cs` — parameterize the kind so tests can drive
   created→won and created→lost paths; keep distinct EventIds.
9. **TESTS** — unit: `StripePaymentProvider` maps each new event (extend `ProviderAdapterTests`);
   `PaymentEventProcessor` posts exactly one reversal on created, a `ReverseOf` on won, void+status on lost,
   idempotent on redelivery. Integration: dispute created→lost drives `Order.Disputed` + `PaymentChargedBack`
   consumed; ledger nets to expected balances (mirror `ChargebackLedgerTests`).

### Phase 2

10. **CREATE `DirectDebitScheme.cs`** — enum `{ Ach, Sepa, Bacs, Becs, Acss }` + static
    `ForCurrency(string)` → USD→Ach, EUR→Sepa, GBP→Bacs, AUD→Becs, CAD→Acss (null = card only).
    Also verify these five (**USD, EUR, GBP, AUD, CAD**) are selectable storefront currency **options**:
    check the storefront/catalog currency config + checkout currency validation
    (`grep -rniE "currency" src/Services/Catalog src/Services/Ordering`) and add any missing ones so a store
    can be created in each of the five and drive the matching direct-debit rail.
11. **CREATE `Mandate.cs`** — aggregate `{ Id, TenantId, UserId, Provider, Scheme, Currency,
    ProviderMandateId, ProviderPaymentMethodId, Status(Pending|Active|Revoked|Failed), AcceptedAt }` with
    Start/Activate/Revoke transitions. Register in `PaymentsDbContext`.
12. **EXTEND `IPaymentProvider`** — `CreateMandateSetupAsync(customer, scheme, currency, ct)` and mandate
    confirmation parsing (Stripe: bank-debit SetupIntent w/ mandate; async `processing`). Implement real in
    Stripe/Polar (mode-gated via `EnsureApiKey`), deterministic in mock/fake.
13. **CREATE `MandateEndpoints.cs`** — customer-scoped create/confirm/list; storefront-currency validation.
14. **MIGRATION** `Mandates` (+ `dotnet format`). **TESTS**: currency→scheme map; mandate lifecycle;
    async settlement (`processing`) → succeeded via webhook.

### Phase 3

15. **UPDATE checkout (Ordering)** — where a cart line is recurring, require an authenticated **verified**
    member (Identity `email_verified`/member claim) AND a usable instrument (active card or `Mandate`) in the
    storefront currency; reject guests/unverified with a typed problem-detail. Find the checkout endpoint via
    `NormalizePaymentOption` reference in `CheckoutEndpoints`.
16. **ADD token-import path** — endpoint to import an existing 3DS-verified provider `payment_method` token;
    verify off-session usability via the provider and persist as a `SavedPaymentMethod`/mandate flagged
    `OffSessionReady`. Renewals (`SubscriptionService`) charge off-session with it.
17. **SURFACE `RequiresAction`** — return the client secret + next-action to storefront on setup/authorize so
    the Payment Element completes 3DS2; on renewal `authentication_required` → `MarkPastDue` + notify.
18. **STOREFRONT UX** — recurring product page shows "sign in as a verified member to subscribe"; account
    adds mandate/bank-debit setup; `/account/access` already lists entitlements (`[[orders-belong-to-a-storefront]]`
    for storefront attribution). next-intl keys + `<Link>` (no `<a href>`).
19. **MIGRATION** `ImportedPaymentMethods` (+ `dotnet format`). **TESTS**: guest/unverified rejected;
    verified+mandate accepted; imported token charges off-session without SCA; renewal SCA → PastDue.

### Phase 4

20. **UPDATE `PaymentAccountAdminEndpoints` `activate`/`suspend`** — after the domain transition, call a new
    `WebhookRegistrationService` that (re)registers the provider webhook endpoint (real, mode-gated) and
    rotates the signing secret through `WebhookSecretService`, persisting URL + status on the account.
21. **MIGRATION** `PaymentAccountWebhookRegistration` (+ `dotnet format`). **TESTS**: activate registers +
    stores secret; suspend deactivates; rotation keeps old secret valid during overlap (verification already
    accepts any active secret — `ParseWebhook`).
22. **DOCS/WIKI** — update `docs/help/admin-operations.*` (dispute status + mandate mgmt), `deployment.*`
    (bank-debit config, webhook registration), `storefront-operations.*` (subscribe-as-member),
    `project-analysis.*` Usage/Payments open-caveat rows. Keep `.md`↔`.html` in sync; UTF-8 safe (no perl).

---

## TESTING STRATEGY

### Unit Tests (`src/Services/Payments/tests/`, xUnit)
Adapter event mapping (each new kind), processor state machine (created/won/lost/void, idempotent redelivery),
ledger balance assertions (mirror `ChargebackLedgerTests`, `LedgerAttributionTests`), currency→scheme map,
mandate lifecycle, SCA `RequiresAction` surfacing, token-import off-session flag.

### Integration Tests (`tests/3commerce.IntegrationTests/`, Testcontainers)
Full dispute created→lost across Payments+Ordering (Phase4Fixture); renewal off-session charge; guest/
unverified recurring-purchase rejection; webhook re-sync on activate. Cap Npgsql pool + container
max_connections (`[[3commerce-integration-test-conn-pools]]`).

### Edge Cases
Dispute after partial refund (only un-refunded gross reverses — existing guard); dispute after full refund
(record-only, no reversal); won after withdrawn (exactly one reverse); lost when already refunded; duplicate
`charge.dispute.*` deliveries; bank-debit async `processing` then late failure; secret rotation overlap;
$0 recurring line (no journal entry — existing guard).

## VALIDATION COMMANDS

### Level 1 — Syntax & Style
```bash
dotnet build 3commerce.sln
dotnet format --verify-no-changes src/Services/Payments/Infrastructure/*.csproj
```
### Level 2 — Unit
```bash
dotnet test src/Services/Payments/tests/*.csproj
```
### Level 3 — Integration
```bash
dotnet test tests/3commerce.IntegrationTests/*.csproj --filter "FullyQualifiedName~Dispute|FullyQualifiedName~Mandate|FullyQualifiedName~Subscription|FullyQualifiedName~Webhook"
```
### Level 4 — Manual
Dev simulate: `POST /dev/simulate-chargeback/{intentId}` (created→lost) and assert payment
`Status=Chargeback`, `DisputeStatus=Lost`, a `VoidPayment` row exists, and `Order.Disputed=true`.

## ACCEPTANCE CRITERIA
- [ ] Full Stripe/Polar dispute + `payment_intent.*` event set is parsed (no silent drops).
- [ ] Dispute sub-status tracked on the payment; `Chargeback`/`Voided` terminal states exist.
- [ ] Lost dispute → `Status=Chargeback` + void payment record + `PaymentChargedBack` published.
- [ ] Won/funds-reinstated reverses exactly one chargeback entry; ledger nets correctly.
- [ ] Direct-debit mandates by storefront currency (ACH/SEPA/BACS/BECS/ACSS); real calls mode-gated.
- [ ] SCA `RequiresAction` surfaced; existing 3DS-verified token importable + charges off-session.
- [ ] Recurring purchase blocked for guests/unverified; allowed for verified members with a usable instrument.
- [ ] Account activate/suspend re-syncs webhook endpoint + rotates secret without dropping notifications.
- [ ] All ledger invariants hold; all validation commands pass; wiki updated (.md↔.html in sync).

## COMPLETION CHECKLIST
- [ ] All phase tasks complete, validated per-task.
- [ ] Full unit + integration suites green (no red merged around — `[[all-tests-must-pass-no-gaps]]`).
- [ ] Migrations formatted (`[[3commerce-ef-migration-format]]`); no AI-authorship trailers on commits/PRs
      (`[[no-ai-authorship-trailers]]`); CI gates via poller (`[[3commerce-ci-merge-workflow]]`); branch
      created before committing.

## NOTES
- **Accounting decision to confirm at Phase-1 review**: reverse funds at `dispute.created`/`funds_withdrawn`
  (matches Stripe pulling funds immediately) vs. only at `closed(lost)`. Recommended: reverse at
  funds-withdrawn, `ReverseOf` on won — so the ledger tracks real cash movement.
- **Void payment record shape**: dedicated `VoidPayment` vs. overloaded `Payment` row — decide in review;
  plan defaults to a dedicated record for clean reporting.
- Real Stripe/Polar credentials remain a **launch gate**; every real call is mode-gated so all phases are
  buildable and testable now against mock/sandbox.

**Confidence for one-pass per-phase success: 7/10** (Phase 1 high; Phases 2-3 depend on provider mandate/SCA
fidelity in mock, which the plan makes deterministic).
