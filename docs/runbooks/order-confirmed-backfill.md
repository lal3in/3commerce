# Runbook — backfill orders that missed `OrderConfirmed` (ADR-0060)

**When:** an environment ran a build from before #288, where Fulfillment and Notifications competed on the
`order-confirmed` queue. Each confirmed order then reached only ONE of them: some were never fulfilled (no
shipment / hold / dropship forwarding, warehouse stock never consumed) and the rest never got their confirmation
email. Measured on the owner's dev DB (2026-10-08): 633 of 1,264 confirmed orders with a physical line had no
shipment or hold; 676 of 1,354 confirmed orders got the email.

**Tool:** [`tools/order-confirmed-backfill`](../../tools/order-confirmed-backfill/README.md) — selection logic, options
and the email-evidence rules are documented there. It reads Ordering, Fulfillment and the Notifications delivery
log, rebuilds each event with Ordering's own `OrderConfirmedFactory`, and **Sends** it to the one queue that missed
it (`queue:fulfillment-order-confirmed` / `queue:order-confirmed`) — never a Publish, so no other consumer gets a copy.

## 0. Preconditions

* The stack runs a build that contains #288 (consumer `FulfillmentOrderConfirmedConsumer` on
  `fulfillment-order-confirmed`), #290 (Fulfillment partitioned by order id) and this change (the Notifications
  worker records `Reference = order-confirmed:{orderId}` — migration `DeliveryReference`, applied on worker start).
  The tool checks both on the broker / database and refuses otherwise.
* Keep the data: bring the stack up WITHOUT `--fresh` (`scripts/dev-up.sh --with-frontends`).
* RabbitMQ management API reachable (`http://localhost:15672`, dev `guest/guest`).

```bash
dotnet build tools/order-confirmed-backfill
BIN=tools/order-confirmed-backfill/bin/Debug/net10.0/3commerce.Tools.OrderConfirmedBackfill.dll
```

## 1. Dry run — what is missing

```bash
dotnet $BIN --target both --dry-run
```

Read the report:

* `[fulfillment] MISSING (to send)` — eligible orders with a shippable line and no Shipment / HeldOrder.
* `[notifications]` evidence buckets. `Missing` is what will be emailed. If `Ambiguous` / `PredatesDeliveryLog` are
  large, the delivery log cannot say who already got an email — **stop and decide** (wider/narrower
  `--email-window`, judged by the printed lag p50/p95/max of certain matches; or accept duplicates with
  `--include-unproven-email`). Never guess.
* `skipped (Refunded|Delivered|Disputed)` — never sent.

Optional: `--list` prints every selected order (id, number, tenant — no email addresses); `--tenant <guid>` scopes.

## 2. Fulfillment

```bash
dotnet $BIN --target fulfillment --execute --limit 5   # canary: check the 5 got a shipment or hold
dotnet $BIN --target fulfillment --execute
# wait for queue:fulfillment-order-confirmed to drain (management UI → Queues), then:
dotnet $BIN --target fulfillment --dry-run              # expect MISSING 0
```

Each order either fulfils (warehouse stock consumed, dropship forwarded, shipments created) or — when stock is short
— gets an automatic Inventory hold and a captured `HeldOrder` (correct: it ships when the hold is released).
Verify (fulfillment_db):

```sql
-- no order+source has two shipments (the unique index makes this impossible; prove it)
SELECT "OrderId", "FulfillmentSource", count(*) FROM fulfillment."Shipments" GROUP BY 1, 2 HAVING count(*) > 1;
-- stock never negative
SELECT count(*) FROM fulfillment."InventoryItems" WHERE "QuantityOnHand" < 0 OR "QuantityReserved" < 0;
-- what the backfill held, and why (inventory shortage = expected)
SELECT h."Reason", count(*) FROM fulfillment."OrderHolds" h WHERE h."Status" = 'Active' GROUP BY 1;
-- nothing dead-lettered
-- management UI: queue fulfillment-order-confirmed_error must be absent or empty
```

## 3. Notifications

```bash
dotnet $BIN --target notifications --execute
# wait for queue:order-confirmed to drain, then:
dotnet $BIN --target notifications --dry-run            # expect SELECTED 0; the sent ones now EmailedByReference
```

Verify (notifications_db) — no order got a second referenced email:

```sql
SELECT "Reference", count(*) FROM notifications.deliveries
WHERE "Reference" LIKE 'order-confirmed:%' AND "Status" = 1 GROUP BY 1 HAVING count(*) > 1;
```

## 4. Ledger

The backfill posts nothing itself. COGS accrual (`OrderCostsRecognized`) was published by Ordering at confirmation
and never depended on the split queue; `ShipmentCreated` has no ledger consumer. Confirm the books are untouched
and still balanced (payments_db):

```sql
SELECT sum("DebitMinor") - sum("CreditMinor") AS trial_balance FROM payments."JournalLines";      -- 0
SELECT count(*) FROM payments."JournalLines" WHERE "Currency" IS NULL OR "Currency" = '';          -- 0
```

## Dev run, 2026-10-08 (owner's DB)

| | Before | Sent | After |
|---|---|---|---|
| Fulfillment-missing (of 1,382 shippable confirmed) | 656 | 5 (canary) + 651 | 0 — +624 orders shipped, +32 held for stock (automatic Inventory hold) |
| Confirmation emails (delivery log) | 795 · Missing 701 · Ambiguous 3 | 701 | 1,496 · Missing 0 (701 EmailedByReference) |
| Duplicate shipments / negative stock / duplicate emails | 0 / 0 / 0 | | 0 / 0 / 0 |
| Ledger lines / trial balance | 14,460 / 0 | | 14,460 / 0 |

The certain email matches lagged p50 16 ms, p95 27 ms, max 299 ms, which set the 1 s default window (10 s left 605
orders Ambiguous). Fulfillment-missing ∩ email-missing was empty: every pre-fix order had reached exactly one
consumer. The RabbitMQ management `messages` count lags by a few seconds — wait until it reads 0 three times in a
row before the verifying dry run.

## Re-running

Safe at any time: Fulfillment is selected from its own Shipment / HeldOrder records and the email side from the
delivery log, whose new rows name their order. The preflight refuses while either queue still holds messages, so an
event in flight is never re-selected. Each send has a deterministic message id per (target, order).

## Storefront duplications (`storefront-duplicated` command)

`StorefrontDuplicated` had the same split: until #288 Payments' `StorefrontDuplicatedConsumer` (copies the source's
payment accounts) and Fulfillment's carrier-copy consumer shared `storefront-duplicated`, so a duplicate got its
payment accounts OR its carrier integrations, never both. The same tool repairs it with a second command that
**Sends** a rebuilt event only to the queue whose copy is missing — `queue:storefront-duplicated` (Payments) or
`queue:fulfillment-storefront-duplicated` (Fulfillment). Both consumers copy the source's CURRENT rows and no-op when
the duplicate already has any row on their side, so nothing on a duplicate is ever overwritten.

**There is no durable source↔copy record.** Catalog stores no "duplicated from", and the `catalog.storefront.duplicate`
audit entry (Audit service, `audit."AuditEntries"`) names only the copy and the name it was given. The tool therefore
links a duplicate to its source from what the duplication left behind, and sends only when that is unambiguous:

* **Lineage** — `ProductPublication.PublishedAt` is set once and copied verbatim; the clone's publications are created
  at the clone's own `CreatedAt`. The source's (product, published-at) pairs as of that instant EQUAL the clone's
  copied pairs, and no other storefront can share such a pair. With nothing published to go by, only the admin default
  name `"X (copy)"` (the audit summary) links to the storefront named X.
* **The half that did copy** — rows on the duplicate created within `--clone-window` (default 600 s) of it are the copy
  that ran; a candidate source must have held the same providers / carriers just before.
* **Agreement** — a side is sent only when every remaining candidate (e.g. the source and earlier identical copies)
  held rows on it when the duplicate was made AND would copy the same rows. None held any → `SourceEmpty` (nothing was
  lost). Otherwise `Undetermined` — reported, never sent. Copies are decided oldest first and a copy being backfilled
  in the same run counts as holding what it receives, so a chain of split copies resolves in one run.

```bash
dotnet $BIN storefront-duplicated --target both --dry-run --list   # verdict per duplicate: complete / missing / undetermined
dotnet $BIN storefront-duplicated --target both --execute --limit 2 # canary (≤2 per target, oldest first)
dotnet $BIN storefront-duplicated --target both --execute
dotnet $BIN storefront-duplicated --target both --dry-run           # after the queues drain: 0 MISSING on both
```

Same preflight as the order backfill (queue exists, only `3commerce.Payments.Api` / `3commerce.Fulfillment.Api`
consumes it, queue empty), deterministic message id per (target, duplicate) and the `3c-backfill` header. The event's
`Name` is the audit summary (the name the live event carried); its `SourceStorefrontId` is the oldest agreeing
candidate that holds the rows — the consumers only read rows from it. Archived duplicates are skipped.

Verify afterwards: every duplicate selected now has rows on both sides, with the same copy fields (name, provider,
mode, state, default, external ref / carrier, credential ref, status, default) as its source, and no duplicate rows;
rows that existed before the run are unchanged (compare `Id`/`UpdatedAt` before and after).

<!-- DEV-RUN-STOREFRONTS -->
