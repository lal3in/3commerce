# Getting started — running the full stack locally

This brings up the entire 3commerce stack on one machine: infrastructure →
database migrations → backend services + gateway + worker → storefront → admin.
There is **no live Stripe or Xero** in dev; payments use a fake provider and Xero
uses a logging client.

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
| Identity / Catalog / Ordering / Payments / Fulfillment / Support | `5101` / `5102` / `5103` / `5104` / `5105` / `5106` |
| Storefront (Next.js) | `3000` |
| Admin (Blazor Server) | `5200` |
| Postgres | `5432` |
| RabbitMQ AMQP / management UI | `5672` / `15672` (guest/guest) |

> The dev "complete test payment" path and `e2e-verify.sh` address Payments
> directly on **`:5104`** (`/dev/simulate-payment/...`); everything else goes
> through the gateway.

## Step-by-step

### 1. Start infrastructure (Postgres + RabbitMQ)

```bash
colima start                                       # if using colima
docker compose -f docker-compose.infra.yml up -d
```

This starts Postgres 17 and RabbitMQ 4. On first run, `infra/postgres/init-databases.sql`
creates the **6 service databases**, roles, and extensions (FTS + `pg_trgm`).

### 2. Apply database migrations (once, and after schema changes)

```bash
for s in Identity Catalog Ordering Payments Fulfillment Support; do
  dotnet ef database update -p src/Services/$s/Infrastructure -s src/Services/$s/Api
done
```

### 3. Build, then start the gateway + 6 services + Notifications worker

```bash
dotnet build 3commerce.sln
scripts/run-all.sh start          # starts gateway, 6 services, notifications worker
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
for p in 5101 5102 5103 5104 5105 5106; do curl -fsS localhost:$p/health/ready; done
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

### 6. Log in and seed the catalog

The dev admin account is seeded:

- **Email:** `admin@3commerce.local`
- **Password:** `dev-admin-password-1`

The catalog starts empty. Log into the admin at `http://localhost:5200`, open
**Imports**, and click **Run sample importer** to load ~10,500 sample SKUs
(see [Admin operations](./admin-operations.md)). After import, the storefront's
home/search pages will show products.

## One-command equivalent

`scripts/e2e-verify.sh --live` automates steps 1–5 (infra, migrations, services,
storefront, admin) and then runs the live smoke flows. Use it to confirm the whole
stack boots and the core journeys work — see [Testing](./testing.md).

## Teardown

```bash
scripts/run-all.sh stop                                   # services + worker
pkill -f 'next-server|npm run start|3commerce.Admin'      # storefront + admin
docker compose -f docker-compose.infra.yml down           # infra (omit to keep data)
```
