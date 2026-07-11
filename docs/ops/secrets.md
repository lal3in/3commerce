# Secrets & rotation (BL-11)

3commerce ships **development-only** secrets in the repo so a fresh clone runs with zero
setup. Every non-Development environment must replace them. The code enforces this: the
Gateway (when minting) and every service (when configuring auth) **refuse to boot outside
Development if they are still configured with the committed dev ES256 key**
(`DevSecretGuard` / the inline check in `InternalClaimsMinter`).

## The secrets

| Secret | Config key | Held by | Committed dev value |
|--------|-----------|---------|---------------------|
| Internal-claims **private** key (ES256) | `InternalAuth:PrivateKey` | Gateway only | `src/Gateway/appsettings.json` |
| Internal-claims **public** key (ES256) | `InternalAuth:PublicKey` | every service | `src/Services/*/appsettings.json` |
| Seed-admin password | `Identity:SeedAdmin:Password` | Identity (dev seeder) | `src/Services/Identity/Api/appsettings.json` |
| Postgres / RabbitMQ credentials | `ConnectionStrings:*` | each service | `appsettings.json` |
| Stripe API key | `Stripe:SecretKey` | Payments | _empty_ (keyless dev runs LocalMock) |
| Stripe webhook signing secret (config fallback) | `Stripe:WebhookSecret` | Payments | _unset_ |
| Polar access token | `Polar:AccessToken` | Payments | _unset_ |
| PayPal REST credentials | `PayPal:ClientId` / `PayPal:Secret` | Payments | _unset_ |
| Afterpay merchant credentials | `Afterpay:MerchantId` / `Afterpay:SecretKey` | Payments | _unset_ |
| Publishing preview-link signing secret | `Publishing:PreviewSecret` | Marketing | code fallback `dev-preview-secret` |
| RabbitMQ management API credentials | `MessageBus:ManagementUser` / `MessageBus:ManagementPassword` | Admin (Mission Control bus stats) | code fallback `guest`/`guest` |
| Grafana admin password | `GRAFANA_ADMIN_PASSWORD` (compose env) | observability profile | fallback `admin` |

Every `_unset_` / fallback row is **env-var injected per deployment** (`Stripe__SecretKey`,
`PayPal__ClientId`, `Publishing__PreviewSecret`, `MessageBus__ManagementPassword`, …) and
never committed — the same rule as the rotated keys below.

The private/public keys are a single keypair: the Gateway signs the short-lived
`X-Internal-Claims` JWT with the private key; services verify it with the public key
(ADR-0012). They must be rotated **together**.

> The committed dev keypair is public, so anyone with the repo could mint admin tokens.
> That is acceptable for local dev and unacceptable anywhere else.

## Rotating

```bash
scripts/rotate-secrets.sh production   # prints fresh keys + admin password as env vars
```

The script generates a new P-256 keypair and a random admin password and prints
ready-to-paste environment variables (it writes nothing). Then:

1. Store the values in your secret manager (never commit them).
2. Inject them as environment variables — ASP.NET maps `__` to config nesting, e.g.
   `InternalAuth__PrivateKey`, `InternalAuth__PublicKey`, `Identity__SeedAdmin__Password`.
   Env vars override `appsettings.json`, so the committed dev values are shadowed.
3. Give the **private** key only to the Gateway; give the **public** key to every service.
4. Rotate the Postgres/RabbitMQ credentials in the connection strings too.
5. Set `ASPNETCORE_ENVIRONMENT` to a non-Development value (`Production`, `Staging`, …).

## Verifying the gate

With a non-Development environment and the dev key still in place, startup fails fast:

> Refusing to start with the committed development InternalAuth key outside Development.
> Rotate per-environment secrets (docs/ops/secrets.md) …

Covered by `DevSecretGuardTests` (Identity unit tests).

## Key rotation on a running system

Because verification uses only the public key, rotate with a brief overlap:

1. Deploy services trusting the **new** public key (add alongside the old if you support
   multiple — v1 holds a single key, so schedule a short maintenance window instead).
2. Switch the Gateway to the new private key.
3. Old internal-claims JWTs expire within 5 minutes (the mint lifetime), so in-flight
   requests drain quickly.

## Payment modes — expected environment per mode (ADR-0039)

`Payments:Mode` is the host-level ceiling (`LocalMock` | `Sandbox` | `Production`); the
resolved runtime mode fails closed. Two boot guards enforce the posture, mirroring
`DevSecretGuard`:

- **`PaymentModeGuard`** — a non-Development host refuses to boot with
  `Payments:Mode=LocalMock` or `Payments:AllowMockEmail=true`. An **absent**
  `Payments:Mode` outside Development defaults to Production, so no committed container or
  Helm config can enable the mock/email path by omission (`appsettings.Container.json`
  carries no `Payments` section; the Helm chart injects none).
- **`PaymentSecretResolver`** — Sandbox/Production hosts refuse to run without
  mode-appropriate credentials, and refuse a credential whose prefix contradicts the mode
  (a live Stripe key under Sandbox, a test key under Production).

| Mode | `ASPNETCORE_ENVIRONMENT` | Required env | Notes |
|------|--------------------------|--------------|-------|
| LocalMock | `Development` only | none | Default in Development (`appsettings.Development.json`); mock adapter, offline, TEST-ONLY payload email to `Payments:MockEmailTo`. |
| Sandbox | any | `Payments__Mode=Sandbox` + **test** credentials (`Stripe__SecretKey=sk_test_…` / `rk_test_…`, or `Polar__AccessToken`, `PayPal__ClientId`+`PayPal__Secret`, `Afterpay__MerchantId`+`Afterpay__SecretKey`) | Only Test-mode accounts resolve; sandbox base URLs are hardcoded per provider (`{Provider}:BaseUrl` overrides). |
| Production | non-Development | `Payments__Mode=Production` (or unset) + **live** credentials (`sk_live_…` / `rk_live_…`) | Only Live-mode accounts resolve; the mock adapter is unreachable. |

## Webhook signing secrets — registry vs config fallback

Webhook verification (`/webhooks/{provider}`) resolves secrets from the **database
registry** (`payments.WebhookSecrets`, managed via the Payments admin endpoints), newest
first; when a provider has **zero active rows** it falls back to the legacy config key
(`Stripe:WebhookSecret` — Stripe only). Dev therefore works with zero rows and no secret.

Rotation (zero-downtime, both secrets verify during overlap):

1. Create the new secret row (admin endpoint) while the old one is still active.
2. Cut the provider's webhook endpoint over to the new signing secret.
3. Deactivate the old row. Rows are masked in list output; raw values are never re-shown.
