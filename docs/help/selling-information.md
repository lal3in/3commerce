# Selling information

Strategic product narrative for buyers, suppliers, tenants, admins, and technical evaluators. This is a sales-support page, not legal, tax, accounting, or security advice.

## Positioning

**3commerce is a multi-tenant commerce operating platform for physical, dropship, digital, subscription, and metered products.** It combines a customer storefront, supplier portal, admin console, CLI, and service-owned backend domains behind one gateway.

The strongest message is not "another shop template". It is: **one governed commerce spine for catalog, supply, checkout, fulfilment, refunds, ledger, supplier operations, marketing attribution, and auditability.**

## Audience-specific value

| Audience | What they care about | 3commerce message |
|---|---|---|
| Clients / shoppers | Fast browsing, clear checkout, account history, support, refunds | SSR storefront, typo-tolerant search, variant-aware cart, shipping-rate selection, saved addresses/cards, order support and RMA flow. |
| Tenants / merchants | Launching and operating one or more legal businesses/storefronts | Tenant-aware data model, storefront lifecycle, domains, RBAC, payment account readiness, pricing/promotions, Xero mapping, launch gates for live rails. |
| Suppliers | Onboarding, readiness, stock feeds, dropship orders, change requests | Supplier Portal plus Entity-backed supplier onboarding, availability feeds, dropship forwarding, maker-checker change approval, supplier payout setup. |
| Admins / operators | Control, traceability, exception handling | Admin console for orders, RMAs/refunds, ledger, imports, offers, payment accounts, payouts, Xero mappings, RBAC, mission control, and bus/health visibility. |
| Technical evaluators | Architecture, correctness, deployment, testability | .NET microservices, YARP gateway, MassTransit/RabbitMQ, service-owned Postgres schemas, EF outbox/inbox, double-entry ledger, Testcontainers integration tests, Playwright E2E, compose and Helm paths. |
| Finance / compliance reviewers | Money correctness, audit, reduced card-data exposure | Payments behind provider seam, card details kept provider-hosted, append-only balanced ledger, Xero journal mapping, audit primitives, idempotent money operations. |

## Core selling points

1. **Commerce operations are modeled end-to-end.** Product discovery, cart, checkout, payment, fulfilment, shipment, support, refund, ledger, and accounting sync are connected through explicit service contracts.
2. **Supply is composable.** Offers define how a product variant is sourced, delivered, priced, and billed. The same catalog can support warehouse stock, dropship, digital entitlement, subscription, usage, or service access.
3. **Money has a source of truth.** Payments are rails; the ledger is the record. Sales and refunds post balanced double-entry transactions, and refunds use one saga path.
4. **Tenancy and authorization are first-class.** Tenants represent legal operating businesses. Gateway-minted internal claims, PDP/PEP authorization, dynamic RBAC, and RLS helpers are built into the platform direction.
5. **Operators get real controls.** The Admin console handles catalog/imports, offers, payment accounts, payouts, mappings, RMA approval, ledger visibility, roles, suppliers, entities, and mission control.
6. **Suppliers get a portal, not an email inbox.** Readiness, stock feeds, and change requests move through a controlled supplier surface and operator approval queue.
7. **Marketing is connected to commerce.** Campaigns, short links, consent-aware behavior tracking, themes, SEO metadata, product feeds, and attribution are represented as platform capabilities.
8. **Deployment is repeatable.** Bare-run development, full compose launch, bounded image builds, migrator bundles, optional PgBouncer/Kafka/observability profiles, and Helm/kind validation are documented.

## Demonstrable proof points

| Proof point | Where to show it |
|---|---|
| Storefront browse/search/product/cart/checkout/account screens | [UI screens](./screens.md) and `docs/help/assets/screenshots/storefront-*.png` |
| Admin operations and operator surfaces | [Admin operations](./admin-operations.md), [UI screens](./screens.md) |
| Supplier portal journey | [Getting started](./getting-started.md), [Testing](./testing.md) supplier E2E notes |
| Service/API inventory | [Platform services](./services.md), `docs/api/api_contracts_index.md` |
| Security and RBAC model | [Users, roles & permissions](./roles-permissions.md) |
| Deployment credibility | [Deployment](./deployment.md) |
| Regression confidence | [Testing](./testing.md), `scripts/e2e-verify.sh --live` |
| Architecture trade-offs | [Project analysis](./project-analysis.html) |

## User-facing story

For shoppers, the buying experience is intentionally familiar: browse, search, choose a variant, add to cart, select shipping, place the order, pay through a provider-hosted flow, then use account/support pages for history and issues. The strategic value is that the ordinary shopping path is backed by service-owned domains and a ledger, not a monolithic demo checkout.

## Supplier story

For suppliers, 3commerce gives a controlled collaboration lane: supplier identity, onboarding readiness, stock-feed requests, availability feeds, dropship forwarding, and change requests. Sensitive changes such as bank or contact updates should move through maker-checker approval instead of ad-hoc email.

## Tenant / merchant story

For tenants, the platform is designed around the reality that each storefront belongs to a legal operating business. Tenants can configure storefront lifecycle, domains, catalog publication, offers, payment accounts, payout instructions, roles, permissions, tax/currency settings, and Xero mappings. Live launch still requires business decisions: legal entity, payment provider account, Xero org, tax strategy, supplier contracts, security review, and cloud target.

## Admin / operations story

Operators get a control plane: import products, maintain catalog and offers, review orders, approve RMAs, issue refunds through the single money path, inspect the ledger, post Xero summaries, manage suppliers/entities, manage RBAC, review mission-control health, and operate payment/payout setup. The message: **operational control beats hidden automation.**

## Technical story

3commerce is built as a distributed-systems learning and launch vehicle. Each service owns its database/schema; services communicate through contracts and MassTransit; writes that publish messages use the outbox; consumers are idempotent; money operations are ledger-backed; frontends are gateway-only; tests include unit, Testcontainers integration, and browser E2E. The trade-off is higher operational complexity than a single monolith, but the seams are visible and documented.

## Pros and cons

| Pros | Cons / trade-offs |
|---|---|
| Strong domain separation and service ownership | More moving parts than a monolith; requires operational discipline. |
| Ledger-backed money model and idempotent refund path | Finance correctness adds implementation complexity. |
| Multi-tenant/RBAC direction is designed in | Some tenant and service surfaces are scaffolded/capability-first and need production hardening before sale as a managed SaaS. |
| Supplier, fulfilment, shipping, digital, subscription, and usage capabilities share one supply vocabulary | Real carrier/provider integrations still need credential onboarding and live API validation. |
| Repeatable dev/compose/Helm paths | Managed cloud deployment remains a launch gate. |
| Rich wiki, screenshots, tests, and API contracts | Public commercial docs, pricing, SLAs, terms, and privacy policy still need business/legal work. |

## Compliance-sensitive messaging

Use careful language:

- Say **"provider-hosted card entry / designed for reduced PCI scope"**, not "PCI compliant" unless a formal PCI assessment has been completed.
- Say **"tax strategy seam and configurable tax behavior"**, not "tax compliant in every jurisdiction".
- Say **"Xero journal integration path / logging client in dev"**, not "production Xero integration is live" until OAuth and a real org are configured.
- Say **"security primitives and ASVS L1 self-audit"**, not "externally certified secure" until an external pen test is complete.
- Say **"privacy and retention primitives exist"**, not "GDPR/APP compliant" until a policy, processor terms, data map, and deployment region commitments are finalized.
- Say **"sandbox/fake carrier and payment adapters in dev"** when demoing local flows.

## Launch-readiness checklist before selling as production software

- Legal entity, merchant terms, supplier terms, privacy policy, cookie/consent policy, refund/returns policy, and acceptable-use policy.
- Live Stripe/payment provider account, webhook secret handling, PCI responsibility matrix, and operational runbook.
- Real Xero OAuth app/org and accounting review of chart mappings.
- Jurisdiction-specific tax strategy and evidence for inclusive/exclusive tax handling.
- Supplier contracts, real catalog feed, carrier credentials, and dropship/warehouse operating model.
- External security review and vulnerability-management process.
- Managed production cluster, backup/restore proof, secrets manager, logging/monitoring access controls, SLO/SLA decision.
- Accessibility review of customer/admin/supplier surfaces.

## Short pitch

**3commerce is an operator-grade commerce platform for businesses that need more than a storefront: governed catalog and supply, checkout, fulfilment, supplier collaboration, refunds, ledger-backed money, marketing attribution, and an auditable admin control plane — with launch gates called out honestly instead of hidden.**
