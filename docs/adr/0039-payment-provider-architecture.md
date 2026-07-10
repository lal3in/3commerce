# 0039 — Provider-agnostic payment architecture: registry, three-mode system, wallets-vs-PSP, email-capture safety

Status: Accepted (2026-07-10)
Context: Payment integration program (tracker pay_1..pay_4); plan `.ai-shared/plans/payment-integration-enhancement.md`. Generalizes ADR-0014 (Stripe-only v1 behind `IPaymentProvider`) and succeeds ADR-0015 (no-legal-entity test-mode seams).

## Context

ADR-0014 shipped a single `IPaymentProvider` seam with one real adapter (`StripePaymentProvider`) and one deterministic `FakePaymentProvider`, chosen at startup by the presence of `Stripe:SecretKey` (`Payments/Api/Program.cs:57-65`). The platform now needs to support many providers (Stripe, Polar, PayPal, Afterpay) and the Apple Pay / Google Pay wallets, per tenant, and to run in three distinct operating postures:

1. **Local Mock** — fully offline, no external calls, simulated scenarios.
2. **Provider Sandbox** — provider test credentials and test cards.
3. **Production** — live credentials, real money.

Two facts about the current code force reconciling decisions:

- There is already a per-tenant `PaymentAccount.PaymentProviderMode { Test=1, Live=2 }`. A second, coarser "mode" (offline mock vs sandbox vs prod) is a **runtime/hosting** concern, not a per-tenant one. Collapsing them, or letting each drift, invites a Production host to run a Test account (or worse, the mock path) unnoticed.
- Requirements ask for a **TEST-ONLY email containing the full would-be provider payload** in the two test modes — sensitive by nature — which must be **impossible** to emit in Production.

## Decision

1. **Provider resolution moves from a startup singleton to a keyed registry.** `IPaymentProviderRegistry.Resolve(PaymentAccountSnapshot)` selects the adapter by `PaymentAccount.Provider` (lowercase `ProviderKey` on each `IPaymentProvider`) and applies the mode gate; `ResolveByKey(provider)` serves inbound webhooks (no account context). Adapters self-register in DI as `IPaymentProvider`; the `if (Stripe:SecretKey)` branch is deleted. This unblocks per-tenant, multi-provider operation without touching call sites.

2. **A three-mode system (`PaymentMode { LocalMock=1, Sandbox=2, Production=3 }`) is the resolved runtime behavior, reconciled against the per-tenant account mode.** A `Payments:Mode` host config (default `LocalMock` in Development, `Production` otherwise) is the ceiling; `PaymentModeResolver` maps `(host mode × PaymentAccount.Mode)` to a `PaymentMode`, **failing closed**: a Production host refuses a `Test` account and never selects the mock adapter; a Sandbox host refuses a `Live` account; LocalMock overrides the declared provider with the mock adapter so offline dev needs no credentials. `PaymentAccount.PaymentProviderMode` is unchanged — it remains the tenant's "which credential set" fact.

3. **First-class, provider-agnostic value objects carry the request/response.** `PaymentRequest` / `PaymentResponse` / `PaymentMethodKind` / `PaymentOutcome` / `PaymentError` (all enums numeric on the wire, per platform invariant) replace the loose primitive parameter list. The seam gains `AuthorizeAsync(PaymentRequest)` alongside the retained granular methods (customers, setup-intents, saved-method details, refunds, webhook parse) so migration is incremental.

4. **Wallets are payment methods tokenized through a PSP; Polar/PayPal/Afterpay are PSP adapters.** Apple Pay and Google Pay are `PaymentMethodKind` values that settle through the account's PSP (Stripe's Payment Element accepts them natively and returns the same `PaymentIntent`) — **not** standalone `IPaymentProvider` adapters, because they are wallet UIs, not processors, and duplicating intent/refund/webhook logic for them buys nothing. PayPal, Polar, and Afterpay are real processors/merchants-of-record and get their own adapters. (Afterpay may alternatively surface *through* Stripe as a method-kind; a dedicated adapter is built only if a tenant needs direct Afterpay settlement.)

5. **Email-capture safety: the mock-payload email path HARD-refuses Production.** The TEST-ONLY payload email is emitted (via a `MockPaymentCaptured` event → Notifications worker) in LocalMock and Sandbox only. A boot-time `PaymentModeGuard.EnsureProductionSafe` (a sibling of `DevSecretGuard`, BL-11) **refuses to start** any non-Development host configured `Payments:Mode=LocalMock` or `Payments:AllowMockEmail=true`. The payload is always redacted (never PAN/CVV/wallet token/raw secrets — masked payment-method refs to last4 only); Production additionally never publishes the capture event, so there is no path to sensitive-data email in production.

6. **Existing invariants are preserved, not re-designed.** Webhooks extend the def_2 secret registry + mt6_7 `/webhooks/{provider}` routing (per-provider `ParseWebhook`, funnelled into the single `PaymentEventProcessor`, publish-before-`SaveChangesAsync` outbox, `WebhookInbox` dedupe). Idempotency extends `IdempotencyRecord` via an `IIdempotencyGuard` across all mutations. Refunds keep the `RefundRequested` contract and the double-entry ledger as truth (ADR-0014); per-provider `RefundAsync` sits behind it. Payments still charges Ordering's net verbatim and never re-taxes (ADR-0038).

## Alternatives considered

- **Keep the startup-singleton selection, add providers by swapping config** — rejected: cannot serve two providers concurrently, and no per-tenant resolution.
- **One "mode" enum reusing `PaymentProviderMode` (Test/Live)** — rejected: a per-tenant credential flag cannot express "offline mock" and gives no host-level ceiling to fail closed against; conflating them is exactly how a prod host could run a test/mock path.
- **Apple Pay / Google Pay as standalone adapters** — rejected: they do not settle money; this duplicates PSP logic and mis-models wallets as processors.
- **Gate the mock email by runtime `if (env)` checks only** — rejected: a misconfiguration should refuse to boot, not rely on a branch being reached; the BL-11 fail-closed guard is the established pattern.

## Consequences

- The whole checkout money-path can be built and QA'd fully offline (LocalMock) with an auditable "what we would have sent" email, then graduated provider-by-provider to Sandbox and Production without changing call sites — directly serving the ADR-0015 "build never blocks on the business / launch is a config gate" posture.
- A Production host cannot run a Test/mock account or emit the payload email; these are boot-refusals plus fail-closed resolution, not conventions.
- Mode is resolved per account but bounded by the host `Payments:Mode` ceiling: a Production host cannot run a still-in-Sandbox provider. Providers mid-graduation run in a separate Sandbox environment or stay disabled until live-ready.
- New provider onboarding is: add an adapter with a `ProviderKey`, register it, add a `/webhooks/{provider}` YARP route + a webhook secret (def_2), and walk the mock → sandbox → production ladder. No core changes.
- Go-live gates (ADR-0015 launch checklist) extend with: `Payments:Mode=Production`, `AllowMockEmail` absent, live secret-prefix asserts, live webhook secrets in the registry, redaction verified, payment-surface pen test.
