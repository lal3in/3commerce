#!/usr/bin/env bash
# Self-hosted LGTM install set for production telemetry (ADR-0065 Q1): the upstream Loki / Tempo / Mimir charts on
# S3-compatible object storage plus Grafana with the repo's dashboards and alert rules — installed and operated by us
# into one namespace. The 3commerce chart's collector (values-prod.yaml: observability.backend=external) exports here.
#
#   deploy/helm/observability/install-lgtm.sh            install / upgrade (needs kubectl + helm 3.8+ and a cluster)
#   deploy/helm/observability/install-lgtm.sh --template render every chart to $OUT (default .run/lgtm-rendered) —
#                                                        no cluster needed; what the CI-less validation runs
#
# Environment:
#   NAMESPACE      target namespace (default observability — the collector's external.* defaults assume it)
#   S3_ENDPOINT    object storage host[:port], no scheme (e.g. s3.eu-central-1.amazonaws.com, minio.storage:9000)
#   S3_REGION      bucket region (e.g. eu-central-1)
# Buckets (create them first; names in values-*.yaml): 3commerce-loki-{chunks,ruler,admin}, 3commerce-tempo-traces,
# 3commerce-mimir-{blocks,ruler,alertmanager}. Give them NO lifecycle rule shorter than the retention: the backends
# delete expired data themselves (Loki 30 d / security 12 months, Tempo 14 d, Mimir 13 months).
#
# Secrets in $NAMESPACE (never committed — create them first, e.g. `kubectl create secret generic … --from-literal`):
#   lgtm-object-storage  AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY   (optional with IRSA / workload identity)
#   grafana-admin        admin-user, admin-password
#   grafana-alerting     ALERT_EMAIL_TO, GF_SMTP_HOST, GF_SMTP_USER, GF_SMTP_PASSWORD, GF_SMTP_FROM_ADDRESS (Q8)
# Runbook: docs/runbooks/observability.md.
set -euo pipefail
cd "$(dirname "$0")/../../.."

# Pinned upstream charts (2026-11). grafana/{grafana,tempo} moved to the grafana-community repo; mimir-distributed
# stays in grafana's. Bump deliberately and re-run --template.
COMMUNITY=https://grafana-community.github.io/helm-charts
GRAFANA=https://grafana.github.io/helm-charts
LOKI_CHART=21.3.1        # Loki 3.7.10
TEMPO_CHART=3.2.1        # Tempo 3.1.1 (single binary)
MIMIR_CHART=6.2.2        # Mimir 3.2.3
GRAFANA_CHART=14.5.0     # Grafana 13.3.1

NAMESPACE="${NAMESPACE:-observability}"
DIR=deploy/helm/observability
PROV=deploy/observability/grafana/provisioning
MODE=install
[[ "${1:-}" == --template ]] && MODE=template
OUT="${OUT:-.run/lgtm-rendered}"

if [[ "$MODE" == install ]]; then
  : "${S3_ENDPOINT:?set S3_ENDPOINT (object storage host[:port])}"
  : "${S3_REGION:?set S3_REGION}"
  for s in grafana-admin grafana-alerting; do
    kubectl -n "$NAMESPACE" get secret "$s" >/dev/null 2>&1 \
      || { echo "missing Secret $NAMESPACE/$s — see the header of $0" >&2; exit 1; }
  done
else
  S3_ENDPOINT="${S3_ENDPOINT:-s3.example.invalid}"; S3_REGION="${S3_REGION:-eu-central-1}"
  mkdir -p "$OUT"
fi

run() { # run <release> <repo> <chart> <version> <values> [--set …]
  local rel="$1" repo="$2" chart="$3" ver="$4" values="$5"; shift 5
  if [[ "$MODE" == template ]]; then
    helm template "$rel" "$chart" --repo "$repo" --version "$ver" -n "$NAMESPACE" -f "$values" "$@" > "$OUT/$rel.yaml"
    echo "rendered $rel ($chart $ver): $(grep -c '^kind:' "$OUT/$rel.yaml") objects → $OUT/$rel.yaml"
  else
    helm upgrade --install "$rel" "$chart" --repo "$repo" --version "$ver" -n "$NAMESPACE" -f "$values" "$@" --wait --timeout 15m
  fi
}

# Grafana provisioning from the repo's files (one source for dev and prod).
if [[ "$MODE" == install ]]; then
  kubectl create namespace "$NAMESPACE" --dry-run=client -o yaml | kubectl apply -f -
  kubectl -n "$NAMESPACE" create configmap grafana-alerting --from-file="$PROV/alerting" --dry-run=client -o yaml | kubectl apply -f -
  kubectl -n "$NAMESPACE" create configmap grafana-dashboards --from-file="$PROV/dashboards" --dry-run=client -o yaml | kubectl apply -f -
fi

run loki    "$COMMUNITY" loki              "$LOKI_CHART"    "$DIR/values-loki.yaml" \
  --set loki.storage.s3.endpoint="$S3_ENDPOINT" --set loki.storage.s3.region="$S3_REGION"
run tempo   "$COMMUNITY" tempo             "$TEMPO_CHART"   "$DIR/values-tempo.yaml" \
  --set tempo.storage.trace.s3.endpoint="$S3_ENDPOINT" --set tempo.storage.trace.s3.region="$S3_REGION"
run mimir   "$GRAFANA"   mimir-distributed "$MIMIR_CHART"   "$DIR/values-mimir.yaml" \
  --set mimir.structuredConfig.common.storage.s3.endpoint="$S3_ENDPOINT" \
  --set mimir.structuredConfig.common.storage.s3.region="$S3_REGION"
run grafana "$COMMUNITY" grafana           "$GRAFANA_CHART" "$DIR/values-grafana.yaml"

if [[ "$MODE" == install ]]; then
  echo "LGTM installed in $NAMESPACE. Point the app at it: helm upgrade … -f deploy/helm/3commerce/values-prod.yaml"
  echo "Grafana: kubectl -n $NAMESPACE port-forward svc/grafana 3001:80  → Alerting shows the provisioned rules."
fi
