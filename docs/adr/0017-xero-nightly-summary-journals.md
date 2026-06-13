# ADR-0017: Xero sync — nightly summary journals + per-refund detail

- **Status:** Accepted
- **Date:** 2026-06-12
- **Source:** PRD design interview (decision log #17, `docs/prd/3commerce/15-appendix.md`)

## Context

Xero integration is a stated requirement. Xero has API rate limits (60 calls/min, 5,000/day), and accountants dislike ledger noise — per-order invoices at thousands of SKUs bloat the books and break at scale.

## Decision

A nightly job in Payments posts **one summarized journal per day** to Xero (sales, refunds, Stripe fees, payouts as separate lines) — the pattern established commerce→Xero integrations use. **Refunds and disputes additionally post individually** for traceability. OAuth2 with token refresh; retries with backoff; sync runs persisted and visible in admin. The internal ledger remains the source of truth; Xero is write-only downstream and is never read back as truth.

## Alternatives considered

- **Per-order real-time invoices** — rejected: rate limits, Xero bloat, and Xero-down-during-checkout coupling.
- **Payout-level reconciliation only** — rejected: VAT filing needs sales/tax breakdowns.
- **Manual CSV export** — rejected: drops an explicit requirement (though acceptable as temporary stopgap).

## Consequences

- Xero unavailability never affects checkout — sync is fully async and retryable.
- Daily reconciliation job compares ledger vs Stripe vs Xero (PRD risk R-3 detection).
