# ADR-0012: Custom auth — opaque cookie at edge, signed claims internally

- **Status:** Accepted
- **Date:** 2026-06-12
- **Source:** PRD design interview (decision log #12, `docs/prd/3commerce/15-appendix.md`)

## Context

Building auth in-house was an explicit owner choice (learning value), made against the default recommendation to lean on ASP.NET Core Identity. Conditions were attached as the price of that choice.

## Decision

- **Browser:** opaque 256-bit random session token in a `Secure; HttpOnly; SameSite=Lax` cookie — XSS-resistant, trivially revocable (server-side session row). No JWTs in the browser.
- **Gateway:** validates the cookie against Identity's introspection endpoint, caching positives ≤ 60 s (bounded revocation lag), then mints a **short-lived (≤ 5 min) signed JWT of claims** forwarded to services, which verify by signature only.
- **Conditions (non-negotiable):** password hashing is **Argon2id from a vetted library** — hand-rolled crypto is prohibited; all of Identity sits behind `IAuthService` so a failed audit can swap in ASP.NET Identity or an IdP; OWASP ASVS L1 self-audit + external review before live traffic.

## Alternatives considered

- **JWT access/refresh tokens in the browser** — rejected: XSS theft surface, revocation pain.
- **Per-request Identity introspection from every service** — rejected: Identity becomes a synchronous bottleneck in every flow.
- **Trusted plain header on a private network** — rejected: one SSRF away from total impersonation.

## Consequences

- Both stateful (edge) and stateless (internal) auth models are exercised — the learning goal.
- Logout/password-reset is effective everywhere within ≤ 60 s (NFR-8).
- Tracked as PRD risk R-2 with the `IAuthService` swap as contingency.
