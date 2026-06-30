# ADR-0037: Kong API-management gate

Status: Accepted
Date: 2026-06-28
Area: Edge / API management

## Context

YARP is already the single public origin for 3commerce. It owns the platform-specific edge behavior:

- session-cookie validation;
- tenant/domain resolution;
- internal-claims minting;
- service route prefixing;
- internal health-route blocking;
- rate limiting and header hygiene.

Kong is valuable when API-management itself becomes a product capability: partner APIs, API plans, API keys, quotas, developer portals, or a gateway fleet independent of the app platform. None of those requirements are active launch gates today.

## Decision

Do not add Kong now. YARP remains the gateway of record.

Revisit Kong only if at least one of these gates is met:

1. Public partner/developer API program.
2. API keys, plans, quotas, or metering as product features.
3. Developer portal requirement.
4. Multi-consumer API monetization or analytics requirement.
5. Need for a centralized gateway fleet/control plane separate from .NET application customization.

If the gate triggers, compare these options in a new ADR before implementation:

- Kong in front of YARP for API-management concerns while YARP keeps session/internal-claims customization.
- Kong replacing YARP only if custom .NET edge behavior is no longer needed or is implemented safely elsewhere.
- YARP-only with additional custom policy if requirements remain platform-specific and modest.

## Consequences

Positive:

- Avoids a second gateway/control plane before there is a product requirement.
- Keeps edge auth/session/claims logic in the current tested .NET gateway.
- Reduces local/CI/deploy infrastructure complexity.

Negative:

- Public API-management features remain deferred.
- A future partner API program will require a dedicated gateway/API-management design slice.

## Validation

No infrastructure is added by this ADR. Existing validation remains:

```bash
helm template 3commerce deploy/helm/3commerce -f deploy/helm/3commerce/values-prod.yaml >/tmp/3commerce-prod-rendered.yaml
scripts/e2e-verify.sh
```
