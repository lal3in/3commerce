# 0069 — Deploy markers from the telemetry itself, and SLO dashboards with the alert rules' thresholds

Status: Accepted — implemented
Area: Observability / release / SLOs
Extends: [0064](./0064-release-identity-on-telemetry.md) (release identity on every metric), [0029](./0029-phase-6-compliance-ops-primitives.md) (SLO notes), [0065](./0065-self-hosted-lgtm-telemetry-alerting-and-browser-rum.md) (the provisioned alert rules, #341), [0058](./0058-branching-and-release-promotion.md) (promotion)
Source: observability capability audit (`docs/reviews/observability-capability-audit.md`) roadmap **P2-2** (release
annotations) and **P2-3** (SLO dashboards)

## Context

ADR-0064 stamps every metric with `service_version` (`<Version>+<sha7>[-dirty]`), `vcs_ref_head_revision` and
`deployment_environment_name`, but Grafana showed no marker when a release went out: "did the p95 jump with the
deploy?" meant comparing the *Running releases* table with a graph by eye. ADR-0029 states SLOs (availability ≥ 99.5 %
monthly, read p95 < 500/800 ms, 5xx < 1 %) and #341 turned them into alert rules, but there was no SLO view — no 30-day
availability, no error budget, no burn rate.

## Decision

### 1. Deploy markers = an annotation QUERY on the release label (no write path)

Two mechanisms were weighed:

| | Annotation query on `service_version` (chosen) | Promotion/deploy posts to Grafana's annotations API |
|---|---|---|
| What it marks | the moment each process of a new version starts reporting — per service, per environment | the moment someone ran `promote.sh` or `helm upgrade` |
| Credentials | none (Grafana reads Mimir as for every panel) | a Grafana service-account token on every machine and in CI |
| Reachability | always (inside Grafana) | production Grafana is port-forward only (ADR-0065); CI/laptops cannot reach it |
| Coverage | every environment and every way of deploying: dev restarts, kind, Helm, a rollback, a hotfix | only the paths that remember to post; a failed post silently loses the marker |
| Truthfulness | ADR-0058 promotion is a git fast-forward, not a deploy — the telemetry sees the real rollout | marks intent, not effect (a rollout that never became healthy still gets a marker) |

So the marker is the Grafana annotation **"Deploys (new service.version)"** on every Mimir dashboard (Services RED,
SLOs, .NET runtime, Database, Messaging), one definition:

```
group by (service_name, service_version, vcs_ref_head_revision, deployment_environment_name) (dotnet_process_cpu_time_seconds_total)
unless
group by (service_name, service_version, vcs_ref_head_revision, deployment_environment_name) (dotnet_process_cpu_time_seconds_total offset 15m)
```

- `dotnet_process_cpu_time_seconds_total` (the built-in `System.Runtime` meter) is reported by every .NET process
  whether or not it serves traffic, so an idle worker's deploy is marked too (an HTTP metric would miss it).
- The query is non-empty for the first 15 minutes of a version that was not reporting 15 minutes earlier; Grafana draws
  those consecutive steps (1-minute step) as ONE region per service and version, titled
  `Deploy: <service> <service.version>` with environment and commit in the text, tagged by service and environment.
- A restart of the SAME version after more than 15 minutes of silence is marked as well — a (re)deploy is what it is.
- Limits: at very wide time ranges Grafana raises the step above 15 minutes and a marker can be skipped; for history the
  *Running releases by service* table stays the record. The storefront (Node) has no metrics pipeline yet, so its
  releases are not marked (its traces and errors carry the release).

`promote.sh` is unchanged: it moves a tested commit between branches (ADR-0058); the deploy that follows is what the
telemetry marks. Posting from it was rejected (table).

### 2. SLO dashboard (`3commerce — SLOs (ADR-0029)`, uid `3commerce-slo`)

Provisioned from `deploy/observability/grafana/provisioning/dashboards/slo.json` (dev mounts the folder; production
mounts the same files via the `grafana-dashboards` ConfigMap of `install-lgtm.sh` — one source). Variables: environment,
service. Panels:

- **Availability, 30 days** (objective ≥ 99.5 %) and **error budget remaining** (0.5 % of requests) per service.
  The SLI is **request-based**: the share of HTTP requests that are not 5xx. ADR-0029 wrote "(`/health/ready`)", but no
  probe metric exists (health endpoints are internal and nothing scrapes them over time); a request-based SLI measures
  what users get and uses the same metric as the 5xx alert. A synthetic `/health/ready` probe can be added later as a
  second SLI without changing these panels.
- **Error-budget burn rate**, 1 h and 6 h windows (5xx ratio / 0.5 %), with lines at **2** (= the alert *HTTP 5xx rate
  above 1 %* over 5 min), **6** and **14.4** (the multi-window "budget gone in ~5 days / ~2 days" thresholds).
- **5xx ratio, 5 min** — exactly the expression of alert `tc-http-5xx-rate` (incl. its near-idle filter), line at 1 %.
- **p95 latency** for catalog reads (< 500 ms, NFR-5) and other reads (< 800 ms) — exactly the expressions of alerts
  `tc-p95-catalog-reads` / `tc-p95-other-reads` (GET, per route), and a **headroom** bar (worst route p95 / target).

The thresholds are written once in the alert rules and repeated here verbatim; the dashboard says which rule each panel
mirrors, so the two cannot drift silently in review. Burn-rate *alerts* are not added (the owner's alert set is #341's;
the 5-minute 5xx rule already pages).

## Consequences

- Every Mimir dashboard shows when each service changed version, in every environment, with no credentials and nothing
  to remember at deploy time; the marker's text names the commit, which leads to the PR (ADR-0058/0064).
- The SLO dashboard answers "are we inside the objective this month, and how fast are we spending the budget" per
  service and environment; 30-day queries read raw series (no recording rules — Mimir runs without its ruler, ADR-0065).
- Pinned by the portal spec `e2e/observability-portals.spec.ts` (L20, full infra set): the SLO dashboard's panels
  return data like every provisioned dashboard, and the deploy-marker test checks that all five Mimir dashboards carry
  the same enabled annotation and that, run like Grafana runs it, it marks the running .NET services with their
  `service.version`.
