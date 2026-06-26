# scripts/

| Script | What it does |
|---|---|
| `dev-up.sh [--with-frontends] [--seed]` | **Bare-run** local env (ADR-0009): Postgres+RabbitMQ in Docker, infra → migrate → all services → (frontends) as host processes. The light default — never builds images, so it can't OOM the Docker VM. |
| `dev-down.sh` | Stops everything `dev-up.sh` started. |
| `run-all.sh [start\|stop]` | Starts/stops just the gateway + services + workers (host `dotnet run`). Used by `dev-up.sh`. |
| `build-images.sh` | Builds **all** container images with **bounded concurrency** (`PARALLEL=2`) + a Docker-memory preflight, so 13 parallel .NET builds can't OOM the VM. |
| `e2e-verify.sh [--live]` | Full regression: build, format, unit, integration, storefront, (live smoke). |
| `lib/services.sh` | **Single source of truth** for the service list (name:path:port). Edit here when adding a service. |
| `lib/preflight.sh` | `require_docker` / `require_docker_memory` guards used by the bring-up scripts. |

**Memory note:** the Docker VM (Colima) needs ~8+ GiB to build the images. Bare-run (`dev-up.sh`) only needs
Docker for Postgres+RabbitMQ, so it works on a small VM. To build images, bump it first:
`colima stop && colima start --cpu 4 --memory 12`.
