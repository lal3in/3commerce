# Feature: Provider-agnostic payment integration — 3-mode system, mock-email capture, multi-provider adapters

This plan is the deliverable for tracker row **pay_1** (Payment integration program, PR A). It is a docs-only design; the implementation ships across **pay_2 / pay_3 / pay_4**. Validate every type name and file path against the codebase before writing code — the existing seam names (`IPaymentProvider`, `PaymentAccount`, `PaymentProviderMode`, `WebhookSecret`, `IdempotencyRecord`, `PaymentEventProcessor`) are load-bearing and must be reused, not reinvented.

## Feature Description

Turn today's single-seam Stripe-or-Fake payment rail into a **provider-agnostic, three-mode, production-ready payment module** supporting Stripe, Polar, PayPal, Afterpay, and the Apple Pay / Google Pay wallets, with a clean path for future providers. The three operating modes are:

1. **Local Mock Mode** — no external calls. Deterministically simulates `success | failure | declined_card | expired_card | requires_3ds | cancelled`. Sends a transactional email containing the **full payment-request payload that would have been sent to the provider**, clearly labelled `TEST ONLY / MOCK PAYMENT`. Reuses the existing Notifications event→worker email seam.
2. **Provider Sandbox Mode** — real provider test credentials (Stripe test mode, Google Pay `TEST` env, Apple Pay sandbox), provider test cards and provider-simulated scenarios. **Also** sends the `TEST ONLY` payload email.
3. **Production Mode** — live credentials, real transactions only. **No test emails, no sensitive payloads.** Secure logging/monitoring with strict redaction. The mock-email path is **hard-refused** at startup and per-request in Production.

## User Story

As a **platform operator onboarding real tenants and new payment providers**
I want **one clean seam that runs fully offline (mock), against provider sandboxes, or in production — with an auditable "what we would have sent" email in the two test modes and none in production**
So that **I can build, demo, and QA the entire checkout money-path with zero legal-entity/credential dependency (ADR-0015), then graduate provider-by-provider to sandbox and live without touching call sites.**

## Problem Statement

The rail is real but narrow:

- **One provider at a time, chosen at startup by config presence.** `src/Services/Payments/Api/Program.cs:58-65` registers `StripePaymentProvider` if `Stripe:SecretKey` is set, else `FakePaymentProvider`, as a process-wide singleton. There is no way to run Stripe for tenant A and PayPal for tenant B, and no registry keyed by provider.
- **Two modes collapsed into two implementations.** "Mock" is `FakePaymentProvider` (deterministic, no scenarios, no email); "real" is Stripe. There is **no Sandbox-vs-Production distinction inside the real adapter**, and no `LocalMock | Sandbox | Production` concept — even though `PaymentAccount.Mode` already carries `Test | Live` per tenant (`src/Services/Payments/Domain/PaymentAccount.cs:12-16`). The two notions of "mode" are unreconciled.
- **No mock-email capture.** Nothing emails the would-be payload. The Notifications worker (`src/Workers/Notifications`) already consumes events and "sends" (dev: logs) email, but there is no `MockPaymentCaptured` event or template.
- **No first-class request/response value objects.** Call sites pass loose primitives (`orderId, amountMinor, currency, idempotencyKey, providerCustomerId, providerPaymentMethodId, setupFutureUsage`) — see `IPaymentProvider.CreateIntentAsync`. There is no `PaymentRequest` / `PaymentResponse` / `PaymentMethodKind` to carry wallet tokens, provider selection, or scenario hints.
- **Wallets are undefined server-side.** Checkout already accepts a `paymentOption` string of `Stripe | CreditCard | ApplePay | GooglePay | PayPal` (`src/Services/Ordering/Api/Endpoints/CheckoutEndpoints.cs:215`), but Payments has no notion of Apple Pay / Google Pay as methods vs providers.
- **Idempotency is partial.** `IdempotencyRecord` (`src/Services/Payments/Domain/IdempotencyRecord.cs`) exists and is used on refunds; intent creation is idempotent only via `AuthorizePaymentConsumer`'s `OrderId` lookup + the provider `Idempotency-Key`. There is no uniform idempotency wrapper across all mutations.
- **Webhooks are Stripe-hardcoded.** `WebhookEndpoints.cs` maps only `/webhooks/stripe`; the def_2 `WebhookSecret` registry is keyed by provider string but only Stripe parses. Routing for `/webhooks/{provider}` is a documented convention (mt6_7) with one wired route.

## Solution Statement

Introduce a thin **provider-agnostic core** — `PaymentRequest`, `PaymentResponse`, `PaymentMethodKind`, and a `PaymentMode` — and resolve providers through an **`IPaymentProviderRegistry`** keyed by `(provider, PaymentAccount)` instead of a single startup singleton. Layer a **`PaymentMode` gate** (`LocalMock=1 | Sandbox=2 | Production=3`) that reconciles the per-environment configuration with the existing per-tenant `PaymentAccount.Mode`. Ship a **`MockEmailPaymentProvider`** that simulates the six scenarios and publishes a `MockPaymentCaptured` event consumed by Notifications to send the `TEST ONLY` payload email (mock and sandbox only; Production hard-refuses). Add provider adapters as **PSP adapters** (Polar, PayPal, Afterpay) and treat **Apple Pay / Google Pay as payment methods tokenized through a PSP**, not standalone adapters. Extend the def_2 webhook registry and `/webhooks/{provider}` routing per provider; extend `IdempotencyRecord` to wrap all payment mutations; keep the existing `RefundRequested` contract and double-entry ledger as truth.

Every repo invariant holds: **numeric enums on the wire**, **outbox publish-before-`SaveChangesAsync`** (`PaymentEventProcessor`/consumers already do this), FORCE RLS on any new `TenantId` table, OpenAPI + `api_contracts_index` regenerated in the implementing PR, `dotnet format` on Infrastructure after `ef migrations add`, tracker updated in the same change.

## Feature Metadata

- **Feature Type**: Enhancement (capability expansion of an existing service)
- **Estimated Complexity**: Large overall; pay_2 Medium, pay_3 Small-Medium, pay_4 Medium (skeletons)
- **Primary Systems Affected**: Payments (Domain/Infrastructure/Api), Notifications worker (new consumer + template), Ordering checkout (unchanged contract; method-kind mapping only), docs/ADR/tracker
- **Dependencies**: None new for pay_2/pay_3 (mock + Stripe already present). pay_4 adds provider SDKs *only when each provider graduates past skeleton*: `Polar` (HTTP), `PayPalCheckoutSdk`/REST, Afterpay REST. No SDK is added while an adapter is a sandbox skeleton.

---

## CONTEXT REFERENCES

### Relevant Codebase Files — READ THESE BEFORE IMPLEMENTING

- `src/Services/Payments/Domain/IPaymentProvider.cs` — the seam to evolve. Records `PaymentIntentResult`, `SetupIntentResult`, `ProviderRefundResult`, `PaymentWebhookEvent`, enum `PaymentWebhookKind` (numeric: `PaymentSucceeded=1, PaymentFailed=2`).
- `src/Services/Payments/Infrastructure/Stripe/StripePaymentProvider.cs` — real adapter (test mode). Reads `Stripe:SecretKey`. Rotation-safe `ParseWebhook(payload, sig, secrets)`.
- `src/Services/Payments/Infrastructure/Payments/FakePaymentProvider.cs` — deterministic dev fake; `FakeFee(gross) = round(gross*0.029)+30`. `ParseWebhook` returns null (uses dev simulate endpoint).
- `src/Services/Payments/Api/Program.cs:57-65` — the startup provider selection to replace with the registry.
- `src/Services/Payments/Api/Endpoints/WebhookEndpoints.cs` — `/webhooks/stripe` + Development-only `/dev/simulate-payment/{intentId}`; both `ExcludeFromDescription`.
- `src/Services/Payments/Infrastructure/PaymentEventProcessor.cs` — the single place a payment outcome becomes ledger truth; idempotent by `WebhookInbox.EventId`; **publishes `PaymentSucceeded`/`PaymentFailed` BEFORE `SaveChangesAsync`** (outbox). All modes must funnel here.
- `src/Services/Payments/Infrastructure/Consumers/AuthorizePaymentConsumer.cs` — request/response saga; idempotent on `OrderId`; charges `NetMinor` verbatim (tax owned by Ordering, ADR-0038 — never re-tax).
- `src/Services/Payments/Infrastructure/Consumers/ExecuteRefundConsumer.cs` — single refund path; idempotent on `RefundId`; ledger reversal + `provider.RefundAsync` + `RefundCompleted`.
- `src/Services/Payments/Domain/PaymentAccount.cs` — per-tenant/storefront account with `PaymentProviderMode { Test=1, Live=2 }`, lifecycle, `SnapshotForCheckout` → `PaymentAccountSnapshot(…, Provider, Mode, ExternalAccountRef)`.
- `src/Services/Payments/Domain/WebhookSecret.cs` + `Infrastructure/WebhookSecretService.cs` — def_2 registry (platform-scoped, masked, rotation-safe, config fallback).
- `src/Services/Payments/Domain/IdempotencyRecord.cs` — `{ Key, RequestHash, ResponseJson, CreatedAt }`.
- `src/Services/Payments/Domain/SavedCards.cs` — `PaymentCustomer`, `SavedPaymentMethod`, `SavedPaymentMethodSnapshot`, `SavedPaymentMethodDetails` (provider refs + brand/last4 only — SAQ-A).
- `src/Workers/Notifications/Program.cs` + `Consumers/UserRegisteredConsumer.cs` + `Email/IEmailSender.cs` + `Email/EmailTemplates.cs` + `Email/LoggingEmailSender.cs` — the event→consumer→`IEmailSender.SendAsync(EmailMessage(To,Subject,Body))` seam the mock email reuses.
- `src/BuildingBlocks/Contracts/Payments/*` — `AuthorizePayment`/`AuthorizePaymentResult`, `PaymentSucceeded`, `PaymentFailed`, `RefundRequested`, `RefundCompleted`, `SubscriptionRequested`, `UsageOverageCharge`.
- `src/BuildingBlocks/Infrastructure/Auth/DevSecretGuard.cs` — the BL-11 "refuse committed dev secret outside Development" pattern the mode/secret guard mirrors.
- `src/BuildingBlocks/Infrastructure/Webhooks/InboundWebhookVerifier.cs` — mt6_7 constant-time HMAC verifier for new HMAC providers.
- `src/Services/Ordering/Api/Endpoints/CheckoutEndpoints.cs:210-225` — `NormalizePaymentOption` accepts `Stripe|CreditCard|ApplePay|GooglePay|PayPal`; the wire mapping target for `PaymentMethodKind`.

### Relevant Documentation — READ BEFORE IMPLEMENTING

- `docs/adr/0014-stripe-only-v1-double-entry-ledger.md` — the `IPaymentProvider` seam + ledger-as-truth decision this generalizes.
- `docs/adr/0015-no-legal-entity-test-mode-config-seams.md` — build-never-blocks; test-mode-only; jurisdiction behind config. **The mode system is the direct successor to this ADR.**
- `docs/adr/0038-per-currency-shelf-prices-and-tax-entry.md` — tax entry convention; Payments charges net verbatim.
- `docs/adr/0039-payment-provider-architecture.md` — the decisions captured for this program (registry, 3-mode, wallet-vs-PSP, email-capture safety).
- `docs/api/api_contracts_index.md` Payments section (`/api/payments`, lines ~176-238) — the surface to extend and regenerate.
- `.ai-shared/plans/deferred-capabilities-completion.md` — plan format precedent; def_2 webhook registry PR is the template for per-provider secret work.
- Tracker rows: `mt6_7` (webhook routing convention), `def_2` (secret registry), `mr_8` (audit emission), `pay_1..pay_4`.

### New Files to Create (implementation PRs — listed here for the map)

- `src/Services/Payments/Domain/PaymentRequest.cs` — `PaymentRequest`, `PaymentResponse`, `PaymentMethodKind`, `PaymentMode`, `PaymentOutcome`, `PaymentError`.
- `src/Services/Payments/Domain/IPaymentProviderRegistry.cs` — resolver seam.
- `src/Services/Payments/Infrastructure/Providers/PaymentProviderRegistry.cs` — keyed resolver + mode gate.
- `src/Services/Payments/Infrastructure/Providers/PaymentModeResolver.cs` — env × account-mode reconciliation + Production hard-refusal.
- `src/Services/Payments/Infrastructure/Providers/Mock/MockEmailPaymentProvider.cs` — scenario simulation + payload capture.
- `src/Services/Payments/Infrastructure/Providers/Stripe/StripePaymentProvider.cs` — moved from `Infrastructure/Stripe/` into the providers tree (namespace-only move; see structure).
- `src/Services/Payments/Infrastructure/Providers/{Polar,PayPal,Afterpay}/*Provider.cs` — pay_4 skeletons.
- `src/BuildingBlocks/Contracts/Payments/MockPaymentCaptured.cs` — the TEST-ONLY email event.
- `src/Workers/Notifications/Consumers/MockPaymentCapturedConsumer.cs` + `EmailTemplates.MockPayment(...)`.

---

## Current vs Desired State

| # | Requirement | Current state (verified) | Missing / desired | Ships in |
|---|-------------|--------------------------|-------------------|----------|
| 1 | Provider-agnostic seam | `IPaymentProvider` with a single startup singleton (Stripe-or-Fake, `Program.cs:58`) | `IPaymentProviderRegistry` keyed by `(provider, PaymentAccount)`; adapters self-register by key | pay_2 |
| 2 | First-class request/response VOs | Loose primitives on `CreateIntentAsync`; no method-kind type | `PaymentRequest`/`PaymentResponse`/`PaymentMethodKind` (numeric enum), carry wallet token + scenario hint | pay_2 |
| 3 | Three modes | Two implementations (Fake vs Stripe); `PaymentAccount.Mode = Test\|Live` only | `PaymentMode { LocalMock=1, Sandbox=2, Production=3 }` gate reconciling env config × account mode | pay_2 |
| 4 | Local Mock (no external calls) | `FakePaymentProvider` deterministic, no scenarios | `MockEmailPaymentProvider`: `success\|failure\|declined_card\|expired_card\|requires_3ds\|cancelled` | pay_3 |
| 5 | Mock TEST-ONLY payload email | none | `MockPaymentCaptured` event → Notifications template; mock **and** sandbox send it; Production refuses | pay_3 |
| 6 | Provider sandbox mode | Stripe test mode via `Stripe:SecretKey`; no wallet TEST env | Sandbox creds per provider (Stripe test, GPay `TEST`, Apple sandbox); sandbox also sends TEST email | pay_2/pay_4 |
| 7 | Production mode safety | No mode concept; no explicit prod refusal of mock/email | Production: live creds only, no TEST email, redacted logs; startup + per-request hard-refuse mock path | pay_2/pay_3 |
| 8 | Stripe | `StripePaymentProvider` (intents/customers/setup/refunds/webhook) present | Move under providers tree; register by key; sandbox/prod key resolution | pay_2 |
| 9 | Polar / PayPal / Afterpay | none | PSP adapters behind the seam; sandbox-ready skeletons, production-gated | pay_4 |
| 10 | Apple Pay / Google Pay | checkout accepts the option strings; no server handling | Payment **methods** tokenized through a PSP (decision below); no standalone adapters | pay_4 |
| 11 | Webhooks per provider | `/webhooks/stripe` only; def_2 registry keyed by provider | `/webhooks/{provider}` routing + per-provider `ParseWebhook` via registry | pay_4 |
| 12 | Idempotency everywhere | `IdempotencyRecord` on refunds; `OrderId`/provider-key on intents | Uniform `IIdempotencyGuard` wrapping all payment mutations | pay_2 |
| 13 | Refunds per provider | `ExecuteRefundConsumer` + `provider.RefundAsync` (Stripe/Fake) | Per-provider refund impls behind the same contract | pay_4 |
| 14 | Typed errors → problem-details | provider exceptions bubble; `AddApiProblemDetails` present | `PaymentError` taxonomy → RFC-7807; retry/timeout policy | pay_2 |
| 15 | Redacted logging | ad-hoc logging | explicit redaction rules (never PAN/CVV/token); correlation ids | pay_2 |

---

## Recommended Folder / Project Structure

Respect the existing 4-project service layout (`Domain`, `Infrastructure`, `Api`, `tests`). Providers live under `Infrastructure/Providers/<Name>/`. The only move is relocating the existing Stripe adapter from `Infrastructure/Stripe/` into `Infrastructure/Providers/Stripe/` (namespace + `using` update; no behavior change — do it in pay_2 with the registry so the diff is coherent).

```
src/Services/Payments/
├── Domain/
│   ├── IPaymentProvider.cs            # evolved: ProviderKey + AuthorizeAsync(PaymentRequest) (below)
│   ├── IPaymentProviderRegistry.cs    # NEW resolver seam
│   ├── PaymentRequest.cs              # NEW: PaymentRequest / PaymentResponse / PaymentMethodKind / PaymentMode / PaymentOutcome / PaymentError / MockScenario
│   ├── PaymentAccount.cs              # existing: PaymentProviderMode { Test, Live } (unchanged)
│   ├── IdempotencyRecord.cs           # existing (reused by IIdempotencyGuard)
│   ├── WebhookSecret.cs / SavedCards.cs / Payment.cs / Refund.cs …
│   └── Ledger/ …                      # unchanged (truth)
├── Infrastructure/
│   ├── Providers/
│   │   ├── PaymentProviderRegistry.cs # NEW keyed resolver + mode gate
│   │   ├── PaymentModeResolver.cs     # NEW env × account-mode reconciliation
│   │   ├── PaymentSecretResolver.cs   # NEW per-provider, per-mode key lookup (+ DevSecretGuard-style refusal)
│   │   ├── PaymentModeGuard.cs        # NEW boot-time Production-safety guard (BL-11 sibling)
│   │   ├── Mock/
│   │   │   └── MockEmailPaymentProvider.cs   # NEW (pay_3) — scenarios + payload capture
│   │   ├── Stripe/
│   │   │   └── StripePaymentProvider.cs      # MOVED from Infrastructure/Stripe/
│   │   ├── Polar/     PolarPaymentProvider.cs     # NEW (pay_4 skeleton)
│   │   ├── PayPal/    PayPalPaymentProvider.cs    # NEW (pay_4 skeleton)
│   │   └── Afterpay/  AfterpayPaymentProvider.cs  # NEW (pay_4 skeleton)
│   ├── Idempotency/ IdempotencyGuard.cs   # NEW EF-backed guard over IdempotencyRecord
│   ├── PaymentEventProcessor.cs           # unchanged funnel (all modes end here)
│   ├── WebhookSecretService.cs            # existing (def_2)
│   └── Consumers/ …                       # AuthorizePayment/ExecuteRefund updated to use registry
├── Api/
│   ├── Program.cs                         # registry + mode registration replaces the if/else singleton
│   └── Endpoints/WebhookEndpoints.cs      # /webhooks/{provider} generalization
└── tests/ …                               # + ProviderRegistry / Mode / MockProvider / Idempotency suites
```

Notifications worker (mock email):

```
src/Workers/Notifications/
├── Consumers/MockPaymentCapturedConsumer.cs   # NEW (pay_3)
└── Email/EmailTemplates.cs                     # + MockPayment(...) template
src/BuildingBlocks/Contracts/Payments/MockPaymentCaptured.cs  # NEW event (pay_3)
```

---

## Interface / Class Design

All new enums are **numeric on the wire** (platform invariant; matches `PaymentWebhookKind`, `PaymentProviderMode`).

### `PaymentMethodKind`, `PaymentMode`, request/response VOs

```csharp
namespace ThreeCommerce.Payments.Domain;

/// Wire values are stable and numeric. Maps from Ordering's checkout paymentOption
/// (Stripe|CreditCard|ApplePay|GooglePay|PayPal — CheckoutEndpoints.NormalizePaymentOption).
public enum PaymentMethodKind
{
    Card       = 1,   // credit/debit, incl. "Stripe"/"CreditCard" default
    ApplePay   = 2,   // wallet — tokenized THROUGH a PSP (see decision)
    GooglePay  = 3,   // wallet — tokenized THROUGH a PSP
    PayPal     = 4,   // PSP-native
    Afterpay   = 5,   // BNPL PSP
    Polar      = 6,   // merchant-of-record PSP
}

/// The operating mode. Distinct from PaymentAccount.PaymentProviderMode (Test|Live):
/// PaymentMode is the RESOLVED runtime behavior; see PaymentModeResolver.
public enum PaymentMode { LocalMock = 1, Sandbox = 2, Production = 3 }

public enum PaymentOutcome { Succeeded = 1, RequiresAction = 2, Failed = 3, Cancelled = 4 }

/// Everything an adapter needs, provider-agnostic. Carries the resolved account so the
/// registry can key on (Provider, PaymentAccount) and the adapter can pick the right creds.
public sealed record PaymentRequest(
    Guid OrderId,
    long AmountMinor,          // gross the shopper pays (net incl. Ordering-owned tax; never re-taxed)
    string Currency,
    string IdempotencyKey,
    PaymentMethodKind MethodKind,
    PaymentAccountSnapshot Account,     // existing VO: Provider, Mode, ExternalAccountRef, tenant/storefront
    string? ProviderCustomerId = null,
    string? ProviderPaymentMethodId = null,
    string? WalletToken = null,         // Apple/Google Pay tokenization blob (redacted from logs/email)
    bool SetupFutureUsage = false,
    MockScenario? Scenario = null);     // honored ONLY in LocalMock (ignored otherwise)

public sealed record PaymentResponse(
    string PaymentIntentId,
    string? ClientSecret,
    PaymentOutcome Outcome,
    PaymentError? Error = null);

/// Typed provider error → problem-details (never leaks provider exception text raw).
public sealed record PaymentError(PaymentErrorCode Code, string Message, bool Retryable);

public enum PaymentErrorCode
{
    CardDeclined = 1, ExpiredCard = 2, AuthenticationRequired = 3, InsufficientFunds = 4,
    ProcessingError = 5, RateLimited = 6, ProviderUnavailable = 7, ConfigurationError = 8,
}

/// LocalMock simulation selector (verified against user requirements).
public enum MockScenario
{
    Success = 1, Failure = 2, DeclinedCard = 3, ExpiredCard = 4, Requires3ds = 5, Cancelled = 6,
}
```

### Evolved `IPaymentProvider` + `IPaymentProviderRegistry`

Keep the existing granular methods (they carry setup-intent/customer/webhook responsibilities that the registry cannot generalize away), and **add** a request/response `AuthorizeAsync` overload so call sites move to the VO without a big-bang rewrite. Adapters expose the provider key they serve.

```csharp
namespace ThreeCommerce.Payments.Domain;

public interface IPaymentProvider
{
    /// Lowercase key matching PaymentAccount.Provider and the /webhooks/{provider} route.
    string ProviderKey { get; }

    Task<PaymentResponse> AuthorizeAsync(PaymentRequest request, CancellationToken ct);

    // Existing surface retained (customers, setup-intents, saved-method details, refunds, webhook parse):
    Task<string> CreateCustomerAsync(Guid userId, string email, CancellationToken ct);
    Task<SetupIntentResult> CreateSetupIntentAsync(string providerCustomerId, CancellationToken ct);
    Task<SavedPaymentMethodDetails> GetPaymentMethodAsync(string providerPaymentMethodId, CancellationToken ct);
    Task<ProviderRefundResult> RefundAsync(string paymentIntentId, long amountMinor, string idempotencyKey, CancellationToken ct);
    PaymentWebhookEvent? ParseWebhook(string payload, string signatureHeader, IReadOnlyList<string> secrets);
}

/// Resolves the adapter for an account, then wraps it in the mode gate.
public interface IPaymentProviderRegistry
{
    /// Chooses by account.Provider; applies the PaymentMode gate (LocalMock swaps in the
    /// mock adapter regardless of the account's declared provider so offline dev needs no creds).
    IPaymentProvider Resolve(PaymentAccountSnapshot account);

    /// For inbound webhooks (no account context) — resolve by the route's {provider} key.
    IPaymentProvider ResolveByKey(string providerKey);
}
```

### `PaymentModeResolver` — reconciling the two "modes"

`PaymentAccount.PaymentProviderMode` (`Test | Live`) is a **tenant-configuration** fact ("this account points at test or live provider credentials"). `PaymentMode` (`LocalMock | Sandbox | Production`) is the **resolved runtime behavior**. They reconcile as:

| Hosting env (`Payments:Mode` config) | `PaymentAccount.Mode` | Resolved `PaymentMode` | Behavior |
|--------------------------------------|-----------------------|------------------------|----------|
| `LocalMock` (default in Development, no live keys) | any | **LocalMock** | no external calls; mock adapter; TEST email |
| `Sandbox` | `Test` | **Sandbox** | provider test creds; TEST email |
| `Sandbox` | `Live` | **error at activation** | readiness refuses a Live account in a Sandbox host |
| `Production` | `Live` | **Production** | live creds; **no email**; redacted logs |
| `Production` | `Test` | **error / refused** | Production never runs a Test account (guard below) |

Resolution rule (fail-closed toward Production safety):

```csharp
public PaymentMode Resolve(PaymentAccountSnapshot account)
{
    var host = _config.GetValue("Payments:Mode", _env.IsDevelopment() ? PaymentMode.LocalMock : PaymentMode.Production);

    // Production host HARD-refuses anything but a Live account and NEVER the mock path.
    if (host == PaymentMode.Production)
        return account.Mode == PaymentProviderMode.Live
            ? PaymentMode.Production
            : throw new PaymentModeException("Production host cannot run a Test payment account.");

    if (host == PaymentMode.Sandbox)
        return account.Mode == PaymentProviderMode.Test
            ? PaymentMode.Sandbox
            : throw new PaymentModeException("Sandbox host cannot run a Live payment account.");

    return PaymentMode.LocalMock; // offline; no creds required
}
```

Startup guard (mirrors `DevSecretGuard.EnsureProductionKey`, BL-11): a non-Development environment configured `Payments:Mode=LocalMock` (or with `Payments:AllowMockEmail=true`) **refuses to boot**.

```csharp
// Program.cs, after config binds:
PaymentModeGuard.EnsureProductionSafe(builder.Configuration, builder.Environment);
//   throws if !IsDevelopment && (Mode==LocalMock || AllowMockEmail) — the mock-email path
//   must be impossible to reach in Production (email-capture safety rule, ADR-0039).
```

---

## Architecture Decision: Wallets vs PSP adapters

**Decision.** Apple Pay and Google Pay are modeled as **`PaymentMethodKind` values tokenized through a PSP (Stripe in v1)**, *not* as standalone `IPaymentProvider` adapters. Polar, PayPal, and Afterpay are **PSP adapters** (each its own `IPaymentProvider` with a `ProviderKey`).

**Why.** Apple Pay and Google Pay are not payment processors — they are wallet UIs that hand the browser a network token (a DPAN + cryptogram). Money still settles through a real PSP that accepts that token: Stripe's Payment Element accepts Apple/Google Pay natively and returns the same `PaymentIntent`; the server code path is identical to a card. Building `ApplePayPaymentProvider` as a peer of Stripe would duplicate Stripe's intent/refund/webhook logic for zero settlement benefit and force us to become a merchant-of-record for wallets we cannot settle. So:

- The storefront's Payment Element (already SAQ-A, client-side confirm) enables Apple/Google Pay as wallet buttons on the **Stripe** account.
- `PaymentMethodKind.ApplePay/GooglePay` is recorded on the checkout snapshot (already flows as `paymentOption`) for analytics/receipts, and passed in `PaymentRequest.MethodKind`; the adapter selected is still the account's PSP.
- `WalletToken` exists on `PaymentRequest` only for a future non-PSP direct-decrypt path; unused while Stripe tokenizes.

PayPal, Polar, Afterpay **are** processors/merchants-of-record with their own intents, refunds, and webhooks → first-class adapters. Afterpay may also surface *through* Stripe as a payment method; the plan implements it as its own adapter only if a tenant needs direct Afterpay settlement, otherwise it is a Stripe method-kind (documented in pay_4 as a graduation choice).

This is captured in **ADR-0039**.

---

## Environment Configuration & Secrets

Per-environment, per-provider, per-mode keys, resolved by `PaymentSecretResolver`. Never commit live keys; dev keys are committed only for LocalMock/Sandbox and are refused in Production by the guard.

```jsonc
// appsettings.json (Development) — LocalMock by default, no external calls
"Payments": {
  "Mode": "LocalMock",             // LocalMock | Sandbox | Production
  "AllowMockEmail": true,          // TEST-ONLY payload email; MUST be false/absent in Production
  "MockEmailTo": "dev-payments@localhost"
}

// Sandbox environment (env vars / secret store) — provider test creds
"Payments__Mode": "Sandbox",
"Payments__AllowMockEmail": "true",
"Stripe__SecretKey": "sk_test_…",
"Stripe__WebhookSecret": "whsec_test_…",       // fallback; registry (def_2) preferred
"GooglePay__Environment": "TEST",
"ApplePay__Environment": "sandbox",
"PayPal__Mode": "sandbox", "PayPal__ClientId": "…", "PayPal__Secret": "…",

// Production — live creds only, mock/email impossible
"Payments__Mode": "Production",
// AllowMockEmail absent → guard passes; if present+true → REFUSE TO BOOT
"Stripe__SecretKey": "sk_live_…"   // supplied via the secret store, never the repo
```

Rules:
- **Secrets are read per (provider, mode).** `PaymentSecretResolver.Get(provider, mode, key)` looks up `"{Provider}:{key}"` and asserts the value's prefix matches the mode (`sk_test_` for Sandbox, `sk_live_` for Production) — a `sk_live_` key under a Sandbox host throws `ConfigurationError` (fail-closed, mirrors DevSecretGuard's fingerprint refusal).
- **Webhook secrets** stay in the def_2 `WebhookSecret` registry (masked, rotation-safe), config fallback preserved.
- **Rotation** for API keys: same "add new, keep old active, cut over, deactivate" model the webhook registry already documents; API keys are single-valued per mode so rotation is a secret-store swap + restart (documented in `docs/ops/secrets.md`).
- Production **must not** carry `AllowMockEmail=true` or `Mode=LocalMock` → `PaymentModeGuard` throws at boot.

---

## Example Implementation Code

### `MockEmailPaymentProvider` (pay_3)

No external calls; deterministic scenario mapping; publishes the TEST-ONLY payload event (mock **and** sandbox — a decorator, not this class, decides whether to also publish in Sandbox). Funnels a success into the **same** `PaymentEventProcessor` via the existing `/dev/simulate-payment` mechanism so the ledger path is identical to real Stripe.

```csharp
namespace ThreeCommerce.Payments.Infrastructure.Providers.Mock;

public sealed class MockEmailPaymentProvider(IPublishEndpoint publisher, TimeProvider time) : IPaymentProvider
{
    public string ProviderKey => "mock";

    public async Task<PaymentResponse> AuthorizeAsync(PaymentRequest r, CancellationToken ct)
    {
        var intentId = $"pi_mock_{r.OrderId:N}";

        // Capture the FULL would-be payload (redacted) for the TEST-ONLY email. Publish BEFORE
        // any state save so it commits through the outbox (publish-before-SaveChanges invariant).
        await publisher.Publish(new MockPaymentCaptured(
            r.OrderId, r.Account.TenantId, r.Account.Provider, r.MethodKind.ToString(),
            r.AmountMinor, r.Currency, intentId,
            PayloadRedactor.ToJson(r),                 // never PAN/CVV/WalletToken (see redaction rules)
            (r.Scenario ?? MockScenario.Success).ToString(),
            time.GetUtcNow()), ct);

        return (r.Scenario ?? MockScenario.Success) switch
        {
            MockScenario.Success       => new(intentId, $"{intentId}_secret", PaymentOutcome.Succeeded),
            MockScenario.Requires3ds   => new(intentId, $"{intentId}_secret", PaymentOutcome.RequiresAction,
                                              new(PaymentErrorCode.AuthenticationRequired, "3DS required (mock)", true)),
            MockScenario.Cancelled     => new(intentId, null, PaymentOutcome.Cancelled),
            MockScenario.DeclinedCard  => new(intentId, null, PaymentOutcome.Failed, new(PaymentErrorCode.CardDeclined, "Card declined (mock)", false)),
            MockScenario.ExpiredCard   => new(intentId, null, PaymentOutcome.Failed, new(PaymentErrorCode.ExpiredCard, "Card expired (mock)", false)),
            _                          => new(intentId, null, PaymentOutcome.Failed, new(PaymentErrorCode.ProcessingError, "Payment failed (mock)", true)),
        };
    }

    public Task<ProviderRefundResult> RefundAsync(string intentId, long amountMinor, string idem, CancellationToken ct)
        => Task.FromResult(new ProviderRefundResult($"re_mock_{Guid.CreateVersion7():N}", true));

    // customers/setup-intents mirror FakePaymentProvider; ParseWebhook returns null (uses simulate endpoint).
}
```

### Mode gate (registry) and registration

```csharp
public sealed class PaymentProviderRegistry(
    IEnumerable<IPaymentProvider> adapters,
    PaymentModeResolver modeResolver) : IPaymentProviderRegistry
{
    public IPaymentProvider Resolve(PaymentAccountSnapshot account)
    {
        var mode = modeResolver.Resolve(account);          // throws in unsafe combinations
        if (mode == PaymentMode.LocalMock)
            return adapters.Single(a => a.ProviderKey == "mock");
        return adapters.Single(a => a.ProviderKey == account.Provider.ToLowerInvariant());
    }

    public IPaymentProvider ResolveByKey(string providerKey) =>
        adapters.Single(a => a.ProviderKey == providerKey.ToLowerInvariant());
}
```

```csharp
// Program.cs — replaces the if(Stripe:SecretKey) singleton (Program.cs:57-65)
PaymentModeGuard.EnsureProductionSafe(builder.Configuration, builder.Environment);
builder.Services.AddScoped<PaymentModeResolver>();
builder.Services.AddScoped<IPaymentProviderRegistry, PaymentProviderRegistry>();
builder.Services.AddSingleton<IPaymentProvider, MockEmailPaymentProvider>();
builder.Services.AddSingleton<IPaymentProvider, StripePaymentProvider>();
// pay_4: AddSingleton<IPaymentProvider, PolarPaymentProvider>(); PayPal; Afterpay …
// AuthorizePaymentConsumer/ExecuteRefundConsumer take IPaymentProviderRegistry and Resolve(snapshot).
```

`AuthorizePaymentConsumer` change: it already has the order's account context via checkout; resolve `registry.Resolve(snapshot)` instead of injecting a single `IPaymentProvider`, then call `AuthorizeAsync(request, ct)`. Behavior for the existing Stripe/mock path is unchanged.

---

## Webhook Handling Design

Extend the def_2 registry + mt6_7 routing convention per provider — no redesign, just generalize the one wired route.

- **Routing:** replace `/webhooks/stripe` with `/webhooks/{provider}` (keep `stripe` working). The gateway already routes `/webhooks/{provider}` to the owning service with no prefix strip and no claims minting (mt6_7). Each new provider adds one YARP route entry.
- **Parsing:** `WebhookEndpoints` resolves `registry.ResolveByKey(provider)` and calls `ParseWebhook(payload, signatureHeader, await secrets.GetActiveSecretsAsync(provider, ct))`. Stripe uses its SDK verify; new HMAC providers use `InboundWebhookVerifier` (constant-time HMAC over `"{timestamp}.{payload}"`, ±5-min tolerance). Signature header name is provider-specific (`Stripe-Signature`, `Paypal-Transmission-Sig`, …) — read from a small per-provider map.
- **Idempotency / funnel:** every parsed event becomes a normalized `PaymentWebhookEvent` and flows into the existing `PaymentEventProcessor.ProcessAsync`, which dedupes by `WebhookInbox.EventId` and publishes `PaymentSucceeded`/`PaymentFailed` **before** `SaveChangesAsync` (outbox). No per-provider ledger code.
- **Secrets:** unchanged def_2 registry (masked, newest-first, config fallback). New providers add rows via the existing `/admin/webhook-secrets` surface.

```csharp
app.MapPost("/webhooks/{provider}", async (string provider, HttpContext http,
    IPaymentProviderRegistry registry, WebhookSecretService secrets,
    PaymentEventProcessor processor, CancellationToken ct) =>
{
    var adapter = registry.ResolveByKey(provider);        // 404 if unknown provider
    var payload = await new StreamReader(http.Request.Body).ReadToEndAsync(ct);
    var sig = http.Request.Headers[SignatureHeader.For(provider)].ToString();
    var ev = adapter.ParseWebhook(payload, sig, await secrets.GetActiveSecretsAsync(provider, ct));
    if (ev is null) return TypedResults.BadRequest();
    await processor.ProcessAsync(ev, ct);
    return TypedResults.Ok();
}).ExcludeFromDescription();
```

---

## Idempotency Design

Keep `IdempotencyRecord` (Key/RequestHash/ResponseJson/CreatedAt) as the store; add an `IIdempotencyGuard` that wraps **all** payment mutations, not just refunds:

```csharp
public interface IIdempotencyGuard
{
    /// Returns the stored response for a replayed Key (same RequestHash), else runs op, stores, returns.
    /// RequestHash mismatch on a known Key → throws (a client reused a key for a different request).
    Task<T> ExecuteAsync<T>(string key, object request, Func<CancellationToken, Task<T>> op, CancellationToken ct);
}
```

- **Intent creation:** `AuthorizePaymentConsumer` keeps its `OrderId` short-circuit (natural idempotency) **and** the provider `Idempotency-Key`; the guard adds a uniform record for retried authorize requests carrying the same `IdempotencyKey`.
- **Refunds:** `ExecuteRefundConsumer` keeps its `RefundId` dedupe; the guard standardizes the stored response shape.
- **Saved-method setup / customer creation:** wrapped by key so a double-submit does not create two Stripe customers.
- **Hash** = stable JSON of the request minus volatile fields; `ResponseJson` is the serialized `PaymentResponse`. Records are per-service (Payments schema), not tenant-scoped (keys are provider/idempotency-scoped).

---

## Refund Handling Design

Unchanged contract, per-provider implementation:

- `RefundRequested` (existing contract) → `ExecuteRefundConsumer` → `registry.Resolve(snapshot).RefundAsync(intentId, amountMinor, RefundId.ToString(), ct)`.
- Ledger reversal + proportional tax reversal (banker's rounding) is already in `ExecuteRefundConsumer` and stays provider-agnostic.
- Each adapter implements `RefundAsync` against its provider (Stripe: `RefundService`; PayPal: refund capture; Afterpay/Polar: their refund endpoints). Mock returns a deterministic success.
- Idempotent on `RefundId` (consumer) + provider idempotency key (adapter). Partial refunds honored by `AmountMinor`.

---

## Error Handling & Logging Strategy

**Error taxonomy → problem-details.** Adapters catch provider SDK/HTTP exceptions and translate to `PaymentError(PaymentErrorCode, Message, Retryable)`; the API layer maps these to RFC-7807 via the existing `AddApiProblemDetails`/`UseApiProblemDetails` (`Program.cs:24,69`). Never surface raw provider exception text to clients.

| `PaymentErrorCode` | HTTP | Retryable | Source example |
|--------------------|------|-----------|----------------|
| CardDeclined / ExpiredCard / InsufficientFunds | 402 | no | Stripe `card_error` |
| AuthenticationRequired | 402 (client re-confirm) | n/a | 3DS required |
| RateLimited | 429 | yes (backoff) | provider 429 |
| ProviderUnavailable / ProcessingError | 502 | yes | timeout / 5xx |
| ConfigurationError | 500 (ops) | no | wrong-mode key |

**Retry/timeout policy.** Provider HTTP calls get a per-provider timeout (default 20s) and a bounded retry (2 attempts, exponential backoff + jitter) **only** for `Retryable` codes and idempotent operations (authorize/refund carry idempotency keys, so retry is safe). Non-retryable declines fail fast. Webhooks are never retried by us — the provider redelivers; our `WebhookInbox` dedupes.

**Logging & redaction (mandatory).**
- **Never log** PAN, CVV, full card number, `WalletToken`, raw provider secrets, or full webhook payloads at Info+. `PayloadRedactor` masks `ProviderPaymentMethodId`→last4 only, drops `WalletToken`, and omits any field named `*token*|*secret*|*cvv*|*pan*`.
- **Always log** correlation id (order id + payment intent id + idempotency key), provider key, resolved `PaymentMode`, outcome, and `PaymentErrorCode` — enough to trace without sensitive data.
- **The TEST-ONLY email** is the *only* place a full (redacted) request payload is materialized, and only in LocalMock/Sandbox; in Production the capture event is never published (guard) and the consumer would refuse it anyway.
- Correlation ids ride the existing telemetry (`AddServiceTelemetry("payments")`).

---

## Example TEST-ONLY Email

**Subject:** `[TEST ONLY / MOCK PAYMENT] Order {orderId} — {amountMinor/100} {CURRENCY} via {provider}/{methodKind}`

**Body (text):**
```
================= TEST ONLY / MOCK PAYMENT =================
This email was generated by 3commerce running in LocalMock/Sandbox
mode. NO money moved. It shows the payment request that WOULD have
been sent to the provider. Never sent in Production.
===========================================================

Order:        3f2a…c91
Tenant:       9b1e…04
Provider:     stripe        Mode: Sandbox
Method:       ApplePay
Scenario:     Success
Amount:       49.90 EUR      Intent: pi_mock_3f2a…c91

--- Provider request payload (redacted) ---
{
  "orderId": "3f2a…c91",
  "amountMinor": 4990,
  "currency": "EUR",
  "idempotencyKey": "ord-3f2a-1",
  "methodKind": 2,
  "providerCustomerId": "cus_test_…",
  "providerPaymentMethodId": "pm_…_4242",   // last4 only
  "setupFutureUsage": false
  // walletToken / PAN / CVV are never included
}
-------------------------------------------
```

`EmailTemplates.MockPayment(...)` builds this from the `MockPaymentCaptured` event; the `PayloadRedactor.ToJson` output is embedded verbatim. A companion example JSON document ships at `docs/api/examples/mock-payment-payload.json` (same content, canonical) for QA reference:

```json
{
  "label": "TEST ONLY / MOCK PAYMENT",
  "mode": "Sandbox",
  "orderId": "3f2a0000-0000-0000-0000-00000000c91",
  "tenantId": "9b1e0000-0000-0000-0000-000000000004",
  "provider": "stripe",
  "methodKind": 2,
  "scenario": "Success",
  "amountMinor": 4990,
  "currency": "EUR",
  "paymentIntentId": "pi_mock_3f2a0000000000000000000000000c91",
  "request": {
    "amountMinor": 4990,
    "currency": "EUR",
    "idempotencyKey": "ord-3f2a-1",
    "methodKind": 2,
    "providerCustomerId": "cus_test_redacted",
    "providerPaymentMethodId": "pm_redacted_4242",
    "setupFutureUsage": false
  }
}
```

---

## Test Plan

**Unit (Payments.tests, xUnit — matches existing `SavedCardTests`, `StripeWebhookSecretTests` style):**
- `PaymentModeResolverTests` — the full env×account matrix incl. every refusal (Production+Test throws, Sandbox+Live throws, LocalMock always mock).
- `PaymentModeGuardTests` — non-Development + `Mode=LocalMock` / `AllowMockEmail=true` refuse to boot (mirrors `DevSecretGuardTests`).
- `MockEmailPaymentProviderTests` — all six scenarios map to the right `PaymentOutcome`/`PaymentErrorCode`; a `MockPaymentCaptured` is published with a redacted payload (asserts no `WalletToken`/PAN).
- `PayloadRedactorTests` — token/secret/cvv/pan fields stripped; pm id reduced to last4.
- `PaymentProviderRegistryTests` — resolves by key; LocalMock overrides the account provider; unknown key throws.
- `PaymentSecretResolverTests` — `sk_live_` under Sandbox throws `ConfigurationError`; correct prefix passes.
- `IdempotencyGuardTests` — replay returns stored response; hash mismatch throws.

**Integration (`Category=Integration`, Testcontainers — matches existing `StripeWebhookSecretTests` integration + `MoneyFlow`):**
- Mock end-to-end: authorize (LocalMock) → `MockPaymentCaptured` observed on the bus (real RabbitMQ) → Notifications consumer sends (LoggingEmailSender captured) → `/dev/simulate-payment` → `PaymentSucceeded` + ledger `Sale` posted.
- `/webhooks/{provider}` routing: stripe route still verifies; an unknown provider 404s; a second registered adapter parses its own signature.
- Idempotency: duplicate authorize with same key reuses the intent and posts one journal entry.
- Production-safety: a host booted `Production` + `AllowMockEmail=true` fails to start (guard) — assert the boot exception.

**Manual QA checklist (per mode):**
- [ ] LocalMock: checkout completes offline, no external calls (network asserted), TEST email logged with redacted payload, all six scenarios reachable via `MockScenario`.
- [ ] Sandbox (Stripe test): real test card `4242…` succeeds; `4000000000000002` declines; `4000002500003155` triggers 3DS; TEST email still sent.
- [ ] Sandbox wallets: Apple Pay (sandbox) / Google Pay (`TEST`) buttons render via Stripe Payment Element and settle as card intents.
- [ ] Refund: partial + full refund post correct ledger reversals and `RefundCompleted`.
- [ ] Redaction: grep logs for PAN/CVV/token — none present.

**UAT checklist:**
- [ ] A tenant admin creates a `Test` payment account, activates it (readiness), completes a sandbox purchase, sees the order + receipt.
- [ ] Operator rotates a webhook secret (def_2) mid-stream; no webhook is dropped.
- [ ] Operator switches a tenant from `Test`→`Live` account only after go-live checklist; Sandbox host refuses a Live account.
- [ ] Finance reconciles the double-entry ledger against provider sandbox dashboard totals.

**Go-live checklist (per ADR-0015 launch gates + this module):**
- [ ] Legal entity registered → live provider keys provisioned in the secret store (never the repo).
- [ ] `Payments:Mode=Production`, `AllowMockEmail` absent; boot guard passes.
- [ ] Live webhook secrets in the def_2 registry; config fallback removed for prod.
- [ ] `PaymentSecretResolver` prefix asserts pass for every live key.
- [ ] Redaction rules verified against a live smoke transaction (then refunded).
- [ ] Monitoring/alerts on `PaymentErrorCode` rates, webhook lag, ledger imbalance.
- [ ] Pen test of the payment surface (ADR-0015 gate) complete.

---

## Graduating mock → sandbox → production (per provider)

Each provider walks the same ladder independently:

1. **LocalMock** — no creds; `MockEmailPaymentProvider` simulates scenarios; TEST email proves the payload shape. Build/QA the full call site.
2. **Sandbox** — set the provider's test creds + `Payments:Mode=Sandbox`; register the real adapter; run provider test cards/scenarios; TEST email still sent for payload audit; wire the provider's `/webhooks/{provider}` route + a test webhook secret.
3. **Production** — live creds in the secret store; add the live webhook secret to the registry; flip `PaymentAccount.Mode`→`Live`; boot guard + secret-prefix asserts enforce safety; **no TEST email**. Graduate one provider at a time; a provider still in Sandbox coexists with another in Production because mode is resolved per account, not per host — **except** the host `Payments:Mode` is the ceiling (a Production host cannot run a Sandbox account; run non-graduated providers in a separate Sandbox environment or keep them disabled until live-ready).

---

## Task Breakdown (implementation PRs)

### pay_2 — Core provider-agnostic abstraction + 3-mode system
Domain: `PaymentRequest`/`PaymentResponse`/`PaymentMethodKind`/`PaymentMode`/`PaymentOutcome`/`PaymentError`/`MockScenario`; `IPaymentProviderRegistry`; `ProviderKey`+`AuthorizeAsync` on `IPaymentProvider`. Infrastructure: `PaymentProviderRegistry`, `PaymentModeResolver`, `PaymentSecretResolver`, `PaymentModeGuard`, `IdempotencyGuard`; move `StripePaymentProvider` under `Providers/Stripe/`; adapt `AuthorizePaymentConsumer`/`ExecuteRefundConsumer` to resolve via the registry. Api: replace the `Program.cs:57-65` singleton with registry + guard; keep `FakePaymentProvider` behavior as the mock's deterministic core (or retire it in favor of the mock — decide in-PR, but preserve the `/dev/simulate-payment` funnel). Error→problem-details mapping + redaction utility. Tests: resolver/guard/registry/secret/idempotency units.
**Validate:** `dotnet build 3commerce.sln && dotnet format --verify-no-changes && dotnet test 3commerce.sln`; no behavior change for the existing Stripe/Fake path (MoneyFlow integration green); OpenAPI unchanged (no new public routes yet).

### pay_3 — MockEmailPaymentProvider + TEST-ONLY email capture
`MockEmailPaymentProvider` (six scenarios); `MockPaymentCaptured` contract; Notifications `MockPaymentCapturedConsumer` + `EmailTemplates.MockPayment`; `PayloadRedactor`; `docs/api/examples/mock-payment-payload.json`. Sandbox decorator that also publishes the capture in Sandbox. Guard ensures Production never publishes/consumes it.
**Validate:** `dotnet build && dotnet format --verify-no-changes && dotnet test`; integration: authorize (LocalMock) → capture on bus → email sent (LoggingEmailSender) → simulate → ledger Sale; assert redaction (no token/PAN in the email body); assert Production boot refusal with `AllowMockEmail=true`.

### pay_4 — Provider adapters (Polar, PayPal, Apple/Google Pay methods, Afterpay)
PSP adapters `PolarPaymentProvider`, `PayPalPaymentProvider`, `AfterpayPaymentProvider` (sandbox-ready skeletons behind the seam, production-gated by the secret resolver); Apple/Google Pay wired as `PaymentMethodKind` through Stripe (no new adapter). `/webhooks/{provider}` generalization + per-provider signature-header map + `SignatureHeader.For`. Per-provider `RefundAsync`. Register each in `Program.cs`. Add SDK/HTTP client per provider only when it graduates past skeleton.
**Validate:** `dotnet build && dotnet format --verify-no-changes && dotnet test`; integration: `/webhooks/{provider}` routes to the right adapter, unknown provider 404s, second adapter verifies its own signature; OpenAPI + `api_contracts_index` regenerated (webhook route note updated); each skeleton refuses to run without its mode-appropriate creds.

---

## STATUS TRACKER ROWS

`pay_1` flips to `done` on this PR (plan + ADR-0039 + adr_index). `pay_2`/`pay_3`/`pay_4` stay `pending`, flipped as each PR lands per the tracker rule.

## VALIDATION (this docs-only PR)

`git diff --check` clean; no source/behavior changes; ADR-0039 added with an adr_index row; tracker `pay_1` → done. Branch `docs/payment-integration-plan` → PR to `main` (orchestrator merges).
