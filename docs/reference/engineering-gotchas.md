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
- **Superuser-connected tests do not exercise RLS.** See the FORCE RLS note in `AGENTS.md`.

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
- **CI `browser-e2e` boots with the importer only.** Specs that need the `--data full` demo seed
  (AU/EU/US storefronts) must probe for it and `test.skip` when absent.

## Money

On any change to pricing, promotions, discounts, tax or subscriptions, ask: **what does this do to
refunds, to renewals, and to multi-storefront?** Every critical in the Sep 2026 pricing audit sat
where promotion-aware money crossed into code written before promotions — correct arithmetic fed from
the wrong source (refunds on list price, tax resolved by currency instead of storefront, renewals at
the undiscounted price, a resolver that ignores price-less offers). Two resolvers that look
interchangeable at a call site (`ResolvePricingOffer` vs `ResolveOffer`) are not. Trace the number
into Support/RMA, Payments renewals, and a second live storefront sharing the currency.

Ledger postings (Payments):

- **Every `JournalLine` sets `Currency`.** Balances key on `(AccountCode, Currency)`; a blank currency
  drops out of the per-currency P&L but still counts per-store, so the two views disagree. Prefer the
  `Ledger.Debit/Credit` helpers; hand-built `new JournalLine { … }` must set `Currency = entry.Currency`.
- **Guard zero lines.** `ck_line_one_side` requires exactly one of Debit/Credit non-zero, so the
  net-revenue line is posted only `if (netMinor > 0)` (shipping-only or usage-metered orders net to
  zero). An unguarded zero line 500s the sale webhook and leaves the order unpaid.
- **No-FX relabel posture (ADR-0041):** with no rate feed, a cost in another currency is relabelled
  into the order currency (with a once-per-order warning) rather than skipped. Reconciliation identity,
  asserted in `MoneyFlowTests`: per currency C, Σ(each store's account in C) + shared/unattributed =
  the per-currency P&L value in C.
- **Every order belongs to a real storefront.** The gateway's synthetic default storefront
  `00000000-0000-0000-0000-000000000101` is not a store; `CheckoutEndpoints.Checkout` rejects it with
  400. Any new order-creating path (seeds, E2E helpers, `e2e-verify.sh` L-flows) must attribute a real
  storefront or skip.

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
- **Don't edit a script while it runs** — bash reads incrementally, so editing `run-all.sh`
  mid-boot corrupts the running instance.
