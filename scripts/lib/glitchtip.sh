# The local GlitchTip error inbox (ADR-0067) for bare-run scripts. Source it from the repo root; no side effects.
# Bash 3.2 compatible. The one source of the dev values is deploy/observability/glitchtip/glitchtip.env (also the
# infra compose service's env_file, from which its entrypoint bootstraps exactly these projects and keys).
#
#   glitchtip_env <KEY>        a value from glitchtip.env (e.g. GLITCHTIP_DEV_API_TOKEN)
#   glitchtip_dsn backend      the DSN for every .NET host (services, gateway, worker, Admin, Supplier Portal)
#   glitchtip_dsn storefront   the DSN for the storefront (its /rum relay + SSR errors)
#   glitchtip_api <path>       GET http://localhost:8000/api/0/<path> with the dev read-only token (JSON on stdout)
#
# Callers give processes SENTRY_DSN="${SENTRY_DSN-$(glitchtip_dsn backend)}": `-` (not `:-`) so an explicitly empty
# SENTRY_DSN= opts a run out. On the CORE infra set (CI's fast lane) GlitchTip is not running; the SDKs then fail to
# deliver silently (bounded queue, no retries that block anything) — same as OTLP to an absent collector.

GLITCHTIP_ENV_FILE="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/deploy/observability/glitchtip/glitchtip.env"
GLITCHTIP_URL="http://localhost:8000"

glitchtip_env() {
  sed -n "s/^$1=//p" "$GLITCHTIP_ENV_FILE" 2>/dev/null | head -1
}

glitchtip_dsn() {
  case "$1" in
    backend)    glitchtip_env GLITCHTIP_DEV_BACKEND_DSN ;;
    storefront) glitchtip_env GLITCHTIP_DEV_STOREFRONT_DSN ;;
    *) echo "glitchtip_dsn: backend|storefront (got '$1')" >&2; return 1 ;;
  esac
}

glitchtip_api() {
  curl -fsS -m 10 -H "Authorization: Bearer $(glitchtip_env GLITCHTIP_DEV_API_TOKEN)" "$GLITCHTIP_URL/api/0/$1"
}
