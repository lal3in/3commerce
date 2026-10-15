# 0064 — Release identity on telemetry: the commit SHA stamps every span, metric, log and image

Status: Accepted — implemented
Area: Observability / build / deployment
Extends: [0058](./0058-branching-and-release-promotion.md) (release promotion), [0021](./0021-containerized-launch.md) (containerized launch), [0009](./0009-local-dev-bare-dotnet-run.md) (bare-run dev)
Source: observability capability audit (`docs/reviews/observability-capability-audit.md`) §8/§9, roadmap item **P0-1** — owner-approved 2026-10-15

## Context

The OTel resource of every .NET process was `AddService(name)` only: no `service.version`, no commit, no
environment. Dockerfiles took no build args and set no labels. When an error rate jumps, nothing ties it to the
promotion or PR that caused it, and once test and prod share a telemetry backend their signals would be
indistinguishable. ADR-0058 makes release tracking unusually simple: `develop → test → main` are fast-forwards of
one tested commit, so **the release identity is the commit SHA**. The logs pipeline also built its own resource,
so its auto-generated `service.instance.id` differed from the traces'/metrics'.

## Decision

1. **Resource attributes** (OTel semantic conventions, current names): every resource built by
   `AddServiceTelemetry` — and the gateway's — carries
   - `service.version` = `<Version>+<sha7>` (`-dirty` appended for an uncommitted bare-run build);
   - `vcs.ref.head.revision` = the full commit SHA (omitted when the build carries none — never a crash);
   - `deployment.environment.name` = `dev` / `test` / `prod` / `ci`;
   - `service.instance.id` = one UUIDv7 per process, shared by traces, metrics and logs.
   `ReleaseIdentity` (`BuildingBlocks/Infrastructure/Observability`) resolves it once per host
   (`builder.AddReleaseIdentity(name)`), registers it as a singleton and logs one
   `Release identity: service.name=… service.version=… vcs.ref.head.revision=… deployment.environment.name=…`
   line at startup.
2. **Source of truth = build metadata.** The assembly `InformationalVersion` is `$(Version)+$(SourceRevisionId)`.
   `Directory.Build.props` fills an empty `SourceRevisionId` from the `GIT_SHA` environment variable; otherwise the
   SDK's bundled SourceLink fills it from `.git` (it only sets an empty one). So:
   - a plain local `dotnet build` is stamped with HEAD automatically;
   - bare-run (`dev-up.sh`, `e2e-verify.sh`'s own boot) builds with `GIT_SHA=$(release_revision)` — HEAD plus
     `-dirty` when tracked files differ (`scripts/lib/release.sh`);
   - container builds have no `.git` (`.dockerignore`), so every Dockerfile declares `ARG GIT_SHA` / `ARG VERSION`
     (GIT_SHA reaches MSBuild as an env var; VERSION → `-p:Version` only when set) and ends with OCI labels
     `org.opencontainers.image.revision`, `.version`, `.source`. `scripts/build-images.sh` (now also
     `build-images.sh <name…>`), `scripts/launch.sh` (compose `x-release-args`) and CI (`docker` matrix,
     `kind-deploy`) pass them.
   `Version` stays the product number (`VersionPrefix` 1.0.0 in `Directory.Build.props`); it is not the release
   identity and must not contain `+`.
3. **Environment from configuration.** The host environment maps Development → `dev`, Staging/Test → `test`,
   Production → `prod`, CI → `ci` (anything else lower-cased). An explicit `OTEL_RESOURCE_ATTRIBUTES` entry for
   any of the four keys **always wins** (read from `IConfiguration`, percent-decoded per the OTel spec; our
   attributes are added after the SDK's env detector, so the explicit value is re-applied rather than
   overwritten). Explicit settings: `deploy/.env.dev` (`dev`), `launch.sh --env prod` (`prod`), Helm
   `deploymentEnvironment` (`values-dev` dev, `values-prod` prod, `--set deploymentEnvironment=test` for the
   release candidate — it runs Production config but must report `test`), CI workflow env (`ci`).
4. **Visibility.** The collector's `resource_to_telemetry_conversion` already turns resource attributes into
   metric labels, so every RED series carries `service_version` / `vcs_ref_head_revision` /
   `deployment_environment_name`; the Services (RED) dashboard gains a *Running releases by service* table.
   No `/info` endpoint: `docs/reference/api.md` has no such convention and health endpoints are internal-only.

## Scope left out

- Admin and Supplier Portal have no OTel pipeline (audit P1-4); their footer is not changed (a per-commit footer
  would make every screenshot run differ). Their assemblies are stamped by the same props and their images carry
  the labels, so P1-4 gets the identity for free via `AddReleaseIdentity`.
- Storefront: OCI labels only; exposing the SHA to the browser (`NEXT_PUBLIC_…`) belongs with the Faro item
  (P1-2), which is the first consumer.
- Grafana release annotations / `release/*` tags from `promote.sh` stay roadmap item P2-2.
- No new packages.

## Consequences

- Any signal can be filtered or grouped by release and environment; "which release broke it" is one query.
- Metric cardinality grows by one series set per release per process — bounded, and old releases age out.
- Each new commit changes `SourceRevisionId`, so every project's generated AssemblyInfo changes and recompiles
  (already true since the SDK started stamping SourceLink SHAs). A dirty bare-run build differs from a plain
  `dotnet build` of the same tree, so switching between them recompiles once.
- Pinned by `ReleaseIdentityTests` (unit lane, e2e-verify **A3k**) and live by **L2b** (the catalog service's
  startup line carries the commit e2e-verify built).
