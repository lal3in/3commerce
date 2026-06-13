# ADR-0013: Guest checkout + optional accounts; MFA/social deferred

- **Status:** Accepted
- **Date:** 2026-06-12
- **Source:** PRD design interview (decision log #13, `docs/prd/3commerce/15-appendix.md`)

## Context

Forced registration is one of the best-documented causes of cart abandonment (~25% in industry surveys). Meanwhile every auth feature added to a hand-built Identity service is new attack surface.

## Decision

- **Guest checkout** is mandatory: email + shipping address only; anonymous cookie-keyed cart.
- Post-purchase conversion: "set a password to track your order" attaches guest orders to the new account.
- v1 account features: email verification, password reset, profile/addresses, order history.
- **Deferred:** MFA (TOTP), social login, passkeys — the custom auth gets hardened before it gets features.

## Alternatives considered

- **Accounts required to purchase** — rejected: pays for model simplicity with lost sales.
- **Full auth suite in v1** — rejected: weeks of work and attack surface on unhardened Identity; OAuth also outsources the learning.
- **Guest-only, no accounts** — rejected: deletes the core "users manage their details and purchases" requirement.

## Consequences

- Ordering must support carts/orders with no user ID, plus a merge path (guest → account, anonymous cart → user cart on login).
- MFA for **admin** accounts should be revisited before live launch (PRD §13).
