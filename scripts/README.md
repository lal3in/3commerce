# scripts/

| Script | What it does |
|---|---|
| `dev-up.sh [--with-frontends] [--seed] [--dummy-data\|--data empty\|catalog\|dummy\|mirror-prod]` | **Bare-run** local env (ADR-0009): Postgres+RabbitMQ in Docker, infra → migrate → all services → (frontends) as host processes. The light default — never builds images, so it can't OOM the Docker VM. Data profiles: `empty` = as-is/no seed, `catalog` = current sample importer (`--seed`), `dummy` = broad demo data via `dev-dummy-data.sh`, `mirror-prod` = reserved placeholder for a future sanitized prod mirror. |
| `dev-down.sh` | Stops everything `dev-up.sh` started. |
| `run-all.sh [start\|stop]` | Starts/stops the gateway + services + workers (host `dotnet run`, detached via nohup). **Verbose by default** (app Debug + EF SQL + MassTransit) → logs carry their own diagnosis; quieten with `LOG_LEVEL=Information`. Used by `dev-up.sh`. |
| `dev-dummy-data.sh [--profile core\|full\|mirror-prod]` | Seeds a running dev stack through gateway APIs. `core` imports the catalog and creates demo shoppers; `full` also attempts tenant/operator data across Entity, Marketing, Pricing, Payments, Fulfillment, Entitlement, and Usage. `mirror-prod` is intentionally a no-op placeholder until a sanitized production snapshot flow exists. |
| `build-images.sh` | Builds **all** container images with **bounded concurrency** (`PARALLEL=2`) + a Docker-memory preflight, so 13 parallel .NET builds can't OOM the VM. |
| `docker-compose.infra.pgbouncer.yml` / `docker-compose.pgbouncer.yml` | Optional compose overlays (not scripts) that expose PgBouncer on `:6432`; the app overlay routes runtime service database connections through PgBouncer while migrations stay direct-to-Postgres. Use with `--profile pgbouncer`. |
| `doctor.sh` | One-shot local-env diagnosis: infra + per-service `/health/ready` (manifest-driven) + recent errors from anything down. Run it first when something misbehaves. |
| `host-check.sh [--deep] [--logs] [target…]` | Full-stack sweep of a host: containers, service health, **RabbitMQ bus state** (stuck/competing queues), Postgres/RabbitMQ logs, observability, compose, host resources + the Colima OOM log. Runs over **local / SSH VPS / GCP** (Hostinger/EC2/GCE/Azure VMs via `ssh`); `--logs` pulls CloudWatch/GCP/Azure managed logs when configured. |
| `postgres-index-audit.sh [--list\|probe]` | Captures read-only `EXPLAIN (ANALYZE, BUFFERS)` plans for known hot PostgreSQL query paths. Use against a seeded stack before proposing partial/expression/composite indexes (ADR-0033). |
| `ci-logs.sh [branch]` | The latest CI run's failing jobs + their error lines (automates the `gh run view --log \| grep` triage). |
| `e2e-verify.sh [--live]` | Full regression: build, format, unit, integration, storefront, (live smoke). |
| `lib/hosts.sh` | Host targets for `host-check.sh` (name\|transport\|detail). Add your VPS / cloud VMs here. |
| `lib/services.sh` | **Single source of truth** for the service list (name:path:port). Edit here when adding a service. |
| `lib/preflight.sh` | `require_docker` / `require_docker_memory` guards used by the bring-up scripts. |

## GUI helper

`tools/script-console/script_console.py` is a stdlib Python/Tkinter GUI that discovers every `scripts/*.sh` file, orders scripts by normal workflow purpose, explains each script in plain language next to its Run button, streams output, and shows Docker/container/image, service-health, package-version, and host-stat status. Each row has **View/Edit .sh** so the operator can visually inspect or update the exact shell file before running it. Run it from the repo root with:

```bash
python3 tools/script-console/script_console.py
```

**Memory note:** the Docker VM (Colima) needs ~8+ GiB to build the images. Bare-run (`dev-up.sh`) only needs
Docker for Postgres+RabbitMQ, so it works on a small VM. To build images, bump it first:
`colima stop && colima start --cpu 4 --memory 12`.

**Maintaining these:** `scripts/lib/services.sh` is the single service source (most scripts derive from it). For when to update each script/doc as the codebase changes, see the **maintenance-triggers table** in `AGENTS.md` (Rules).
