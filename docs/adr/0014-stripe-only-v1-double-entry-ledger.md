# ADR-0014: Stripe-only v1 behind IPaymentProvider; custom double-entry ledger as source of truth

- **Status:** Accepted
- **Date:** 2026-06-12
- **Source:** PRD design interview (decision log #14, `docs/prd/3commerce/15-appendix.md`)

## Context

The owner wanted "Stripe or Polar". Investigation finding: **Polar is a merchant-of-record for digital products only** — like Paddle/Lemon Squeezy, it cannot process physical-goods sales, which is what this store sells (ADR-0002). The owner also wants maximal learning in the money domain while staying launchable.

## Decision

- **Custom double-entry ledger** in Payments is the source of truth for all money facts: append-only, every transaction balances (Σ debits = Σ credits, DB-enforced). Stripe is a rail; Xero is a downstream report.
- **`IPaymentProvider` abstraction with exactly one v1 adapter: Stripe** (Payment Intents; Apple/Google Pay included). Polar remains a future adapter for a possible digital line; regional processors (PayPal/Adyen/Mollie) stay open behind the same seam.
- **Card data never touches our servers**: Stripe Payment Element tokenizes client-side → PCI SAQ-A. Raw card handling was explicitly refused.
- Refunds execute only via the saga/ledger path — never directly against Stripe.

## Alternatives considered

- **Polar as the rail** — impossible for physical goods (digital-only MoR).
- **Dual adapters (Stripe + Polar) in v1** — rejected: double webhook/reconciliation/testing for zero revenue.
- **Raw card handling** — refused outright: PCI SAQ-D scope, uninsurable liability.

## Consequences

- The ledger is the durable learning artifact and survives any processor swap.
- Daily reconciliation (ledger vs Stripe vs Xero) is required tooling (PRD risk R-3).
- Money is integer minor units + ISO 4217 everywhere; corrections are reversing entries, never updates.
