# 9. Security & Configuration

Security is a stated top-level requirement. The custom-auth decision was made knowingly; this section records the conditions attached to it.

## Authentication (customer & admin)

- **Edge:** opaque 256-bit random session token in a `Secure; HttpOnly; SameSite=Lax` cookie. Immune to XSS token theft; trivially revocable (server-side session row). No JWTs in the browser.
- **Gateway:** validates the cookie against Identity's introspection endpoint, caching positive results ≤ 60 s (bounded revocation lag). Invalid/absent → 401 before any service is reached.
- **Internal:** gateway mints a short-lived (≤ 5 min) signed JWT of claims (`sub`, `role`, `session_id`) forwarded as a header; services verify signature only — no per-request Identity calls. Signing keys rotated; services hold only the public key.
- **Password storage:** Argon2id from a vetted library, per-user salt, parameters reviewed against current OWASP guidance. **Hand-rolled crypto is prohibited.** All of Identity sits behind `IAuthService` so it can be replaced (e.g. by ASP.NET Identity or an IdP) if an audit fails it — this was the explicit price of building auth custom.
- **Lockout/abuse:** progressive delays per account, per-IP rate limits at the gateway, generic error messages (no user-enumeration), single-use expiring tokens for verification/reset, all sessions revoked on password change/reset.

## Authorization

- Roles: `customer`, `admin` — claims in the internal JWT.
- Admin (Blazor) requires `admin` role **plus** network controls: separate subdomain, IP allowlist.
- Services enforce resource ownership (a customer can only read their own orders/tickets) — never trust the gateway alone.

## Payments security

- Card data **never touches our servers**: Stripe Payment Element tokenizes client-side → PCI **SAQ-A** scope. Raw card handling was explicitly ruled out.
- Webhooks: Stripe signature verification, event-ID dedup inbox, idempotent handlers.
- Refunds execute only through the saga (Support approval or admin action) — no direct Stripe console workflow, so the ledger can never silently diverge.
- Ledger is append-only; corrections are reversing entries, never updates.

## Network posture

- Only the gateway (and storefront/admin hosts) are publicly reachable; the six services and RabbitMQ bind to localhost/private network.
- Internal-claims header is stripped from all inbound public requests at the gateway (header-injection defense).
- TLS termination at the edge in any deployed environment.

## Configuration management

| Concern | Mechanism |
|---|---|
| Local secrets | .NET user-secrets per service; `.envrc` (direnv) for shared env vars; `.env*` git-ignored |
| Per-service config | `appsettings.json` (non-secret) + environment variables (secret/env-specific) |
| Currency | Single configurable value (e.g. `STORE_CURRENCY=EUR`) — **not hardcoded**, jurisdiction unknown until registration |
| Tax | `ITaxStrategy` selected by config; v1 flat-rate placeholder |
| Keys requiring config | Stripe (test) secret + webhook secret; Xero OAuth client; email provider key; internal JWT signing key; per-service Postgres connection strings; RabbitMQ credentials |

## Security scope

- ✅ In scope (v1): everything above, plus dependency scanning in CI and a pre-launch self-audit of Identity against OWASP ASVS.
- ❌ Out of scope (v1): MFA, social login, passkeys, WAF/DDoS tooling, GDPR self-service deletion/export (manual on request), penetration test (pre-launch task, post-registration).

## Deployment considerations (forward-looking)

- Stripe live keys, Xero production org, and a privacy policy/imprint are **launch gates**, all blocked on company registration.
- When Kubernetes arrives: secrets via sealed-secrets/external-secrets, network policies replicating the localhost-only posture, per-service service accounts.
