# Getting started — running the full stack locally

This brings up the entire 3commerce stack on one machine: infrastructure →
database migrations → backend services + gateway + worker → storefront → admin → supplier portal.
There is **no live Stripe or Xero** in dev; payments use a fake provider and Xero
uses a logging client. If you are demoing the stack to a buyer, supplier, or technical
evaluator, pair this runbook with [Selling information](./selling-information.md) so
fake/sandbox rails and launch gates are described accurately.

## Prerequisites

| Requirement | Notes |
|-------------|-------|
| **Container runtime** | Docker or **colima** (`colima start`). Needed for Postgres + RabbitMQ, and for Testcontainers-based integration tests. |
| **.NET 10 SDK** | Installed user-locally at `~/.dotnet` (no sudo). `DOTNET_ROOT=$HOME/.dotnet`; ensure `~/.dotnet` and `~/.dotnet/tools` are on `PATH`. `.envrc` (direnv) sets this for you — run `direnv allow` after editing it. |
| **Node.js** | For the Next.js storefront and Playwright. CI uses Node 22. |
| **dotnet-ef tool** | For applying migrations: `dotnet tool install --global dotnet-ef`. |

### Ports used

| Component | Port |
|-----------|------|
| Gateway (YARP, public origin) | `8080` |
| Backend services | `5101`–`5113` (Identity, Catalog, Ordering, Payments, Fulfillment, Support, Entity, Marketing, Pricing, Audit, Workflow, Entitlement, Usage) |
| Storefront (Next.js) | `3000` |
| Admin (Blazor Server) | `5200` |
| Supplier Portal (Blazor Server) | `5300` |
| Postgres | `5432` |
| RabbitMQ AMQP / management UI | `5672` / `15672` (guest/guest) |

> The dev "complete test payment" path and `e2e-verify.sh` address Payments
> directly on **`:5104`** (`/dev/simulate-payment/...`); everything else goes
> through the gateway.

## Step-by-step

### 1. Start infrastructure (Postgres + RabbitMQ)

> **Quick start (bare-run, recommended).** One command brings up infra + migrations + every
> service + the frontends, reusing the already-built solution — it never builds container images,
> so it can't OOM a small Docker VM:
>
> ```bash
> scripts/dev-up.sh --with-frontends --seed             # catalog only; stop with: scripts/dev-down.sh
> scripts/dev-up.sh --with-frontends --data smoke       # fast deterministic fixture manifest
> scripts/dev-up.sh --with-frontends --dummy-data       # broad demo data (--data full)
> ```
>
> The manual steps below are the same thing, broken out. For the **containerized** stack, build images
> with `scripts/build-images.sh` (bounded concurrency + memory preflight) — a plain
> `docker compose up --build` builds all 13 .NET images in parallel and will OOM a 6 GiB Colima.

```bash
colima start                                       # if using colima
docker compose -f docker-compose.infra.yml up -d
```

This starts Postgres 17 and RabbitMQ 4. On first run, `infra/postgres/init-databases.sql`
creates the **13 service databases**, roles, and extensions (FTS + `pg_trgm`).

### 2. Apply database migrations (once, and after schema changes)

```bash
for s in Identity Catalog Ordering Payments Fulfillment Support Entity Marketing Pricing Audit Workflow Entitlement Usage; do
  dotnet ef database update -p src/Services/$s/Infrastructure -s src/Services/$s/Api
done
```

### 3. Build, then start the gateway + 13 services + Notifications worker

```bash
dotnet build 3commerce.sln
scripts/run-all.sh start          # starts gateway, 13 services, notifications worker
```

`scripts/run-all.sh` runs each app via bare `dotnet run --no-build` in the
background (ADR-0009). Logs land in `.run/<name>.log`, PIDs in `.run/<name>.pid`.
Stop everything with:

```bash
scripts/run-all.sh stop
```

Check readiness (each service exposes `/health/ready`, **internal only** — not via
the gateway):

```bash
for p in {5101..5113}; do curl -fsS localhost:$p/health/ready; done
```

### 4. Start the storefront (`:3000`)

Dev mode (hot reload):

```bash
cd src/Storefront
npm install                       # first time only
npm run dev                       # next dev -p 3000
```

Or production mode (what the live smoke tests use):

```bash
cd src/Storefront
npm run build
GATEWAY_URL=http://localhost:8080 npm run start
```

The storefront reads `GATEWAY_URL` (default `http://localhost:8080`) — see
`src/Storefront/.env.example`. It talks **only** to the gateway.

### 5. Start the admin console (`:5200`)

```bash
ASPNETCORE_URLS="http://localhost:5200" dotnet run --project src/Admin
```

(The live verification script runs the built DLL directly with
`ASPNETCORE_ENVIRONMENT=Development` to bind `:5200`.) The admin reads
`Gateway:BaseUrl` (default `http://localhost:8080`).

### 6. Start the supplier portal (`:5300`, optional)

```bash
ASPNETCORE_URLS="http://localhost:5300" dotnet run --project src/SupplierPortal
```

The supplier portal also reads `Gateway:BaseUrl` and stays gateway-only.

### 7. Log in and seed the catalog

The dev admin account is seeded:

- **Email:** `admin@3commerce.local`
- **Password:** `dev-admin-password-1`

The catalog starts empty. Log into the admin at `http://localhost:5200`, open
**Imports**, and click **Run sample importer** to load ~10,500 sample SKUs
(see [Admin operations](./admin-operations.md)). After import, the storefront's
home/search pages will show products.

> **Payments in dev run in mock mode.** With no provider credentials, Payments
> defaults to `LocalMock` (`appsettings.Development.json`): checkouts complete
> the full saga and ledger without any external call, and each one emits a
> **TEST ONLY / MOCK PAYMENT** email (with a redacted payload) to the
> Notifications worker log (`.run/notifications.log`). See
> [Testing → payment modes](./testing.md) to point it at your inbox or move to
> sandbox credentials.
>
> **Operator MFA** is available on the admin **Security** page (`/security`):
> enroll a TOTP factor, manage recovery codes, and set the tenant MFA policy —
> see [Roles &amp; permissions](./roles-permissions.md).

## One-command equivalent

`scripts/dev-up.sh --with-frontends --seed` does steps 1–7 in one go (add `--fresh` to start from an empty
database, `--data full` for the broad demo data). It prints **`Up.`** only once every surface is genuinely
**ready** — gateway, RabbitMQ, pgAdmin, Kafka UI (cluster *online*), Grafana, Loki, Tempo, Mimir and the
frontends — because several keep warming up well after their containers start. Anything still not ready
after its time budget is named, and the script exits **3** so a chained test run does not start on a
half-ready stack. It also starts the storefront from a clean `.next/`, since the process it replaces may
have died mid-compile.

`scripts/e2e-verify.sh --live` then runs the live smoke flows — against that running stack as-is, or, on a
clean machine, against one it boots and tears down itself. See [Testing](./testing.md).

## Or: containerized launch

The steps above are the **bare-run** dev loop (ADR-0009). To run the whole stack in
containers instead — as a repeatable, deployable launch — use:

```bash
scripts/launch.sh --fresh --env dev     # brand-new deployment (build + up)
scripts/launch.sh --reuse --env dev     # relaunch, keep data
```

Data choices for bare-run dev:

- no flag / `--data empty` — leave databases as they are after migrations.
- `--seed` / `--data catalog` — current behavior: trigger the Catalog sample importer.
- `--data smoke` — run `scripts/dev-dummy-data.sh --profile smoke` for the fast deterministic fixture manifest used by Playwright.
- `--dummy-data` / `--data dummy` / `--data full` — run `scripts/dev-dummy-data.sh --profile full`, which uses gateway APIs to create broad demo data, scenario products/offers, and representative historical flows where public/admin APIs exist.
- `--data exhaustive` — run the slower state-rich hook for local/nightly validation as it grows.
- `--data mirror-prod` — reserved for a future sanitized production mirror; it intentionally fails today rather than pulling production data.

This brings up all 18 app images + Postgres + RabbitMQ via `docker-compose.yml`, applies
migrations with an EF-bundle migrator, and exposes the same ports (gateway `:8080`,
storefront `:3000`, admin `:5200`, supplier portal `:5300`). The catalog comes up empty — seed it via the admin
Imports screen. Full details (fresh/reuse, dev/prod, Helm/k8s) are in
[Deployment](./deployment.md).

## Teardown

```bash
scripts/dev-down.sh            # stop services, frontends and infra — keeps all data
scripts/dev-down.sh --clean    # also drops the dev data volumes (what dev-up.sh --fresh does first)
```

It stops processes by recorded pid and by port, never with a `pkill -f` pattern (a pattern also kills
processes this stack did not start).

**Unused volumes are cleaned up automatically.** The containerised stack (`scripts/launch.sh`) keeps its own
state in compose project `3commerce` — e.g. `3commerce_rabbitmq_data` — which bare-run dev never mounts, so
after switching back to bare-run Docker shows it as *unused* indefinitely. `dev-down.sh --clean` (and so
`dev-up.sh --fresh`) removes such volumes; a plain `dev-up.sh` / `dev-down.sh` only names them, because
`launch.sh --reuse` would want them back. Never touched: the telemetry volumes bare-run dev borrows from that
project (Loki/Tempo/Mimir/Prometheus) and `launch.sh`'s external Postgres (`3commerce-db_dbdata`). Rules live
in `scripts/lib/volumes.sh`.
