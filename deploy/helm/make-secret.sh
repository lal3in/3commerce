#!/usr/bin/env bash
#
# make-secret.sh — emit a Kubernetes Secret with a fresh ES256 keypair for a prod-mode
# Helm deploy (satisfies the BL-11 DevSecretGuard). base64 `data` preserves PEM newlines.
#
#   deploy/helm/make-secret.sh | kubectl apply -f -
#   helm install 3commerce deploy/helm/3commerce -f deploy/helm/3commerce/values-prod.yaml
#
set -euo pipefail
NAME="${1:-3commerce-secrets}"
command -v openssl >/dev/null 2>&1 || { echo "openssl required" >&2; exit 1; }

PRIV="$(openssl ecparam -name prime256v1 -genkey -noout 2>/dev/null | openssl pkcs8 -topk8 -nocrypt 2>/dev/null)"
PUB="$(printf '%s' "$PRIV" | openssl ec -pubout 2>/dev/null)"
ADMIN="$(openssl rand -base64 24 | tr '+/' '-_' | tr -d '=')"
b64() { printf '%s' "$1" | base64 | tr -d '\n'; }

cat <<EOF
apiVersion: v1
kind: Secret
metadata:
  name: ${NAME}
type: Opaque
data:
  InternalAuth__PrivateKey: $(b64 "$PRIV")
  InternalAuth__PublicKey: $(b64 "$PUB")
  Identity__SeedAdmin__Password: $(b64 "$ADMIN")
EOF

echo "# admin password (store it): ${ADMIN}" >&2
