# MVP Walkthrough Runbook

The full MVP success scenario (PRD §11 / TL;DR), end to end, on the keyless dev stack
(Stripe → `FakePaymentProvider`, Xero → `LoggingXeroClient`). Proves every Phase 1–4 piece
works together. Most of it is automated by `scripts/e2e-verify.sh --live`; this runbook is the
manual/reference version and the acceptance record.

## Prerequisites

```bash
colima start                                            # container runtime
scripts/dev-up.sh --fresh --with-frontends --data full  # whole infra set → migrations (all 13 services) →
                                                        # gateway + services + worker → storefront :3000,
                                                        # admin :5200, supplier portal :5300 → demo data
```

`--data full` matters: checkout refuses the gateway's synthetic default storefront, so the shopper steps need
one of the seed's live demo storefronts (AU/EU/US). `--seed` alone imports the catalog only.

## The scenario

| Step | Action | Expected |
|------|--------|----------|
| 1 | Admin logs in (`admin@3commerce.local` / `dev-admin-password-1`) and runs the sample importer (admin → Imports) | ~10,500 read / ~10,417 accepted / ~83 rejected |
| 2 | ProductUpserted events populate Ordering's `ProductCopies` | `select count(*) from ordering."ProductCopies"` (ordering DB) → ~10,417 |
| 3 | Shopper searches the storefront (`/search?q=...`) | typo-tolerant results, p95 < 500 ms |
| 4 | Shopper adds a product on a demo storefront, checks out as guest in that storefront's currency | 201 + clientSecret; net/tax/gross per the storefront's tax regime (inclusive: tax extracted from the shelf price; exclusive: added — ADR-0038) |
| 5 | Complete the (simulated) payment on the confirmation page | order → **Confirmed**; balanced Sale posted; trial balance 0 |
| 6 | OrderConfirmed → email + Fulfillment shipments | confirmation email logged; shipments per line `FulfilmentType` (warehouse stock consumed, dropship lines forwarded — ADR-0028) |
| 7 | Admin assigns tracking (Fulfillment) | TrackingAssigned → "shipped" email |
| 8 | Shopper opens an RMA from the order support page | RMA saga in **Requested**; ticket/RMA email |
| 9 | Admin approves the RMA (admin → RMA queue) | RefundRequested → refund executes → **RefundIssued**; balanced reversal; trial balance 0 |
| 10 | Admin posts the day's summary to Xero (admin → Xero sync) | one balanced ManualJournal logged; SyncRun = Posted |

## Acceptance (PRD §11)

- ✅ A shopper finds a product among ≥ 10k SKUs, buys as a guest, gets emails, and is refunded.
- ✅ Every step traverses the MVP's six services (Identity, Catalog, Ordering, Payments, Fulfillment, Support) via RabbitMQ; the platform now runs 13.
- ✅ The double-entry ledger is balanced to the cent (trial balance = 0) after sale **and** refund.
- ✅ Xero receives a balanced journal matching the ledger.

## Automated equivalent

```bash
scripts/e2e-verify.sh --live    # runs the L* flows (L1–L20d): infra, auth, search, checkout saga, ledger, refund, RMA, observability
```

## Launch gates still open (not code — PRD Appendix B)

- Company registration → real Stripe keys, real Xero org (+ the OAuth2 client behind `IXeroClient`), the home EU Member State and its tax configuration.
- Supplier contract → real catalog feed; decide dropship vs warehouse (per-offer `FulfilmentType`, ADR-0028).
- External security review (see `docs/security/asvs-l1-audit.md`).
