# Deployment

**Two ways to run the stack:** the **bare-run** inner-loop dev path (ADR-0009 —
`dotnet run` per service, containers for infra only) and the **containerized launch**
(ADR-0021 — all 10 app images + infra via `docker-compose.yml`, graduating to a Helm
chart validated against a `kind` cluster in CI). What is still deferred behind explicit
launch gates: **live Stripe/Xero, a real `ITaxStrategy`, an external pen test, and a real
cloud cluster** (the Helm chart is kind/CI-proven, not yet wired to a managed k8s).

## Containerized launch (compose)

One command brings the whole stack up in containers, repeatably:

```bash
scripts/launch.sh [--fresh|--reuse] [--env dev|prod]
```

| Flag | Effect |
|------|--------|
| `--fresh` | `docker compose down -v` first — drops the Postgres volume so init SQL re-runs, migrations re-apply, catalog is empty: a brand-new deployment. |
| `--reuse` | keep existing data; just (re)launch (default). |
| `--env dev` | committed dev ES256 keys, admin auto-seeded (default). |
| `--env prod` | mint a fresh keypair, inject it, and **enforce the BL-11 secret gate** (`DevSecretGuard` refuses the committed dev key outside `Development`). |

Endpoints once up: gateway `:8080`, storefront `:3000`, admin `:5200`. The catalog is
**not** auto-seeded — after a fresh launch, log into the admin **Imports** screen (or
`POST /api/catalog/admin/import-runs` with an admin session) to load the sample SKUs.

How it fits together:

- **`docker-compose.yml`** runs the 10 app images + `rabbitmq`; Postgres is a separate external instance
  (`docker-compose.db.yml`, its own volume) that `launch.sh` starts first and the app connects to, with healthchecks, `depends_on` conditions (services wait for Postgres/RabbitMQ
  healthy **and** the migrator completed), and memory limits.
- **Config** is injected as a hybrid: committed `appsettings.Container.json` per app for host
  wiring (`Host=postgres`, `amqp@rabbitmq`, YARP destinations → service names on `:8080`,
  admin → `gateway:8080`), loaded by `USE_CONTAINER_CONFIG=true` — **decoupled from
  `ASPNETCORE_ENVIRONMENT`** so the dev/prod env (secret gate + admin seeder) stays
  orthogonal. Secrets/env-mode come from `deploy/.env.<env>` (+ a prod overlay for the keys).
- **Migrations** run via `deploy/migrator` — a one-shot image that builds a framework-dependent
  **EF migration bundle** per DB-owning service and applies them before the services start.
  Design-time `DbContext` factories let the bundles build the context without the app host.

## Containerized launch (Kubernetes / Helm)

The same topology is packaged as an umbrella Helm chart at **`deploy/helm/3commerce`**:

```bash
# dev (committed keys baked into the images)
helm install 3commerce deploy/helm/3commerce -f deploy/helm/3commerce/values-dev.yaml

# prod (BL-11 gate enforced): create the Secret first
deploy/helm/make-secret.sh | kubectl apply -f -
helm install 3commerce deploy/helm/3commerce -f deploy/helm/3commerce/values-prod.yaml
```

The chart templates Postgres (init-SQL ConfigMap + `emptyDir`), RabbitMQ, the 6 services
(each with a per-service EF-bundle **initContainer** that migrates its own DB), the gateway
(+ optional Ingress), the worker, admin, and storefront. Image name == k8s `Service` name ==
container hostname, so the **same `appsettings.Container.json` serves both compose and k8s**.
CI's `kind-deploy` job builds the images, loads them into a `kind` cluster, `helm install
--wait`s, and probes the gateway — so the chart can't silently rot.

## Dockerfiles

All **10** runnable apps have a Dockerfile, built in CI's `docker` matrix
(`.github/workflows/ci.yml`) and run together by the compose/Helm paths above:

- `src/Gateway/Dockerfile`, `src/Workers/Notifications/Dockerfile`, `src/Admin/Dockerfile`
- `src/Services/{Identity,Catalog,Ordering,Payments,Fulfillment,Support}/Api/Dockerfile`
- `src/Storefront/Dockerfile` (Next.js `output: "standalone"`, non-root)
- plus `deploy/migrator/Dockerfile` (the schema migrator)

The .NET images are a two-stage build (`sdk:10.0` → `aspnet:10.0`, with `curl` for
healthchecks). **The build context must be the repo root** (central package management):

```bash
docker build -f src/Gateway/Dockerfile .
```

## Bare-run dev (ADR-0009)

The fast inner loop is unchanged — see [Getting started](./getting-started.md):

1. `docker compose -f docker-compose.infra.yml up -d` — Postgres 17 + RabbitMQ 4.
2. `dotnet ef database update` per service — migrations.
3. `scripts/run-all.sh start` — gateway + 6 services + Notifications worker (bare `dotnet run`).
4. Storefront `npm run build && GATEWAY_URL=http://localhost:8080 npm run start` (`:3000`).
5. Admin `dotnet run --project src/Admin` (`:5200`).

## Configuration & secrets

Dev defaults below; production values are launch-gated or injected per env.

### Storefront

| Key | Where | Default | Purpose |
|-----|-------|---------|---------|
| `GATEWAY_URL` | env (`src/Storefront/.env.example`; compose sets `http://gateway:8080`) | `http://localhost:8080` | The only origin the storefront talks to. |
| image `remotePatterns` | `next.config.ts` | `picsum.photos` | Dev product images from the seed importer; real CDNs added later. |

### Admin

| Key | Where | Default | Purpose |
|-----|-------|---------|---------|
| `Gateway:BaseUrl` | `appsettings.json` / `appsettings.Container.json` | `http://localhost:8080` / `http://gateway:8080` | Gateway origin. |
| `Admin:AllowedIPs` | `appsettings.json` | `""` (allow all) | Comma-separated IPs/CIDRs; empty = no restriction (dev). |
| `InternalAuth:PublicKey` | `appsettings.json` (dev) / env or Secret (prod) | dev key (committed) | Verifies internal signed claims. The **BL-11 gate** refuses the committed dev key outside `Development`. |

The seeded dev admin is `admin@3commerce.local` / `dev-admin-password-1` (Development only).

### Backend config seams (dev defaults)

| Key | Read by | Default | Notes |
|-----|---------|---------|-------|
| `ConnectionStrings:Database` / `RabbitMq` | every service | `localhost` (bare) / `postgres`,`rabbitmq` (container) | Overridden by `appsettings.Container.json` when `USE_CONTAINER_CONFIG=true`. |
| `Store:Currency` | `SampleDataImporter`, cart fallback (BL-9) | `EUR` | Configurable store currency; data model is per-entity currency (multi-currency display = future FX). |
| `Importer:TargetRows` | `SampleDataImporter` (Catalog) | `10_500` | Sample-import row count. CI sets `400` to keep the projection storm light. |
| `Stripe:SecretKey` | Payments `Program.cs` | _unset_ | If set, real `StripePaymentProvider`; **unset → `FakePaymentProvider`** (keyless dev). |
| Xero client | Payments `Program.cs` | `LoggingXeroClient` | Real OAuth2 Xero is a future swap (ADR-0017). |

## Still deferred — launch gates (PRD Appendix B)

Engineering-complete; these are **business/operational**, not code:

1. **Company registration** — blocks live **Stripe** keys, a real **Xero** org, a real
   `ITaxStrategy` (tax presence), payout currency, and privacy policy/imprint.
2. **Supplier contract** — blocks the real catalog feed and the dropship-vs-warehouse
   decision (modelled today by the per-line `FulfillmentSource`).
3. **External security review** — see `docs/security/asvs-l1-audit.md`.
4. **Real cloud cluster** — the Helm chart is `kind`/CI-proven; a managed k8s target, plus
   prod DB/RabbitMQ credential rotation and a secrets store, are the next rung (ADR-0021).

Until cleared, the platform runs on the keyless dev stack (`FakePaymentProvider` +
`LoggingXeroClient`); the swap to live rails is configuration, not new application code.
