# ADR-0031: YARP production hardening

- **Status:** Accepted
- **Date:** 2026-06-28
- **Source:** Production platform architecture roadmap (`.ai-shared/plans/production-platform-architecture-roadmap.md`, `pplat_1`)

## Context

ADR-0011 made YARP the single public origin because the edge must run custom session validation, tenant/storefront domain resolution, internal-claims minting, rate limiting, and header hygiene. The initial configuration was intentionally simple: one destination per service cluster and no explicit destination health policy.

As the platform moves toward production and horizontal app replicas, the gateway needs a documented operating posture that supports multiple service destinations, actively removes unhealthy destinations from routing, and avoids unsafe edge retries around money-mutating operations.

## Decision

Keep **YARP** as the API gateway and reverse proxy. Harden the YARP configuration with these conventions:

1. Every service cluster declares `LoadBalancingPolicy = PowerOfTwoChoices` so additional destinations can be added by configuration without code changes.
2. Every service cluster uses active destination health checks against `/health/ready`.
3. Every service cluster sets a bounded `HttpRequest.ActivityTimeout` of 30 seconds.
4. The gateway does **not** configure blanket proxy retries. Application/service retries and MassTransit consumer retries remain the default reliability mechanism.
5. Money-mutating routes such as checkout authorization, refunds, payment-account lifecycle changes, supplier payouts, Xero postings, and provider webhooks must not be retried at the gateway unless the route is explicitly proven idempotent and covered by idempotency tests.
6. Production can run multiple Gateway replicas behind an infrastructure/cloud/Kubernetes load balancer, while YARP routes to service replicas through configured destinations or Kubernetes Services.

## Alternatives considered

- **Kong now** — rejected for this stage. The current product needs custom .NET session/tenant/internal-claims behavior more than public API-management features. Kong remains a future option if partner API plans, developer portals, API keys/quotas, or API monetization become product requirements.
- **Infra-only ingress/nginx/Traefik** — rejected as the only gateway for the same reason as ADR-0011: custom auth/tenant logic belongs at the application edge. An ingress or cloud load balancer may still sit in front of YARP.
- **Gateway-level retries everywhere** — rejected. Retrying unsafe mutating requests can duplicate financial operations unless every route has explicit idempotency semantics.

## Consequences

- Operators can scale service replicas without changing gateway code.
- YARP can stop routing to destinations that fail readiness checks.
- Gateway timeouts are explicit and easier to tune per route/cluster later.
- Retry behavior remains conservative; business workflows continue to rely on service-owned idempotency, EF outbox/inbox, and MassTransit retry policies.
- Kong adoption is decision-gated by product/API-management requirements, not by ordinary reverse-proxy hardening.
