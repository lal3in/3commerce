# scripts/

| Script | What it does |
|---|---|
| `dev-up.sh [--with-frontends] [--seed]` | **Bare-run** local env (ADR-0009): Postgres+RabbitMQ in Docker, infra → migrate → all services → (frontends) as host processes. The light default — never builds images, so it can't OOM the Docker VM. |
| `dev-down.sh` | Stops everything `dev-up.sh` started. |
| `run-all.sh [start\|stop]` | Starts/stops the gateway + services + workers (host `dotnet run`, detached via nohup). **Verbose by default** (app Debug + EF SQL + MassTransit) → logs carry their own diagnosis; quieten with `LOG_LEVEL=Information`. Used by `dev-up.sh`. |
| `build-images.sh` | Builds **all** container images with **bounded concurrency** (`PARALLEL=2`) + a Docker-memory preflight, so 13 parallel .NET builds can't OOM the VM. |
| `doctor.sh` | One-shot local-env diagnosis: infra + per-service `/health/ready` (manifest-driven) + recent errors from anything down. Run it first when something misbehaves. |
| `host-check.sh [--deep] [--logs] [target…]` | Full-stack sweep of a host: containers, service health, **RabbitMQ bus state** (stuck/competing queues), Postgres/RabbitMQ logs, observability, compose, host resources + the Colima OOM log. Runs over **local / SSH VPS / GCP** (Hostinger/EC2/GCE/Azure VMs via `ssh`); `--logs` pulls CloudWatch/GCP/Azure managed logs when configured. |
| `ci-logs.sh [branch]` | The latest CI run's failing jobs + their error lines (automates the `gh run view --log \| grep` triage). |
| `e2e-verify.sh [--live]` | Full regression: build, format, unit, integration, storefront, (live smoke). |
| `lib/hosts.sh` | Host targets for `host-check.sh` (name\|transport\|detail). Add your VPS / cloud VMs here. |
| `lib/services.sh` | **Single source of truth** for the service list (name:path:port). Edit here when adding a service. |
| `lib/preflight.sh` | `require_docker` / `require_docker_memory` guards used by the bring-up scripts. |

**Memory note:** the Docker VM (Colima) needs ~8+ GiB to build the images. Bare-run (`dev-up.sh`) only needs
Docker for Postgres+RabbitMQ, so it works on a small VM. To build images, bump it first:
`colima stop && colima start --cpu 4 --memory 12`.

**Maintaining these:** `scripts/lib/services.sh` is the single service source (most scripts derive from it). For when to update each script/doc as the codebase changes, see the **maintenance-triggers table** in `AGENTS.md` (Rules).
