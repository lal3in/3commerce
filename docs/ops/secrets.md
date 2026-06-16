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
