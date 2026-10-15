# Engineering gotchas

Failure modes that have each cost this project at least one red CI run, a flaky suite, or a
wrong number in the ledger. Each entry: the rule, then why. Read the section that matches what
you are touching before you push.

---

## Formatting and restore (CI `build-test`)

- **Format-verify every project a change touches, Domain included.** `build-test` runs
  `dotnet format --verify-no-changes` with analyzers, so style diagnostics fail the build even
  though `dotnet build`/`dotnet test` pass locally. `IDE0040` (explicit accessibility) applies to
  interface members too — write `public Task<…>` like `IPaymentProvider`. PR #202 failed on a new
  `IDirectDebitProvider` in a `*.Domain` project nobody format-checked. The repo's pre-push hook
  (`.githooks/pre-push`) runs this check for you.
- **After `dotnet ef migrations add`, run `dotnet format` on the Infrastructure csproj that holds
  the migration** (e.g. `dotnet format src/Services/Ordering/Infrastructure/3commerce.Ordering.Infrastructure.csproj`).
  The scaffolder emits block-scoped namespaces and a charset `.editorconfig` rejects
  (`IDE0161`, `CHARSET`); formatting the Api project misses it. Bit PRs #40 and #48.
- **A new NuGet advisory on a transitive package fails restore on every PR** (`NU1903`, CI restores
  warnings-as-errors with audit on) — not caused by the PR. Under central package management a bare
  `<PackageVersion>` only binds direct references, so pin BOTH: the `<PackageVersion>` in
  `Directory.Packages.props` (comment the GHSA id) **and** a version-less `<PackageReference>` in the
  project that pulls it in. Precedents: `Microsoft.OpenApi` (every `*.Api` csproj), `SSH.NET`
  (`tests/3commerce.IntegrationTests`). Verify with `dotnet restore <proj> --force-evaluate`; for
  test-infra packages also run one integration test.

## Integration tests (Testcontainers)

- **Cap every `WebApplicationFactory`'s Npgsql pool and raise the container's ceiling.** Fixtures
  start many factories (Phase4Fixture: 7 per test), each with a default 100-connection pool, against
  one Postgres. Under CI load a random test 500s with `53300: sorry, too many clients already` — it
  looks like a bug in whichever test lost the draw; don't chase it. Use
  `new NpgsqlConnectionStringBuilder(cs) { MaxPoolSize = 10, MinPoolSize = 0 }` and
  `.WithCommand("-c", "max_connections=400")` on every shared fixture and per-class container (PR #155).
- **To drive a consumer, publish via `IBus`, not a scoped `IPublishEndpoint`.** Services use the EF
  transactional outbox: a scoped publish only leaves when a `SaveChanges` flushes it, and a test has
  none, so the consumer never runs and the test times out. Use
  `fixture.<Service>.Services.GetRequiredService<IBus>().Publish(msg)`, then poll the target DB
  (see `UsageChargeRevenueTests`, `DigitalFulfilmentTests.PollAsync`).
- **A test-built host must wait for its bus to start, because both publishing and stopping depend on it.**
  MassTransit's hosted service `StartAsync` returns at once and starts the bus in the background
  unless `MassTransitHostOptions.WaitUntilStarted` is set. `AddServiceBus` now sets it (with a bounded
  `StartTimeout`) for every service, so every fixture host and any ad-hoc host built on `AddServiceBus`
  waits; `TestBusHosting.WaitForBusStartup()` (PR #284/#285) was folded into it. A host that builds its bus
  any other way must set the option itself (`Bus.Factory…StartAsync()` already waits). Without it, two
  failures look like slow tests, and raising the timeout fixes neither:
  1. *Lost message.* A publish that reaches RabbitMQ before the queue is bound is silently dropped
     (`MockEmailCaptureTests` failed this way at 30 s and at 120 s, PR #284).
  2. *Zombie consumer.* A `WebApplicationFactory` disposed before its start finishes (a ~100 ms test,
     or create-then-dispose to declare a queue) is never stopped. `MassTransitBus.StopAsync` logs
     `Failed to stop bus … (Not Started)` and does nothing. The bus then finishes starting with an
     already-disposed `IServiceProvider` and keeps competing for its service's durable queues on the
     shared broker. Every message it takes faults (`ObjectDisposedException … 'IServiceProvider'`)
     into `_error`, so a *later* test's projection never lands (`StorefrontGoLiveReadinessTests`,
     PR #285). To check a test log, grep for `Failed to stop bus`.

  A host a test creates and never disposes does the same harm without any race: its bus keeps
  consuming from the shared broker's durable queues for the rest of the run (`CatalogOfferDuplicateGuardTests`
  and `CatalogOfferPublishTests` leaked 4 Catalog hosts, and `Phase4Fixture` never disposed its Marketing
  host). Dispose every factory a test creates (`await using`, or the test class's
  `IAsyncLifetime.DisposeAsync`). Every fixture's `CreateFactory` registers its host with a
  `TestHostTracker` (`TestBusHosting.cs`), whose bus observer counts the bus's starts and stops. At
  teardown the fixture disposes any host whose bus is still running and then fails with
  `[Test Collection Cleanup Failure (<collection>)]`, naming the service and the test that created the host. The summary
  still reads `Failed: 0`, but `dotnet test` exits 1 (`e2e-verify.sh` A4–A6 checks for the line). To see
  the names, rerun with `--logger "console;verbosity=detailed"`. That verbosity also prints the app logs:
  expect 0 `Failed to stop bus` and 0 `Cannot access a disposed object`. Compare `Bus started` with
  `Bus stopped` per broker port, but trust the tracker over that log count. MassTransit logs through an
  `AsyncLocal` LogContext, and the stop runs on whichever thread calls `Host.StopAsync` first. When that is
  a test thread that also built an ad-hoc `Bus.Factory` bus (`SpineTests.Duplicate_delivery_…`,
  `CatalogOfferPublishTests`), the line goes to a context with no logger and is lost. So a run can read
  68/67 on one port even though the tracker saw the bus stop (PR #286).
  3. *Disposed while stopping.* A disposed factory is not always a stopped bus. Under
     `WebApplicationFactory`, a minimal-hosting app's `Host.StopAsync` runs twice at once: once from the
     factory's dispose and once from the app's own `app.Run()`, which wakes on ApplicationStopping.
     `MassTransitHostedService.StopAsync` sets its stopped flag *before* it awaits the bus, so the second
     caller returns immediately. If that caller is the factory, it disposes the `IServiceProvider` while
     a busy bus is still draining. The bus never finishes stopping and keeps consuming, and every message it
     takes faults with `ObjectDisposedException 'IServiceProvider'`. Because this only happens when the bus
     is busy, it fails at random. The tracker caught it in `SpineTests` (the restarted Ordering) and in
     Phase3's lazy Catalog host. `AddTestBusHosting` registers `BusStopsBeforeHostDisposal`, which the host
     stops first: it stops MassTransit once, and both callers wait for that stop.
- **A Mac that sleeps mid-run fails whichever test is waiting, and the log looks like a lost message.** Agents
  run suites while the display is off. The Mac then runs in dark-wake windows and goes back to sleep at the end
  of one (`pmset -g log | grep -E 'Sleep|DarkWake'`), pausing the test process, the Docker VM and the broker. A
  wait loop on `DateTimeOffset.UtcNow` finds its deadline gone on wake and throws without looking again:
  `CheckoutOfferGateParityTests` failed with "Ordering approval … did not project", the test timed at `[1 s]`
  (xUnit's Stopwatch stops during sleep) and the run took 9m25s instead of ~4m (5 m asleep, 2026-10-15). Tell a
  sleep from a real timeout by that pair: a short test duration with a 30 s wait, and a long run.
  `scripts/e2e-verify.sh` re-runs itself under `caffeinate -is` on macOS; wrap ad-hoc long runs the same way
  (`caffeinate -is dotnet test …`). Write new waits with `Eventually.PollAsync` / `UntilAsync`
  (`tests/3commerce.IntegrationTests/Eventually.cs`): its budget is Stopwatch time, which pauses while the host
  sleeps, and it polls once more after the budget.
- **Superuser-connected tests do not exercise RLS.** See the FORCE RLS note in `AGENTS.md`.

## Messaging / consumers

- **A service starts serving only once its bus is up — and a broker that stays unreachable crashes it.**
  `AddServiceBus` sets `MassTransitHostOptions.WaitUntilStarted` (both overloads), so `Host.StartAsync`
  waits until every receive endpoint's queue is declared, bound and consuming. Kestrel starts after every
  other hosted service (`WebApplicationBuilder` registers it last), so until then the port does not even
  accept connections: `doctor.sh` shows `000`, not `503`. Before, the bus started in the background: a
  service answered requests and published (outbox) before its own queues existed, and on a FRESH broker
  RabbitMQ drops a message published to an exchange with no queue bound yet (first deploy, a wiped broker).
  The start is bounded by `Messaging:StartTimeoutSeconds` (default 120, env `Messaging__StartTimeoutSeconds`):
  MassTransit retries the connection until then, and then the start throws `RabbitMqConnectionException:
  Broker unreachable` and the process exits non-zero. Compose (`restart: unless-stopped`) and Kubernetes
  restart it; bare-run does not, but `dev-up.sh` starts no service before RabbitMQ is healthy. A bare-run
  service that is down with `Broker unreachable` in `.run/<svc>.log` just needs RabbitMQ up and a restart.
  `BusStartupTests` pins the wait, the bound and the slow-broker case.

- **A consumer's class name IS its queue name — and the broker is shared, so it must be unique across
  services (ADR-0060).** Every bus uses `MassTransitExtensions.EndpointNameFormatter` (kebab-case, no
  prefix, one vhost), so `OrderConfirmedConsumer` → queue `order-confirmed` in whichever service declares
  it. Fulfillment and Notifications both did: they became COMPETING consumers of one queue, so each
  `OrderConfirmed` reached only one of them — on the dev broker 633 of 1,264 confirmed physical orders had
  no shipment and only 676 of 1,354 got a confirmation email. Fulfillment and Payments did the same with
  `StorefrontDuplicatedConsumer` (a duplicated store got its carriers OR its payment accounts). Nothing
  errors; per-fixture brokers in the integration tests hide it. Name a consumer for its service when the
  message is shared (`FulfillmentOrderConfirmedConsumer`, `EntitlementIssuingConsumer`);
  `ConsumerEndpointNameTests` (unit lane) fails on any queue two services would share, and
  `e2e-verify --live` L17b asserts the paid order reached Fulfillment AND Notifications. To check a live
  broker, map each queue's consumers to the client that opened their connection — every queue must be
  consumed by ONE service (several replicas of it are fine):
  `curl -s -u guest:guest localhost:15672/api/consumers | jq -r '.[] | "\(.queue.name) \(.channel_details.connection_name)"'`
  then `curl -s -u guest:guest localhost:15672/api/connections | jq -r '.[] | "\(.name) \(.client_properties.connection_name)"'`
  (the second column is `3commerce.<Service>.Api` / `3commerce.Workers.Notifications`).

- **Two consumers that write different columns of one row: give each its own row — never
  read-then-insert, and don't rely on an upsert either.** Catalog's carrier and payment readiness
  consumers shared `StorefrontServiceReadiness`. Both events for a new storefront arrive together, both
  saw no row, both inserted, and one failed with `23505` (PK). Retry hid it, so the only symptom was an
  error log per new storefront. An `INSERT … ON CONFLICT DO UPDATE` does NOT fix this here: the EF outbox
  runs every consumer in a **REPEATABLE READ** transaction (MassTransit's default), and its snapshot is
  taken at the inbox lock, before the consumer runs. A row that a concurrent transaction commits after
  that point makes the upsert (or a plain `UPDATE` of a shared row) fail with `40001 could not serialize
  access`. The fix: one table per signal, each written by its own consumer with a single schema-qualified
  upsert, and a view that combines them for readers. Tables with one writer per row never contend.
  The same holds for two events of ONE signal for one row (a carrier configured, then activated): run in
  parallel they fail with 40001, and the retried, older value can land last. Give such an endpoint
  `ConcurrentMessageLimit = 1` (Catalog `Program.cs`) so it applies them one at a time in queue order.
- **A projection 20–30 s late after a busy moment: the bus-outbox deliveries collided (ADR-0007 amendment
  2026-10-16).** After any pass of 10+ messages, MassTransit 8's `BusOutboxDeliveryService` runs up to SIX delivery
  transactions at once (more with replicas). Each locks the oldest batch with
  `… "OutboxState" ORDER BY "Created" LIMIT 1 FOR UPDATE SKIP LOCKED`. Under the outbox's REPEATABLE READ, a batch a
  sibling had just changed raised `40001 could not serialize access due to concurrent update`. The delivery then
  backed off 3 s, then up to 30 s, and EVERY pending message of the service waited.
  - Symptoms: `StorefrontReadinessCrossServiceTests` (30 s) and `AuditTimelineProjectionTests` (20 s) timed out on
    roughly one full run in four. They always passed alone, and resources were fine.
  - To confirm, run with `--logger "console;verbosity=detailed"` and grep
    `could not serialize access due to concurrent update` next to `OutboxState`.
  - Fix: `AddServiceBus` runs the DELIVERY transaction READ COMMITTED (`OutboxDeliveryIsolation`), which skips a
    changed row instead of failing. Consumers stay REPEATABLE READ.
  - Don't "fix" it with one delivery at a time (`QueryMessageLimit = 10`). That delivered about 70 msg/s, a 10.5k
    import took 2.5 min, and the next host's messages waited behind the backlog.
  - A test whose host stages a big outbox backlog lets it drain before disposing the host (`CatalogSearchTests`).
    Otherwise the next host on the shared DB delivers the backlog first.
- **…and parallel delivery does not keep publish order, so a "current truth" event needs `AsOf` (ADR-0043
  amendment 2026-10-16).** Each request or consumer that publishes is its own outbox batch. When Payments' "submitted"
  (readiness `false`) and "activated" (`true`) reached Catalog inverted, its blind upsert stuck on `false`.
  `ConcurrentMessageLimit = 1` only keeps QUEUE order. Fix: the event carries `AsOf` (the time the publisher read
  the state) and the projection keeps the newer copy (`… DO UPDATE … WHERE r."AsOf" <= EXCLUDED."AsOf"`). A null
  `AsOf` comes from a legacy publisher and still applies. `ReadinessOutboxOrderingTests` bursts the outbox and then
  makes the two transitions. On failure it prints each delivery (value, AsOf, message id, retry, fault) and the
  stored row. The readiness test's own failure message also tells the cases apart: "no row" means the event never
  arrived, a row with a stale flag means an inversion.
- **One writer, several messages for one key: partition the endpoint by that key.** Support's
  `OrderSnapshotConsumer` is the only writer of `OrderSnapshot`, yet two `OrderConfirmed` for one order
  with different message ids (the inbox only dedupes a repeated id) consumed in parallel both saw no row
  and one failed with `23505`. `INSERT … ON CONFLICT DO NOTHING` does not fix it either: under REPEATABLE
  READ a conflicting row committed after the snapshot raises `40001 could not serialize access` instead of
  being skipped (checked on Postgres 17; READ COMMITTED skips it silently). Use
  `endpoint.UsePartitioner<T>(endpoint.CreatePartitioner(n), m => m.Message.Key)` from
  `.Endpoint(e => e.AddConfigureEndpointCallback(…))` (`OrderSnapshotConsumer.PartitionByOrder`; not a
  `ConsumerDefinition`, which `ConsumerEndpointNameTests` rejects). That overload sits on the endpoint's message
  pipe, outside the per-consumer outbox filter, so the second message for a key opens its transaction only
  after the first has committed. Unlike `ConcurrentMessageLimit = 1`, other keys still run in parallel. The
  partitioner is per process, so replicas competing for the queue can still race; retries cover that.
- **Several event TYPES that update one row: share ONE partitioner across them.** Ordering's
  `OrderStatusConsumer` applies CheckoutCompleted, OrderCancelled, RefundCompleted, PaymentDisputed and
  PaymentChargedBack to the same `Orders` row, and they arrive together (a dispute lost at once publishes
  Disputed + ChargedBack back to back; refunds follow each other). In parallel, all but one update failed
  with 40001, and the retried event could land after a later one: a partial refund retried after the full
  refund is skipped (the order is no longer Confirmed), losing `PartiallyRefunded` (a negative-control run
  reproduced it). `OrderStatusConsumer.PartitionByOrder` creates one `IPartitioner` and passes it to
  `UsePartitioner<T>` for every message type, keyed by order id, from
  `.Endpoint(e => e.AddConfigureEndpointCallback(…))` (a `ConsumerDefinition` fails `ConsumerEndpointNameTests`).
  An order's events then apply one at a time in queue order; other orders stay parallel. Per-type
  partitioners would still let two different event types of one order overlap.
- **A row shared by MANY keys (a counter), or written from outside the partition, needs more than a
  partitioner (ADR-0007 amendment 2026-10-11).** Per-storefront order numbers came from one
  `OrderNumberSequences` row read-then-incremented inside the confirming consumer; the endpoint is
  partitioned by ORDER id, so two orders of one storefront confirmed in parallel and collided (`40001`, or
  `23505` on the store's first order). Allocate such a value OUTSIDE the outbox snapshot: one
  `INSERT … ON CONFLICT DO UPDATE SET n = n + 1 RETURNING …` in its own READ COMMITTED transaction on its own
  connection (`OrderNumberAllocator`) — it queues on the row lock and never fails; a rolled-back consumer
  burns its value (gaps). Give that connection its own Npgsql pool (distinct `Application Name`): consumers
  that already hold every main-pool connection would otherwise wait on themselves. An HTTP endpoint that
  updates a row consumers also write (`mark-delivered` → `Orders`) gets an EF concurrency token — Npgsql maps
  a `uint` `IsRowVersion()` property to `xmin`, no column or DDL — and turns `DbUpdateConcurrencyException`
  into a 409 problem. A consumer keyed by something else (EmailVerified → every guest order with that email)
  must not update the rows itself: publish one per-key message (`GuestOrderOwnerVerified`) and consume it on
  the keyed endpoint through the SAME partitioner instance.

## Browser E2E (Playwright)

- **A seed POST gated by an async cross-service projection must retry until accepted.**
  `POST /api/support/rma` returns 404 until Support's `OrderSnapshot` projection lands, which lags
  when an earlier spec floods the bus. Fire-and-forget silently drops it and a later poll times out —
  raising the poll timeout does not help. Wrap the POST in
  `expect.poll(async () => (await request.post(…)).status(), { timeout }).toBe(202)` (stops on the
  first 202, so no duplicates), then poll the read model. Signature: passes alone, fails in the full suite.
- **Auth-heavy specs can hit the gateway's auth rate limit.** Production caps login/register/reset at
  30/min per `{tenant}:{storefront}:{IP}` (ADR-0044); the whole suite runs from one IP. `run-all.sh`
  raises `RateLimiting__AuthPermitLimit` for the bare-run dev/E2E gateway only. A 429, or a
  `beforeAll` user-create returning `undefined`, after adding specs means this budget — raise the
  dev/E2E lever, never weaken assertions or the production default.
- **Never turn a broken precondition helper into a skip.** `driveCheckout()` once returned `false` on
  ANY error, so the three "after a real checkout" portal cases skipped on every local run while the
  real cause (global `products[0]` was an unapproved approval-gate fixture → checkout 400) stayed
  hidden — and so did a Loki that 503'd every full-size collector batch (4 MB ingester gRPC limit, see
  `deploy/observability/loki.yaml`). Skip only when the precondition is genuinely
  absent (no demo store); once it exists, throw with the step, status and body.
- **Don't re-click a synchronous action inside `toPass`.** The admin sample import runs inside the
  POST (6–40 s on a busy stack); a `toPass { click; expect(3 s) }` re-clicked as soon as the button
  re-enabled, which cleared the status and started another import. Click only while idle, then
  wait on the feedback locator itself with a timeout sized to the real work.
- **CI `browser-e2e` boots with the importer only.** Specs that need the `--data full` demo seed
  (AU/EU/US storefronts) must probe for it and `test.skip` when absent.

## Money

On any change to pricing, promotions, discounts, tax or subscriptions, ask: **what does this do to
refunds, to renewals, and to multi-storefront?** Every critical in the Sep 2026 pricing audit sat
where promotion-aware money crossed into code written before promotions — correct arithmetic fed from
the wrong source (refunds on list price, tax resolved by currency instead of storefront, renewals at
the undiscounted price, a resolver that ignores price-less offers). Two resolvers that look
interchangeable at a call site (`ResolvePricingOffer` vs `ResolveSupplyOffer`/`ResolveLineOffer`, formerly `ResolveOffer`) are not. Trace the number
into Support/RMA, Payments renewals, and a second live storefront sharing the currency.

Ledger postings (Payments):

- **Every `JournalLine` sets `Currency`.** Balances key on `(AccountCode, Currency)`; a blank currency
  drops out of the per-currency P&L but still counts per-store, so the two views disagree. Prefer the
  `Ledger.Debit/Credit` helpers; hand-built `new JournalLine { … }` must set `Currency = entry.Currency`.
- **Guard zero lines.** `ck_line_one_side` requires exactly one of Debit/Credit non-zero, so the
  net-revenue line is posted only `if (netMinor > 0)` (shipping-only or usage-metered orders net to
  zero). An unguarded zero line 500s the sale webhook and leaves the order unpaid.
- **Non-order charges post tax too (ADR-0063 P4).** A subscription renewal and a usage charge are shelf-priced
  (ADR-0038): on an inclusive store their tax is inside the charge, so posting them with `taxMinor: 0` books the
  GST/VAT as revenue. Use `RecurringChargeTax.For` (checkout's split) with the store's projected rate in force
  (`StorefrontLedgerAccounts.TaxRateBasisPoints`) and the subscription's snapshotted terms (`TaxInclusive`,
  `TaxExempt`). Exclusive regimes still charge the bare price and post no tax — an owner decision, not an oversight.
  Renewals/usage have no `Payment` row, so the refund saga and the dispute webhook cannot reach them.
- **No-FX relabel posture (ADR-0041):** with no rate feed, a cost in another currency is relabelled
  into the order currency (with a once-per-order warning) rather than skipped — today only the carrier
  cost; COGS can no longer cross currencies (the supply offer is in the order's currency). Reconciliation identity,
  asserted in `MoneyFlowTests`: per currency C, Σ(each store's account in C) + shared/unattributed =
  the per-currency P&L value in C.
- **Every order belongs to a real storefront.** The gateway's synthetic default storefront
  `00000000-0000-0000-0000-000000000101` is not a store; `CheckoutEndpoints.Checkout` rejects it with
  400. Any new order-creating path (seeds, E2E helpers, `e2e-verify.sh` L-flows) must attribute a real
  storefront or skip — **and check out in that storefront's currency** (ADR-0059: a cart in another
  currency is a 400; the cart cookie is shared across stores on one host, so empty it between stores).
- **Availability is decided per storefront AND currency, the same way in Catalog and Ordering** (ADR-0059):
  an offer only "covers" a line on its own store (or all stores) in its own currency. Change the covering
  rule in `ProductsEndpoints` and `OfferResolution.IsSupplyAvailable` together, or the listing and checkout
  disagree again.
- **One offer decides price, supply and cost; reversals reverse what was BOOKED** (ADR-0059 Amendment 2).
  `ResolveLineOffer` = the price-setting offer (`ResolvePricingOffer`) or, when the catalogue price stands,
  `ResolveSupplyOffer` (covering this store/all in the order's currency ∧ approved); both rank variant →
  store-scoped → `Priority`. It shares `Covers` with the gate, so change them together, and never take the
  supply from `ResolvePricingOffer` alone (it drops price-less and out-of-window offers) nor from
  `ResolveSupplyOffer` alone (a price-less offer could then ship a line another offer priced). Checkout
  snapshots the offer's unit cost on the line (`SupplierUnitCostMinor`); confirmation accrues that and records
  `BookedSupplierCostMinor`; RMA values returns from the booked figure. Never re-resolve offers for an existing
  order (an edited/re-scoped offer would book one cost and reverse another).

## Privacy and telemetry

- **Never log a secret or a full email address outside Development — the logs ship to Loki.** The
  Notifications `LoggingEmailSender` once logged every email body (verification and password-reset links are
  bearer secrets) at `Information` with no environment guard (observability audit P0-2). Now only a
  `Development` host logs full emails (the live lane greps links from `.run/notifications.log`); every other
  environment logs subject, reference and a `rcpt-<sha256/12>` recipient hash, and `EmailSenderGuard` refuses
  to boot a non-Development worker unless `Notifications:Email:AllowLoggingSender=true` acknowledges a sandbox.
  Any new log line carrying user data follows the same rule: gate on `IsDevelopment()` or log a pseudonym.
- **The collector redacts as a backstop, not a licence.** `transform/redact` (dev
  `deploy/observability/otel-collector-config.yaml` + the Helm copy — keep them identical) strips `token=`/
  `password=` values, `Bearer` credentials and email addresses from log bodies and log/span attributes, and
  deletes `Authorization`/`Cookie` attributes. It only sees OTLP: console output and `.run/*.log` are not
  redacted. Keep its regexes free of backslashes and dollar signs (OTTL string escaping + collector env
  expansion); validate a change with `otelcol-contrib validate --config=…` on the pinned 0.110.0.
- **Retention is explicit:** Loki 30 d (compactor `retention_enabled` + `delete_request_store` — retention is a
  no-op without both), Tempo 48 h dev / 14 d Helm, Mimir 30 d dev / 13 months (`395d`) prod values, the dev
  Prometheus scrape view 15 d (a `command` flag — overriding `command` drops the image defaults, so they are
  repeated there).

## Local dev stack

- **The infra is all-or-nothing — never start/stop individual infra containers by hand; use
  `scripts/dev-up.sh` / `scripts/dev-down.sh`.** The set (every `docker-compose.infra.yml` service under
  `portals` + the observability services borrowed from `docker-compose.yml`) has ONE definition,
  `scripts/lib/infra.sh`. A hand-started postgres + rabbitmq + valkey passes every port check while Kafka,
  pgAdmin and the whole telemetry pipeline are gone. `scripts/doctor.sh` reports `up` / `down` /
  `PARTIAL (missing: …)` and exits 1 on PARTIAL; `dev-up.sh` heals PARTIAL to full; `e2e-verify.sh --live`
  refuses to run on it. A Colima/Docker restart is a classic source: only the borrowed observability services
  carry `restart: unless-stopped`, so they come back on their own and the rest do not.
  Running dev-up from another checkout/worktree recreates postgres/pgadmin (their bind-mount source path
  changes) — expected and harmless: the data lives in the named volumes.
- **Never `pkill -f` a pattern that could match stack processes** — it has killed the running
  stack's own services mid-seed. Target explicit PIDs; ports are the source of truth
  (`lsof -nP -iTCP:<port> -sTCP:LISTEN`). `scripts/lib/procs.sh` (`reap_port`, `prune_stale_pids`)
  is the shared reaper.
- **Never `docker volume prune`.** Once the stack's containers are removed, the named
  `3commerce-infra_pgdata` volume shows as dangling and a prune destroys the dev database. Remove
  leaked volumes by explicit id.
- **`docker exec … <<'SQL'` without `-i` silently does nothing** (stdin isn't forwarded). Use
  `psql -c '…'` or `docker exec -i`.
- **Ephemeral service hosts ignore `ASPNETCORE_URLS` unless started with `--no-launch-profile`** —
  `launchSettings.json`'s port wins, and curls against the env-var port return empty (this once
  overwrote `docs/api/*.openapi.json` with empty output).
- **The Docker VM clock drifts after macOS sleep.** Loki/Prometheus *instant* queries with relative
  windows (`[15m]`) then return empty while data exists (`[1h]` still works). In tests use
  `query_range` with client-supplied start/end.
- **Seed orders the way a shopper would, and never file a failed checkout under `allowed_4xx`.** The
  `--data full` seed once walked EVERY tenant storefront (dozens of Draft/Paused E2E leftovers → "not
  currently open for orders"), reused per-user carts that a failed checkout left full (every later add/
  checkout of that user failed too), and bought subscriptions without a saved card — 30 of 49 checkouts
  400'd while the run looked green. `dev-dummy-data.sh` now orders only on its own live demo stores,
  for products sellable there, from an emptied cart, and a failed checkout prints its body and exits 4.
  It also never publishes "e2e" fixtures to the demo stores: E2E leftovers shift the global catalogue
  pages, and a scenario fixture as a store's first product breaks the PDP/cart/offer specs.
- **A renamed query parameter is silently ignored, not rejected.** `GET /api/catalog/admin/offers`
  renamed `productId=` to `product=` (#255); the seed's dedupe kept sending `productId=`, got every offer
  of the supplier back, and skipped all but one per-store COGS offer for weeks. When a caller filters a
  list, also check the filtered field in the response.
- **Don't edit a script while it runs** — bash reads incrementally, so editing `run-all.sh`
  mid-boot corrupts the running instance.
