# ADR-0026: Service accounts + installable .NET global-tool CLI

- **Status:** Accepted
- **Date:** 2026-06-18
- **Source:** Multi-tenant platform expansion plan (phase 1 foundation).
- **Builds on:** [ADR-0025](0025-pdp-pep-dynamic-rbac.md) (PDP/PEP, dynamic RBAC), [ADR-0011](0011-yarp-gateway-single-origin.md) (gateway as single origin).

## Context

Operators want to automate the platform (CI, scripts, bulk ops) and run admin tasks from a
terminal, not only the Blazor UI. Automation identities must be first-class and tightly
scoped, and the CLI must go through the same authorization as everything else.

## Decision

- **Service accounts** are non-human principals (ADR-0023) with **hash-only** credentials
  (client-id + a secret we store only as a hash; the secret is shown once at creation). They
  authenticate via client-credentials to obtain a short-lived token, are **narrowly scoped to
  explicit permissions** (never a broad mirror), are revocable/rotatable, and **cannot approve**
  maker-checker changes (ADR-0025). Every service-account action is audited.
- The **CLI** ships as an installable **.NET global tool** (`src/Cli/3commerce.Cli`). It talks
  **only to the Gateway** (ADR-0011) — never to a service or DB directly, never seeing the
  internal claims header. It supports human login and service-account login, named config
  profiles, explicit `--tenant`/`--storefront` scope selection, output formats
  (table/json/yaml/csv), and discovers available commands/permissions from OpenAPI + the
  permission registry.
- **Human MasterGlobal** gets a broad command surface (mirror of admin capabilities); everyone
  else — including all service accounts — is gated by their PDP-resolved permissions.
- Mutating commands that may retry carry an **idempotency key**, and destructive/sensitive
  commands require an explicit **confirmation + reason** flag (which flows into the audit).

## Alternatives considered

- **Long-lived API keys with broad scope** — rejected: a leaked broad key is a tenant breach;
  short-lived tokens + narrow scopes + rotation limit blast radius.
- **CLI calling services directly** — rejected: bypasses the Gateway's domain/context/rate
  limits and the single-origin rule (ADR-0011); the CLI is just another Gateway client.
- **A bespoke daemon/agent instead of a global tool** — rejected for v1: a `dotnet tool`
  installs in one command and needs no runtime service.

## Consequences

- Service-account secrets are unrecoverable by design; rotation issues a new secret.
- The CLI inherits all authorization/audit guarantees because it is a Gateway client with no
  privileged backdoor.
- Command/permission discovery depends on OpenAPI + registry being kept current (an existing
  repo rule for endpoints).
