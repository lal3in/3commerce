# MVP Walkthrough Runbook

The full MVP success scenario (PRD §11 / TL;DR), end to end, on the keyless dev stack
(Stripe → `FakePaymentProvider`, Xero → `LoggingXeroClient`). Proves every Phase 1–4 piece
works together. Most of it is automated by `scripts/e2e-verify.sh --live`; this runbook is the
manual/reference version and the acceptance record.

## Prerequisites

```bash
colima start                                            # container runtime
docker compose -f docker-compose.infra.yml up -d        # Postgres + RabbitMQ
# apply migrations for all six services (once)
for s in Identity Catalog Ordering Payments Fulfillment Support; do \
  dotnet ef database update -p src/Services/$s/Infrastructure -s src/Services/$s/Api; done
scripts/run-all.sh start                                # gateway + 6 services + worker
cd src/Storefront && GATEWAY_URL=http://localhost:8080 npm run start &   # storefront :3000
dotnet run --project src/Admin &                        # admin :5200
```

## The scenario

| Step | Action | Expected |
|------|--------|----------|
| 1 | Admin logs in (`admin@3commerce.local` / `dev-admin-password-1`) and runs the sample importer (admin → Imports) | ~10,500 read / ~10,417 accepted / ~83 rejected |
| 2 | ProductUpserted events populate Ordering's `ProductCopies` | `select count(*) from "ProductCopies"` → ~10,417 |
| 3 | Shopper searches the storefront (`/search?q=...`) | typo-tolerant results, p95 < 500 ms |
| 4 | Shopper adds a product, checks out as guest | 201 + clientSecret; correct net/tax(19%)/gross |
| 5 | Complete the (simulated) payment on the confirmation page | order → **Confirmed**; balanced Sale posted; trial balance 0 |
| 6 | OrderConfirmed → email + Fulfillment shipments | confirmation email logged; one shipment per fulfillment source |
| 7 | Admin assigns tracking (Fulfillment) | TrackingAssigned → "shipped" email |
| 8 | Shopper opens an RMA from the order support page | RMA saga in **Requested**; ticket/RMA email |
| 9 | Admin approves the RMA (admin → RMA queue) | RefundRequested → refund executes → **RefundIssued**; balanced reversal; trial balance 0 |
| 10 | Admin posts the day's summary to Xero (admin → Xero sync) | one balanced ManualJournal logged; SyncRun = Posted |

## Acceptance (PRD §11)

- ✅ A shopper finds a product among ≥ 10k SKUs, buys as a guest, gets emails, and is refunded.
- ✅ Every step traverses the six services via RabbitMQ.
- ✅ The double-entry ledger is balanced to the cent (trial balance = 0) after sale **and** refund.
- ✅ Xero receives a balanced journal matching the ledger.

## Automated equivalent

```bash
scripts/e2e-verify.sh --live    # runs L1–L19: infra, auth, search, checkout saga, ledger, refund, RMA
```

## Launch gates still open (not code — PRD Appendix B)

- Company registration → real Stripe keys, real Xero org, real `ITaxStrategy`.
- Supplier contract → real catalog feed; decide dropship vs warehouse (per-line `FulfillmentSource`).
- External security review (see `docs/security/asvs-l1-audit.md`).
