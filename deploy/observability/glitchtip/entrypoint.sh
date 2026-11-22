#!/usr/bin/env sh
# Local-dev entrypoint of the GlitchTip error inbox (ADR-0067), mounted into glitchtip/glitchtip:7.x by
# docker-compose.infra.yml. Production does NOT use it (Helm chart + its migration job, a managed database).
#
# 1. Ensure the `glitchtip` role + database exist in the SHARED dev Postgres. infra/postgres/init-databases.sql only
#    runs on a brand-new volume, so an existing dev volume would never get them; doing it here (idempotently, as the
#    dev superuser) covers fresh and existing volumes with one mechanism. The name has no `_db` suffix on purpose:
#    those are the service databases e2e-verify L1 counts.
# 2. `init_database` (migrations + partitions, under GlitchTip's own advisory lock).
# 3. bootstrap.py: admin user, organisation, the backend/storefront projects with the fixed DSNs of glitchtip.env.
# 4. The image's own start script — one granian process that serves HTTP AND runs the task worker + scheduler
#    (GlitchTip 7 removed SERVER_ROLE / GLITCHTIP_EMBED_WORKER: there is nothing else to run). SKIP_INIT: done above.
set -eu
cd /code

python - <<'PY'
import os, time
import psycopg
from psycopg import sql

admin = os.environ["GLITCHTIP_DB_ADMIN_URL"]
role, password, db = "glitchtip", os.environ["GLITCHTIP_DB_PASSWORD"], "glitchtip"
for attempt in range(60):
    try:
        conn = psycopg.connect(admin, autocommit=True, connect_timeout=3)
        break
    except psycopg.OperationalError as err:
        if attempt == 59:
            raise
        print(f"glitchtip-entrypoint: waiting for Postgres ({err.__class__.__name__})", flush=True)
        time.sleep(2)
with conn:
    if conn.execute("SELECT 1 FROM pg_roles WHERE rolname = %s", (role,)).fetchone() is None:
        conn.execute(sql.SQL("CREATE ROLE {} LOGIN PASSWORD {}").format(sql.Identifier(role), sql.Literal(password)))
        print(f"glitchtip-entrypoint: created role {role}", flush=True)
    if conn.execute("SELECT 1 FROM pg_database WHERE datname = %s", (db,)).fetchone() is None:
        conn.execute(sql.SQL("CREATE DATABASE {} OWNER {}").format(sql.Identifier(db), sql.Identifier(role)))
        print(f"glitchtip-entrypoint: created database {db}", flush=True)
PY

python manage.py init_database
python manage.py shell < /etc/glitchtip/bootstrap.py

SKIP_INIT=true
export SKIP_INIT
exec ./bin/start.sh
