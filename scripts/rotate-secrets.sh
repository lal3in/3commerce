#!/usr/bin/env bash
#
# rotate-secrets.sh — generate per-environment secrets for 3commerce (BL-11).
#
# The repo ships DEV-ONLY secrets (the ES256 internal-claims keypair, the seed-admin
# password) so local dev works with zero setup. Every non-Development environment MUST
# supply its own — the Gateway and services refuse to boot with the committed dev key
# (see docs/ops/secrets.md). This prints ready-to-paste environment variables; it does
# NOT write any files or touch a secret store.
#
# Usage: scripts/rotate-secrets.sh [environment-name]
#
set -euo pipefail

ENV_NAME="${1:-production}"

command -v openssl >/dev/null 2>&1 || { echo "openssl is required" >&2; exit 1; }

# ES256 (P-256) keypair, PKCS#8 private + SubjectPublicKeyInfo public, PEM.
PRIVATE_PEM="$(openssl ecparam -name prime256v1 -genkey -noout 2>/dev/null \
  | openssl pkcs8 -topk8 -nocrypt 2>/dev/null)"
PUBLIC_PEM="$(printf '%s' "$PRIVATE_PEM" | openssl ec -pubout 2>/dev/null)"

# A strong random admin password (URL-safe).
ADMIN_PASSWORD="$(openssl rand -base64 24 | tr '+/' '-_' | tr -d '=')"

# Collapse PEMs to single-line \n form for env vars / JSON.
to_oneline() { awk 'BEGIN{ORS="\\n"} {print}' <<<"$1" | sed 's/\\n$//'; }

cat <<EOF
# ---------------------------------------------------------------------------
# 3commerce secrets for environment: ${ENV_NAME}
# Generated $(date -u +%Y-%m-%dT%H:%M:%SZ). Store these in your secret manager;
# do NOT commit them. Inject as environment variables (double-underscore = nesting).
# ---------------------------------------------------------------------------

# Gateway only — holds the private key, mints internal-claims JWTs.
InternalAuth__PrivateKey="$(to_oneline "$PRIVATE_PEM")"

# Every service — verifies internal-claims JWTs with the public key.
InternalAuth__PublicKey="$(to_oneline "$PUBLIC_PEM")"

# Identity — initial admin (the in-app DevAdminSeeder only runs in Development;
# for real environments create the admin via your provisioning flow with this value).
Identity__SeedAdmin__Password="${ADMIN_PASSWORD}"

# Reminder: also rotate the Postgres / RabbitMQ credentials in your ConnectionStrings.
EOF
