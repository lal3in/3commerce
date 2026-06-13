# 13. Future Considerations

Deliberately deferred. Each item lists its trigger — the signal that it's time.

## Near-term (post-MVP, pre-launch)

| Item | Trigger |
|---|---|
| **Company registration → live mode** | Business decision; unblocks Stripe live keys, real Xero org, real `ITaxStrategy`, privacy policy/imprint |
| **Kubernetes deployment** (kind/k3d → managed) | MVP demo complete; Dockerfiles already maintained — this is the planned deployment-learning phase |
| **First real supplier importer** | Supplier contract signed; plugs into `ISupplierImporter`; also forces the dropship-vs-warehouse decision the per-line `FulfillmentSource` field has kept open |
| **Pen test / external security review of Identity** | Before first live customer; `IAuthService` seam exists precisely so a failed audit can swap in ASP.NET Identity or an IdP without redesign |
| **Stripe Tax or OSS-aware `ITaxStrategy`** | Registration country known; cross-border B2C thresholds approached |

## Product enhancements

| Item | Trigger |
|---|---|
| MFA (TOTP) and passkeys | Before live launch (admin accounts first), or first credential-stuffing signal |
| Social login (Google/Apple) | Conversion data shows registration friction |
| Discounts/promotions engine | First marketing campaign needs it |
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
| Second payment rail (PayPal/Adyen/Mollie) | Checkout data shows abandoned payments wanting other methods; `IPaymentProvider` seam ready |

## Integration opportunities

- Carrier APIs (rate quotes, label printing) once fulfillment model is decided
- Supplier EDI/API order forwarding replacing manual Fulfillment step
- Analytics pipeline (events already on RabbitMQ → warehouse) when business questions demand it
