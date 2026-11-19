# Dev bootstrap for the local GlitchTip error inbox (ADR-0067). Run by entrypoint.sh through
# `python manage.py shell` after `init_database`, on EVERY start — so it is idempotent: it only creates what is
# missing and re-asserts the fixed keys. Values come from deploy/observability/glitchtip/glitchtip.env.
#
# Creates: the admin user (verified email), the organisation, a team, the `backend` and `storefront` projects
# with the EXACT project id + public key of their DSN (so the DSNs handed to the bare-run processes are known
# before GlitchTip ever starts), server-side IP + PII scrubbing on both, and a fixed API token for scripts.
import os
import uuid
from urllib.parse import urlparse

from allauth.account.models import EmailAddress
from django.db import connection, transaction

from apps.api_tokens.models import APIToken
from apps.organizations_ext.models import Organization
from apps.projects.models import Project, ProjectKey
from apps.teams.models import Team
from apps.users.models import User

env = os.environ
EMAIL = env["GLITCHTIP_DEV_ADMIN_EMAIL"]
PASSWORD = env["GLITCHTIP_DEV_ADMIN_PASSWORD"]
ORG = env.get("GLITCHTIP_DEV_ORG", "3commerce")
TOKEN = env["GLITCHTIP_DEV_API_TOKEN"]
PROJECTS = [
    ("backend", "dotnet", env["GLITCHTIP_DEV_BACKEND_DSN"]),
    ("storefront", "javascript", env["GLITCHTIP_DEV_STOREFRONT_DSN"]),
]
# Defence in depth: the SDK side already scrubs (TelemetryScrubber / lib/telemetry/scrub.ts); GlitchTip scrubs again.
SCRUB = {"enabled": True, "scrub_emails": True, "scrub_credit_cards": True, "scrub_private_keys": True}
READ_SCOPES = ["project:read", "team:read", "event:read", "org:read", "member:read", "project:releases"]


def dsn_parts(dsn):
    url = urlparse(dsn)
    return int(url.path.rstrip("/").rsplit("/", 1)[-1]), uuid.UUID(url.username)


with transaction.atomic():
    user = User.objects.filter(email=EMAIL).first()
    if user is None:
        user = User.objects.create_superuser(email=EMAIL, password=PASSWORD)
        print(f"glitchtip-bootstrap: created user {EMAIL}")
    EmailAddress.objects.get_or_create(user=user, email=EMAIL, defaults={"primary": True, "verified": True})

    org = Organization.objects.filter(slug=ORG).first()
    if org is None:
        org = Organization.objects.create(name=ORG, slug=ORG)
        print(f"glitchtip-bootstrap: created organisation {ORG}")
    if not org.users.filter(pk=user.pk).exists():
        org.add_user(user)
    team, _ = Team.objects.get_or_create(slug="platform", organization=org)
    team.members.add(org.organization_users.get(user=user))

    for slug, platform, dsn in PROJECTS:
        project_id, public_key = dsn_parts(dsn)
        project = Project.objects.filter(organization=org, slug=slug).first()
        if project is None:
            if Project.objects.filter(pk=project_id).exists():
                raise SystemExit(f"glitchtip-bootstrap: project id {project_id} is taken by another project — "
                                 f"the {slug} DSN in glitchtip.env cannot be honoured")
            project = Project.objects.create(id=project_id, organization=org, slug=slug, name=slug, platform=platform)
            print(f"glitchtip-bootstrap: created project {slug} (id {project_id})")
        elif project.pk != project_id:
            raise SystemExit(f"glitchtip-bootstrap: project {slug} has id {project.pk}, its DSN says {project_id}")
        project.scrub_ip_addresses = True
        project.scrub_config = SCRUB
        project.save(update_fields=["scrub_ip_addresses", "scrub_config"])
        team.projects.add(project)
        if not ProjectKey.objects.filter(project=project, public_key=public_key).exists():
            key = ProjectKey.objects.filter(project=project).order_by("pk").first()
            if key is None:
                ProjectKey.objects.create(project=project, public_key=public_key, name="dev")
            else:
                ProjectKey.objects.filter(pk=key.pk).update(public_key=public_key, is_active=True)

    # Explicit ids bypass the sequence: move it past them so a project created in the UI gets a free id.
    with connection.cursor() as cursor:
        table = Project._meta.db_table
        cursor.execute(
            f"SELECT setval(pg_get_serial_sequence('{table}', 'id'), GREATEST((SELECT MAX(id) FROM {table}), 1))"
        )

    token = APIToken.objects.filter(token=TOKEN).first()
    if token is None:
        token = APIToken.objects.create(user=user, label="3commerce dev scripts (read-only)")
        APIToken.objects.filter(pk=token.pk).update(token=TOKEN)
        token.refresh_from_db()
        token.add_permissions(READ_SCOPES)
        print("glitchtip-bootstrap: created the read-only API token")

print(f"glitchtip-bootstrap: ready — org {ORG}, projects " + ", ".join(f"{s} (id {dsn_parts(d)[0]})" for s, _, d in PROJECTS))
