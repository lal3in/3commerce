# order-confirmed-backfill

> Two commands: the default re-delivers `OrderConfirmed`; [`storefront-duplicated`](#storefront-duplicated--the-split-storefront-copies) re-delivers `StorefrontDuplicated`.

Operator tool for [ADR-0060](../../docs/adr/0060-unique-receive-endpoint-per-service.md). Until #288, Fulfillment's
and Notifications' `OrderConfirmedConsumer`s shared the `order-confirmed` queue, so every `OrderConfirmed` reached
only one of them. This tool finds the confirmed orders each side missed and **sends** (never publishes) a rebuilt
`OrderConfirmed` straight to the queue that missed it:

| Target | Queue | Selected when |
|---|---|---|
| `fulfillment` | `queue:fulfillment-order-confirmed` | order is `Confirmed`, not disputed, has a line Fulfillment ships (`FulfilmentType.RequiresShipping()`: Warehouse, Dropship, Unassigned), and Fulfillment has **no `Shipment` and no `HeldOrder`** for it — the two records `FulfillmentOrderConfirmedConsumer` checks before acting |
| `notifications` | `queue:order-confirmed` | order is `Confirmed`, not disputed, and the delivery log **proves** no confirmation email went out (`Missing`) |

Refunded, Delivered and Disputed orders are reported and never sent. The event is rebuilt from Ordering's order by
`OrderConfirmedFactory` — the same mapping `OrderStatusConsumer` publishes with. A `Send` to one queue reaches
only that queue's consumer: no customer is emailed twice by a fulfilment backfill and nothing is fulfilled twice by
an email backfill. Fulfillment is idempotent by order anyway (partitioned by order id since #290).

Full procedure, verification queries and expected results: [docs/runbooks/order-confirmed-backfill.md](../../docs/runbooks/order-confirmed-backfill.md).

## Usage

```bash
dotnet build tools/order-confirmed-backfill
BIN=tools/order-confirmed-backfill/bin/Debug/net10.0/3commerce.Tools.OrderConfirmedBackfill.dll

dotnet $BIN --target both --dry-run                    # counts per target, sends nothing
dotnet $BIN --target both --dry-run --list             # + every selected order (id, number, tenant — no emails)
dotnet $BIN --target fulfillment --execute --limit 5   # canary
dotnet $BIN --target fulfillment --execute
dotnet $BIN --target notifications --execute
dotnet $BIN --target both --dry-run                    # after the queues drain: 0 to send on both
```

| Option | |
|---|---|
| `--target fulfillment\|notifications\|both` | required |
| `--dry-run` / `--execute` | exactly one is required — sending is never the default |
| `--tenant <guid>` | restrict the SENDS to a tenant (repeatable). Email evidence is still matched over all tenants |
| `--limit <n>` | send at most n per target, oldest first |
| `--list` | print every selected order |
| `--email-window <s>` / `--email-skew <s>` / `--email-orphan-reach <min>` | legacy time-match tuning (defaults 1 / 1 / 30) |
| `--include-unproven-email` | also email `Ambiguous` + `PredatesDeliveryLog` orders — **will** re-email some customers |
| `--skip-preflight` | skip the RabbitMQ management checks (not advised) |

Exit codes: `0` ok · `1` error · `2` usage · `3` refused (precondition or broker preflight).

Connections: `backfill.settings.json` (local dev defaults) overridden by `BACKFILL_`-prefixed environment variables,
e.g. `BACKFILL_ConnectionStrings__Ordering=…`, `BACKFILL_RabbitMqManagement__Url=…`. Only host/port/database/user
are printed — never a password, never an email address.

## How "already emailed" is decided

The Notifications delivery log (`notifications.deliveries`) records recipient, subject, status and time. Since this
change the worker also records **`Reference = order-confirmed:{orderId}`** (migration `DeliveryReference`), so:

* `EmailedByReference` — a Sent row names the order. Exact. Every email the backfill causes is recorded this way,
  which is what makes a re-run select only what is still missing.
* Older rows have no reference and are matched by **recipient + time**: a Sent row to the order's address stamped
  `[confirmedAt − skew, confirmedAt + window]` may be that order's. Per address, only certain conclusions are drawn,
  repeatedly: a row with exactly one open candidate order is that order's (`EmailedByTimeMatch`); an order with no
  open candidate row never got one (`Missing`). The rest is `Ambiguous` (k emails for n > k orders, unknown which).
* A row no order can explain (an email sent later than the window — the worker was down) makes every `Missing`
  order of that address confirmed up to `--email-orphan-reach` before it `Ambiguous`.
* Orders confirmed before the log's first row are `PredatesDeliveryLog` — there is no evidence either way.

Only `Missing` is emailed by default. The dry run prints the bucket counts, the number of unexplained rows and the
lag distribution of the certain matches (p50/p95/max) to judge the window by.

## Preflight (before any send)

Via the RabbitMQ management API: the target queue exists, **every consumer on it is the target's own process**
(`3commerce.Fulfillment.Api` / `3commerce.Workers.Notifications` — so the fix is live and no competing consumer is
left), and the queue holds **no messages** (a previous run has drained; a re-run never re-selects orders whose
event is merely in flight). For `notifications`, the worker must have applied `DeliveryReference` (it migrates on
start) — otherwise the tool refuses, because without references a re-run could not see what it already sent.

Each send carries a deterministic message id per (target, order) and a `3c-backfill` header.

## `storefront-duplicated` — the split storefront copies

The same tool repairs the other half of ADR-0060: until #288 Payments (`StorefrontDuplicatedConsumer`, copies payment
accounts) and Fulfillment (`FulfillmentStorefrontDuplicatedConsumer`, copies carrier integrations) shared the
`storefront-duplicated` queue, so each duplicated storefront got only one of the two copies.

```bash
dotnet $BIN storefront-duplicated --target both --dry-run --list     # verdict per duplicated storefront
dotnet $BIN storefront-duplicated --target both --execute --limit 2  # canary
dotnet $BIN storefront-duplicated --target both --execute
dotnet $BIN storefront-duplicated --target both --dry-run             # after the queues drain: 0 MISSING
```

| Target | Queue | Selected when (per duplicated storefront) |
|---|---|---|
| `payments` | `queue:storefront-duplicated` | the duplicate has **no** payment account, and every candidate source had accounts when the duplicate was made and would copy the same ones |
| `fulfillment` | `queue:fulfillment-storefront-duplicated` | the same for carrier integrations |

Options: `--target payments|fulfillment|both`, `--dry-run|--execute` (one required), `--tenant <guid>` (repeatable),
`--limit <n>` (per target, oldest first), `--list`, `--clone-window <s>` (default 600), `--skip-preflight`. Extra
connections: `Catalog`, `Audit`, `Payments` (plus the existing `Fulfillment`, `RabbitMq`).

Duplicated storefronts are the `catalog.storefront.duplicate` entries of the Audit service plus any storefront Catalog
proves is a copy (it holds a publication first published before the storefront existed). **Nothing records the
source**, so it is inferred conservatively (`Storefronts/StorefrontBackfillPlanner.cs`):

1. **Lineage** — candidates are older storefronts of the tenant whose (product, `PublishedAt`) pairs at the duplicate's
   creation equal the duplicate's copied publications (`PublishedAt` is set once and copied verbatim, so only a
   duplication can share a pair). Nothing published → only a duplicate named `"X (copy)"` links to the storefront X.
2. **Copied half** — rows on the duplicate created within the clone window are the copy that ran; a candidate must have
   held the same providers / carriers just before.
3. **Name** — several candidates and a `"X (copy)"` name → the one named X.
4. **Agreement** — a side is `Missing` (sent) only if every candidate held rows on it at the time and all would copy the
   same rows; none held any → `SourceEmpty`; otherwise `Undetermined` (reported, never sent). A side on which the
   duplicate has ANY row is `Present` and never sent — owner edits are never overwritten (the consumers no-op there
   anyway). Archived or vanished duplicates are skipped.

The rebuilt event carries the audit summary as `Name` (what the live event carried) and the oldest agreeing candidate
that holds the rows as `SourceStorefrontId` — the consumers only read the rows to copy from it, so any agreeing
candidate yields the same copy. Same broker preflight (expected consumers `3commerce.Payments.Api` /
`3commerce.Fulfillment.Api`), deterministic message id per (target, duplicate), `3c-backfill` header.
