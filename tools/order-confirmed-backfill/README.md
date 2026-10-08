# order-confirmed-backfill

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
