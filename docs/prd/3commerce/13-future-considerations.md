# 13. Future Considerations

Deliberately deferred. Each item lists its trigger — the signal that it's time.

## Near-term (post-MVP, pre-launch)

| Item | Trigger |
|---|---|
| **Company registration → live mode** | Business decision; unblocks Stripe live keys, real Xero org, real `ITaxStrategy`, privacy policy/imprint |
| **Kubernetes deployment** (kind/k3d → managed) | MVP demo complete; Dockerfiles already maintained — this is the planned deployment-learning phase |
| **First real supplier importer** | Supplier contract signed; plugs into `ISupplierImporter`; also forces the dropship-vs-warehouse decision the per-line `FulfillmentSource` field has kept open |
| **Pen test / external security review of Identity** | Before first live customer; `IAuthService` seam exists precisely so a failed audit can swap in ASP.NET Identity or an IdP without redesign |
| ~~**Stripe Tax or OSS-aware `ITaxStrategy`**~~ — **OSS-aware strategy delivered 2026-10-28 (follow-ups to 2026-11-11)** | Shipped as `EuOssTaxStrategy` ([ADR-0063](../../adr/0063-customer-tax-invoices-and-credit-notes.md) P1b and its follow-ups: per-line destination, special VAT territories + Northern Ireland, OSS renewals). The home Member State stays per-storefront configuration until company registration. Still open: reduced-rate categories, re-deciding a subscription's scheme after the threshold is crossed, and the TEDB rate-table verification that gates EU cross-border B2C go-live. A tax engine (Stripe Tax etc.) is revisited only if the rate/category catalogue becomes a burden (ADR-0063 D17) |

## Product enhancements

| Item | Trigger |
|---|---|
| MFA (TOTP) and passkeys | Before live launch (admin accounts first), or first credential-stuffing signal |
| Social login (Google/Apple) | Conversion data shows registration friction |
| ~~Discounts/promotions engine~~ — **delivered 2026-09** | Shipped as a storefront-wide items discount plus threshold promotions ([ADR-0051](../../adr/0051-threshold-promotions-and-combinability.md)) and coupon codes ([ADR-0052](../../adr/0052-coupon-codes-and-redemption-limits.md)). See [Pricing & promotions](../../help/pricing-and-promotions.md). Still open: category-scoped thresholds, buy-X-get-Y, bulk/generated code batches, gift cards, and "you're $X away" nudges |
| Product reviews & ratings | Steady organic traffic exists |
| Multi-currency display, then pricing | Meaningful non-home-currency traffic in analytics |
| Live chat + knowledge base | Ticket volume exceeds solo-operator capacity |
| Polar adapter for a digital line (gift cards, ebooks) | Concrete digital product planned — Polar cannot process the physical catalog (digital-only MoR) |

## Architecture evolution

| Item | Trigger |
|---|---|
| Dedicated search engine (Meilisearch/Typesense) behind `ISearchProvider` | Relevance/facet complaints or catalog ≫ 100k SKUs; fed by the product events already on RabbitMQ |
| Separate Postgres instances per service | Production load isolation needs; connection-string change only, by design |
| Event sourcing for the ledger | Audit/replay requirements grow; scoped refit of Payments only — never the whole domain |
| Redis: gateway session cache + cart storage | Gateway introspection cache or cart write volume becomes measurable bottleneck |
| Marketplace capabilities (third-party sellers, Stripe Connect) | Explicit business pivot — known ~3× scope expansion, not drift-in-by-accident |
| **Structured e-invoicing & clearance** (Peppol BIS 3.0, Factur-X/ZUGFeRD, SdI, KSeF, FR PA, VeriFactu) | Selling B2B in BE/PL/FR/DE, any sale in IT, ES VeriFactu (2027) — [ADR-0063](../../adr/0063-customer-tax-invoices-and-credit-notes.md) phase 3 |
| **ViDA digital reporting (intra-EU B2B)** | 1 Jul 2030 |
| **Marketplace / deemed-supplier invoicing** | Only with the marketplace pivot |
| Second payment rail (PayPal/Adyen/Mollie) | Checkout data shows abandoned payments wanting other methods; `IPaymentProvider` seam ready |

## Integration opportunities

- Carrier APIs (rate quotes, label printing) once fulfillment model is decided
- Supplier EDI/API order forwarding replacing manual Fulfillment step
- Analytics pipeline (events already on RabbitMQ → warehouse) when business questions demand it
