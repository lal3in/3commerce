# Platform services

The six DB-owning services added once CI was restored ([ADR-0030](../adr/0030-deferred-services-extracted.md)).
Each owns a Postgres database + named schema, sits behind the YARP gateway with internal-claims auth, and
uses the MassTransit EF outbox. See [services.html](./services.html) for every endpoint and the use cases
per option, and the [API contracts index](../api/api_contracts_index.md).

| Service | Gateway | Port | Owns |
|---|---|---|---|
| Marketing | `/api/marketing` | 5108 | Campaigns, short links (mt5_1/5_3) |
| Pricing | `/api/pricing` | 5109 | Prices + graduated tiers (mt7_1) |
| Audit | `/api/audit` | 5110 | Central searchable audit projection (mt6_1) |
| Workflow | `/api/workflow` | 5111 | Scheduled-job run history (mt6_3) |
| Entitlement | `/api/entitlement` | 5112 | Digital-line access; consumes `OrderConfirmed` (mt7_2/7_6) |
| Usage | `/api/usage` | 5113 | Metered balances + overage billing → Payments (mt7_4/7_5) |

Marketing and Pricing are standalone domains; Audit and Workflow are event projections; Entitlement and
Usage were extracted out of Fulfillment (which now ships only physical lines).

For audience-specific positioning of these capabilities — shoppers, tenants, suppliers, admins, technical evaluators, and finance/compliance reviewers — see [Selling information](./selling-information.md).
