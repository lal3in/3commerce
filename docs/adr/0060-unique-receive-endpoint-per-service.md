# 0060 — One receive endpoint per service: consumer names are queue names, and must not collide

Status: Accepted — implemented
Area: Messaging / Fulfillment / Notifications / Payments
Extends: [0030](./0030-deferred-services-extracted.md) (which first recorded "distinct consumer names across services" as a rule, without a check)

## Context

Every bus is built by `AddServiceBus` (`BuildingBlocks/Infrastructure/Messaging/MassTransitExtensions.cs`)
with MassTransit's kebab-case endpoint name formatter — no prefix, no namespace — and every service connects
to the same RabbitMQ vhost. A receive endpoint (queue) is therefore named from the consumer or saga type
name **alone**: `OrderConfirmedConsumer` → `order-confirmed`, whichever service declares it.

Two class names were declared twice:

| Queue | Consumers |
|---|---|
| `order-confirmed` | Fulfillment `OrderConfirmedConsumer` (stock consumption, shipments, dropship forwarding — ADR-0028) **and** Notifications `OrderConfirmedConsumer` (confirmation email) |
| `storefront-duplicated` | Payments `StorefrontDuplicatedConsumer` (clone payment accounts) **and** Fulfillment `StorefrontDuplicatedConsumer` (clone carrier integrations) |

Two services consuming one queue are **competing consumers**: RabbitMQ round-robins each message to ONE of
them. Nothing errors and nothing dead-letters — each service simply never sees the messages its peer took.
Measured on the dev broker before the fix:

* RabbitMQ management: `order-confirmed` had 2 consumers on the connections `3commerce.Fulfillment.Api` and
  `3commerce.Workers.Notifications`; `storefront-duplicated` on `3commerce.Payments.Api` and
  `3commerce.Fulfillment.Api`.
* History: of 1,264 confirmed orders with a physical line, **633 had no Fulfillment shipment or hold**; of
  1,354 confirmed orders, only **676 got a confirmation email**.
* Controlled: 12 fresh orders through the gateway → 5 reached Fulfillment, 7 got the email, **0 both**.
  6 storefront duplications → 3 copies got only payment accounts, 3 only carriers, **0 both**.

ADR-0030 had already named the hazard (Entitlement's consumer is `EntitlementIssuingConsumer` for this
reason) but nothing enforced it. Integration fixtures give each test its own broker and in-process hosts,
so the collision is invisible there; the live journeys asserted a confirmed order, not its fan-out.

## Decision

1. **Consumer and saga type names are globally unique across services; the queue name stays derived from
   the type name.** The duplicates are renamed in the service whose concern is narrower so the established
   owners keep their queues: `FulfillmentOrderConfirmedConsumer` → `fulfillment-order-confirmed`,
   `FulfillmentStorefrontDuplicatedConsumer` → `fulfillment-storefront-duplicated`. `order-confirmed` stays
   Notifications', `storefront-duplicated` stays Payments' — both become single-owner queues.
2. **One formatter, exposed:** `MassTransitExtensions.EndpointNameFormatter` (kebab-case, unprefixed) is what
   both `AddServiceBus` overloads set and what the guard computes names with, so the test cannot drift from
   the runtime. `AddMassTransit` is called nowhere else.
3. **Enforced by a unit-lane test**, `ConsumerEndpointNameTests` (in `tests/3commerce.IntegrationTests`,
   not `Category=Integration`, so it runs in CI `build-test` and `e2e-verify` A3d). It reads the bus hosts
   from `scripts/lib/services.sh` plus the Notifications worker, loads each host's own assemblies, derives
   every consumer/saga endpoint name through the shared formatter and fails when a name belongs to two
   services. It also fails when BuildingBlocks defines a consumer (it would be registered by many
   services), when an endpoint name is set explicitly (`ReceiveEndpoint("…")`, `Endpoint(e => e.Name = …)`,
   a `ConsumerDefinition`/`SagaDefinition`) since the guard does not model those, and when a host's
   assemblies cannot be loaded (a new service must be referenced by the test project).
4. **Live check:** `scripts/e2e-verify.sh --live` L17b asserts the paid order produced a Fulfillment shipment
   (or hold) AND the Notifications confirmation email.

### Rejected: a per-service prefix on every endpoint

Giving `AddServiceBus` a service name and prefixing every queue (`fulfillment-order-confirmed`,
`ordering-checkout-state`, …) makes collisions impossible by construction, but renames ~44 queues on every
existing broker. RabbitMQ does not remove the old ones: each stays bound to its message exchanges and
keeps accumulating a copy of every message with no consumer — unbounded growth toward a memory alarm that
blocks the whole bus — and messages in flight at deploy time would be stranded in them. That needs a
coordinated drain-and-delete of every queue on every environment for no benefit over the guard. Renaming
only the colliding side changes two queue names, both NEW, and leaves no stale queue behind.

## Consequences

* After deploy, `OrderConfirmed` and `StorefrontDuplicated` reach both of their services. Nothing on the
  broker needs deleting: `order-confirmed` and `storefront-duplicated` keep their (now single) consumer
  and bindings; the two new queues are created by Fulfillment on startup. Verify on the management API
  (`/api/queues/%2F/order-confirmed` → `consumer_details[].channel_details.connection_name`, matched to
  `/api/connections` → `client_properties.connection_name`): `order-confirmed` is consumed only by
  `3commerce.Workers.Notifications`, `fulfillment-order-confirmed` only by `3commerce.Fulfillment.Api`.
  Measured after the fix on the same dev broker: 12 fresh orders → **12/12** with a shipment AND an email;
  6 storefront duplications → **6/6** with both payment accounts and carriers.
* **Orders confirmed before the fix that Fulfillment never saw stay unfulfilled** (and the other half never
  got their email). Fulfillment's processing is idempotent by order, so a backfill can re-deliver
  `OrderConfirmed` for the affected orders straight to `queue:fulfillment-order-confirmed` (a `Send`, not a
  `Publish`, so customers are not emailed twice). That backfill is a separate, operator-run follow-up.
* A new consumer of an already-consumed event must carry a service-specific name. The guard says so in its
  failure message.
