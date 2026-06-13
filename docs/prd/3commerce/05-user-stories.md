# 5. User/Flow Stories

## Shopper stories

### US-1: Find a product in a large catalog
**As a** shopper, **I want to** search with typo tolerance and filter by attributes, **so that** I find the right product among thousands of third-party SKUs.
> Example: typing "blutooth hedphones" returns Bluetooth headphones, filterable by brand and price; results render server-side in < 500 ms p95.

### US-2: Buy without an account
**As a** first-time shopper, **I want to** check out as a guest with just my email and shipping address, **so that** I'm not forced through registration to give the store money.
> Example: cart (cookie-keyed) → checkout → Stripe payment sheet (card/Apple Pay) → confirmation page + email. Zero account fields.

### US-3: Convert to an account after buying
**As a** guest who just ordered, **I want to** set a password on the confirmation page, **so that** I can track this order and reuse my address next time.
> Example: "Track your order — set a password" creates an account from the order email; the guest order is attached to it.

### US-4: Track my orders
**As a** registered shopper, **I want to** see order history with live status (paid, shipped, tracking number), **so that** I don't need to contact support to know where my package is.
> Example: order page shows per-line-item status, since line items may ship from different fulfillment sources.

### US-5: Request a refund without email ping-pong
**As a** shopper with a damaged item, **I want to** open a refund request from the order itself with a typed reason and photos, **so that** I get a structured resolution instead of an inbox thread.
> Example: order → "Report a problem" → reason "arrived damaged" → RMA created → email updates at each state change → refund lands on my card (Stripe test).

## Operator stories

### US-6: Approve refunds safely
**As an** operator, **I want to** approve an RMA and have the refund execute across Stripe, the ledger, and Xero automatically, **so that** money out is always accounted for and never double-issued.
> Example: clicking "Approve & refund €34.99" triggers the refund saga; the order shows "refunded", the ledger holds a balanced reversal entry, Xero receives the posting. Clicking twice does nothing (idempotent).

### US-7: Monitor catalog imports
**As an** operator, **I want to** see each supplier import run (rows read, accepted, rejected, orphaned products), **so that** bad feed data never silently corrupts the storefront.
> Example: import dashboard row: "SampleSupplier — 10,000 read / 9,962 accepted / 38 rejected (missing price)".

## Technical (builder) stories

### US-8: Survive a dead service mid-checkout
**As the** builder, **I want** checkout modeled as a saga with a transactional outbox, **so that** killing the Payments service mid-checkout leaves no order stuck or charged-but-unconfirmed once it restarts.
> Example: integration test kills Payments after PaymentIntent creation; on restart, the saga resumes from the outbox and the order reaches a terminal state.

### US-9: Trace a request across six services
**As the** builder, **I want** OpenTelemetry trace propagation through gateway → services → RabbitMQ consumers, **so that** I can see one checkout as a single distributed trace.
> Example: a trace ID from the storefront request appears in Ordering, Payments, and the email worker spans.
