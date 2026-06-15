# Deployment

**Honest status: deployment is local-only today.** The stack runs on one machine
via bare `dotnet run` for the services + worker (ADR-0009), containers only for
infrastructure (Postgres + RabbitMQ). Per-service Dockerfiles exist and are
build-verified in CI, but **there is no Kubernetes, no cloud deploy, and no live
Stripe/Xero** — those are deferred behind explicit launch gates.

## Current local deployment

See [Getting started](./getting-started.md) for the full bring-up. In short:

1. `docker compose -f docker-compose.infra.yml up -d` — Postgres 17 + RabbitMQ 4.
2. `dotnet ef database update` per service — migrations.
3. `scripts/run-all.sh start` — gateway + 6 services + Notifications worker (bare
   `dotnet run`, backgrounded; logs in `.run/*.log`).
4. Storefront: `npm run build && GATEWAY_URL=http://localhost:8080 npm run start` (`:3000`).
5. Admin: `dotnet run --project src/Admin` (`:5200`).

## Dockerfiles

Per-runnable-project Dockerfiles exist for the **backend** and are built in CI's
`docker` matrix job (`.github/workflows/ci.yml`):

- `src/Gateway/Dockerfile`
- `src/Workers/Notifications/Dockerfile`
- `src/Services/{Identity,Catalog,Ordering,Payments,Fulfillment,Support}/Api/Dockerfile`

Each is a standard two-stage .NET build (`sdk:10.0` → `aspnet:10.0`). **The build
context must be the repo root** (central package management lives there), e.g.:

```bash
docker build -f src/Gateway/Dockerfile .
```

> **Gap to note:** there are **no Dockerfiles for the Storefront or the Admin app**,
> and no `docker-compose` that runs the application tier — only `docker-compose.infra.yml`
> for Postgres/RabbitMQ. CI only *builds* the backend images; it does not run them
> as the deployed stack (the live E2E job uses bare `dotnet run` via `e2e-verify.sh`,
> not the images).

## Configuration & secrets

Everything below is **dev-only** configuration; production values are launch-gated.

### Storefront

| Key | Where | Default | Purpose |
|-----|-------|---------|---------|
| `GATEWAY_URL` | env (`src/Storefront/.env.example`) | `http://localhost:8080` | The only origin the storefront talks to. |
| image `remotePatterns` | `next.config.ts` | `picsum.photos` | Dev product images from the seed importer; real CDNs added later. |

### Admin

| Key | Where | Default | Purpose |
|-----|-------|---------|---------|
| `Gateway:BaseUrl` | `src/Admin/appsettings.json` | `http://localhost:8080` | Gateway origin. |
| `Admin:AllowedIPs` | `appsettings.json` | `""` (allow all) | Comma-separated IPs/CIDRs; empty = no restriction (dev). Set for a real deploy. |
| `InternalAuth:PublicKey` | `appsettings.json` | dev key (committed) | Public key for verifying internal signed claims. **Dev-only key.** |

The seeded dev admin is `admin@3commerce.local` / `dev-admin-password-1`.

### Backend config seams (dev defaults)

| Key | Read by | Default | Notes |
|-----|---------|---------|-------|
| `Importer:TargetRows` | `SampleDataImporter` (Catalog) | `10_500` | Sample-import row count. CI sets `Importer__TargetRows=400` to keep the projection storm light on small runners. |
| `Stripe:SecretKey` | Payments `Program.cs` | _unset_ | If set, uses the real `StripePaymentProvider`; **unset → `FakePaymentProvider`** (deterministic, keyless dev). No live Stripe in this build. |
| Xero client | Payments `Program.cs` | `LoggingXeroClient` | Always the logging client today; real OAuth2 Xero is a future swap (ADR-0017). |
| dev simulate-payment | Payments (Development env only) | — | `POST /api/payments/dev/simulate-payment/<intent>` — the storefront's "Complete test payment (dev)" button and the live smoke tests use this. |

> **About `STORE_CURRENCY`:** the design intends a single configurable store
> currency behind `ITaxStrategy`/config (ADR-0015), but **in the current code the
> currency is hard-coded to `EUR`** (e.g. `SampleDataImporter`, the search
> provider, cart fallbacks). There is no `STORE_CURRENCY` env var wired up yet —
> treat it as a planned seam, not a live setting.

## Kubernetes / production: deferred

ADR-0009 keeps local dev on bare `dotnet run` and **explicitly defers k8s to the
deploy phase**. There are no k8s manifests, Helm charts, or cloud infrastructure in
the repo. The "MVP complete" status is on **dev/test rails** only.

## Launch gates (not code — PRD Appendix B)

From `docs/prd/3commerce/15-appendix.md` §B (and echoed in the MVP runbook), the
remaining blockers are **business/operational, not engineering**:

1. **Company registration** — country unknown → blocks live **Stripe keys**, a real
   **Xero** production org, a real `ITaxStrategy` (tax presence), payout currency,
   and privacy policy/imprint.
2. **Supplier contract** — blocks the real catalog feed and forces the
   **dropship-vs-warehouse** decision (modelled today by the per-line
   `FulfillmentSource`).
3. **External security review** — see `docs/security/asvs-l1-audit.md`.

Until those are cleared, the platform runs **only** on the keyless dev stack:
`FakePaymentProvider` for payments and `LoggingXeroClient` for accounting. The swap
to live rails is a configuration change (set `Stripe:SecretKey`, wire a real Xero
client) gated on registration — not new application code.
