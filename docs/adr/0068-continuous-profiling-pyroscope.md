# 0068 — Continuous profiling: Grafana Pyroscope + the Pyroscope .NET profiler, with span profiles

Status: Accepted — implemented
Area: Observability / build / dev infra
Extends: [0065](./0065-self-hosted-lgtm-telemetry-alerting-and-browser-rum.md) (self-hosted LGTM; owner Q7 approved Pyroscope), [0064](./0064-release-identity-on-telemetry.md) (service identity), [0067](./0067-glitchtip-error-inbox.md) (the dev infra set grows by what is measured)
Source: observability capability audit (`docs/reviews/observability-capability-audit.md`) §7 and roadmap **P2-1**;
owner decisions 2026-11-15 (Q7: Pyroscope approved) and 2026-11-19 (Pyroscope ON BY DEFAULT in the dev infra set; the
6 GB dev VM grows only by what is measured)

## Context

A slow trace says *which* span is slow, never *why*: CPU, allocations, lock contention or exceptions. There was no
profiler of any kind (audit §7). Pyroscope is Grafana's profiling backend — the fifth backend beside Loki, Tempo and
Mimir, read by the same Grafana, and able to link a span to the profile samples taken while it ran.

## Decision

### 1. Backend: Grafana Pyroscope 2.4.4

- **Dev:** `grafana/pyroscope:2.4.4` (latest patch of 2.4; it superseded 2.4.3 on 2026-11-20) in `docker-compose.yml`'s
  `observability` profile and in `DEV_BORROWED_SERVICES`, so it is part of the all-or-nothing infra set (dev-up,
  dev-down, doctor, host-check, e2e-verify, and CI's full set on pushes to `test`/`main`). Single binary, local
  filesystem (`pyroscope_data`), 7-day retention, `-metastore.min-ready-duration=0` (the 15 s hold exists for rolling
  updates of a cluster), 512M limit. The image is distroless, so readiness is `GET :4040/ready` from the host
  (`_infra_probe`, dev-up's gate). It profiles itself (`service_name=pyroscope`), which is what the dev store holds
  while every .NET process runs bare on macOS (below).
- **Production:** `deploy/helm/observability/values-pyroscope.yaml` for the upstream chart `grafana/pyroscope` 2.4.4,
  installed by `install-lgtm.sh` (`--template` renders it): monolithic v2 storage on the shared S3 (bucket
  `3commerce-pyroscope`, credentials from `lgtm-object-storage` or workload identity via `-config.expand-env`), a
  persistent volume for the metastore's raft state, multi-tenancy on (tenant `3commerce`, like LGTM), 14-day retention
  (= traces), no bundled Alloy/Agent/MinIO, no ingress.
- **Grafana:** datasource uid `pyroscope` (dev `lgtm.yml`, prod `values-grafana.yaml` with `X-Scope-OrgID`), and Tempo's
  `tracesToProfiles` (CPU profile type, `service.name` → `service_name`): a span's "Profiles for this span".

### 2. .NET: the Pyroscope .NET profiler (not OpenTelemetry profiling)

Weighed against the OpenTelemetry route — the eBPF profiler (`opentelemetry-ebpf-profiler`) feeding the collector's
profiles signal into Pyroscope's OTLP ingest:

| | Pyroscope .NET profiler (chosen) | OTel eBPF profiler |
|---|---|---|
| Maturity | GA, 1.6.1 (2026-11-09) | profiles signal still development/alpha |
| Deployment | a native library in each image, env-enabled | a privileged host-level DaemonSet (kernel BTF, `CAP_SYS_ADMIN`) |
| .NET frames | managed frames resolved by the CLR profiling API | JIT frames need perf maps (`DOTNET_PerfMapEnabled`) |
| Profile types | CPU, wall, allocation, lock, exception, live heap | CPU only |
| Span profiles | yes (`Pyroscope.OpenTelemetry`) | no |

So: the **native CLR profiler** from the `pyroscope-dotnet` 1.6.1 release, in every .NET image (13 services, gateway,
Notifications worker, Admin, Supplier Portal):

- a `profiler` build stage, **byte-identical in every Dockerfile**, downloads the glibc tarball for `TARGETARCH`
  (x86_64 or aarch64), **verifies its pinned SHA-256**, and strips debug info (`strip --strip-debug`: 185 MB → ~16 MB);
  the runtime stage copies `/pyroscope` right after its base layers, so the layer is shared by all images;
- the image sets only `CORECLR_PROFILER` (the profiler's CLSID), `CORECLR_PROFILER_PATH`, a log dir, and
  `PYROSCOPE_APPLICATION_NAME` = the OpenTelemetry `service.name` (so Tempo's link finds the profiles) — **the image
  never enables it**: nothing loads until a deployment sets `CORECLR_ENABLE_PROFILING=1`, `LD_PRELOAD` (the API
  wrapper) and `PYROSCOPE_PROFILING_ENABLED=true`;
- **Helm `profiling.enabled`** renders those (+ `PYROSCOPE_SERVER_ADDRESS`, `PYROSCOPE_TENANT_ID`,
  `PYROSCOPE_LABELS=deployment_environment_name:<env>`, profile types: CPU, allocation, lock, exception on; wall time off —
  it samples idle threads) on all 17 .NET workloads; **values-prod turns it on** (tenant `3commerce`). The containerized
  dev launch can turn it on from `deploy/.env.dev`.
- **Span profiles:** `Pyroscope` 1.6.1 + `Pyroscope.OpenTelemetry` 0.5.1 (`PyroscopeSpanProcessor`): root spans get
  `pyroscope.profile.id` and the profiler labels their CPU samples with it. `AddTelemetryCore` adds the processor **only
  where the native profiler is loaded** (`Profiling.IsEnabled`: Linux, `CORECLR_ENABLE_PROFILING=1`,
  `PYROSCOPE_PROFILING_ENABLED`) — it calls into the profiler, so it must never run where the profiler is absent.

### 3. The profiler is Linux-only: bare-run on macOS is never profiled

The native profiler ships for Linux (x86_64/aarch64, glibc and musl) and, in preview, Windows x64 — **not macOS**. The
default dev mode runs every .NET process bare on the Mac (ADR-0009), so dev profiles come only from Linux containers
(the containerized launch, kind, a Linux dev host with the env above). This is documented where developers look
(getting-started, the runbook, doctor's output) instead of being papered over: `Profiling.IsEnabled` is false off Linux.

### 4. Dependencies (each justified)

| Dependency | Why | Licence |
|---|---|---|
| `grafana/pyroscope:2.4.4` image (dev) + chart `pyroscope` 2.4.4 (prod) | The profiling backend (owner Q7); Grafana-native, one binary | AGPL-3.0 (backend, like Loki/Tempo/Mimir) |
| `pyroscope-dotnet` 1.6.1 native profiler (in the images) | The only GA .NET continuous profiler for Pyroscope; CLR profiling API, all profile types | Apache-2.0 |
| NuGet `Pyroscope` 1.6.1 + `Pyroscope.OpenTelemetry` 0.5.1 | Span profiles (the Tempo → profile link); 0.5.1 targets OpenTelemetry 1.16.0, our version | Apache-2.0 |

## Rejected

- **OpenTelemetry eBPF profiling** (table above): alpha signal, privileged DaemonSet, CPU only, no span link — revisit
  when the profiles signal is stable.
- **`dotnet-monitor` on demand only**: no history; answers "what is it doing now", not "why was it slow at 02:10".
- **Shipping the unstripped profiler**: +185 MB per image (and kind-deploy has run out of disk before).
- **Enabling the profiler in the image**: every container (CI, compose-smoke, kind) would load it; it is a deployment
  decision.
- **The Pyroscope Node SDK for the storefront**: not in scope (audit: optional), a new dependency for SSR only.

## Memory (6 GB dev VM)

Measured 2026-11-25 on the whole seeded stack from fully down (15 infra containers incl. GlitchTip and Pyroscope, 13
services, gateway, worker, 3 frontends, 8 min of traffic; 107 samples): **peak VM used 3903 MB** of 5911 (p99 3853,
p50 3671); Pyroscope ~72 MiB steady (idle ~40). The bare-run .NET processes load no profiler (Linux-only), so it adds
nothing there. Against ADR-0067's start-up peak (4479 MB, Loki WAL replay) the worst case is 4479 + 72 + 1 GiB margin
= 5575 MB: **fits in the 6 GB VM**, ~336 MB spare.

## Consequences

- A slow span in Tempo opens its own CPU profile in production; flame graphs per service, release environment and
  profile type sit next to RED metrics, logs and traces in one Grafana.
- Every .NET image is ~16 MB larger (one shared layer) and the build downloads a pinned, checksum-verified tarball.
  Bumping the profiler = one version + two checksums, the same in every Dockerfile (`ProfilingTests` fails otherwise).
- Production pays a few percent CPU for the enabled types; turn types off per environment in `profiling.types`.
- Pinned by `ProfilingTests` (unit lane, e2e-verify **A3zr**) and live **L20d** (a CPU profile pushed to Pyroscope's `/ingest` is
  queried back by service and frames on any platform; .NET service profiles are reported where the profiler runs and
  skipped, with the reason, on bare-run; Grafana has the datasource and Tempo's traces-to-profiles link). The .NET
  profiler itself was verified in a Linux container of the catalog image: managed frames in Pyroscope, and Tempo
  spans carrying `pyroscope.profile.id`. Runbook: `docs/runbooks/observability.md`, "Profiling (Pyroscope)".
