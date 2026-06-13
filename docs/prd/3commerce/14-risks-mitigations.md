# 14. Risks & Mitigations

## R-1: Microservices overwhelm a solo developer (scope/abandonment risk)

- **Impact:** High — project stalls before any usable milestone; both learning and business goals fail.
- **Likelihood:** High — this is the single most common outcome of solo microservices builds; the 3–5× premium over a monolith was accepted explicitly.
- **Mitigation:** Phases end at demonstrable milestones (Phase 1 proves all plumbing on trivial features); strict MVP scope file; "simple inside each service" principle; no new infrastructure (k8s, search engines, second rails) before MVP.
- **Detection:** A phase exceeding its estimate by >50%, or weeks passing without a runnable end-to-end demo.
- **Contingency:** Collapse to 3 services (Identity / Commerce / Money) without changing contracts — the capability seams make merging cheaper than splitting.

## R-2: Custom auth ships with a vulnerability

- **Impact:** Critical — credential breach on a real store; legal and reputational damage.
- **Likelihood:** Medium — flows are hand-built by design; the dangerous primitives (Argon2id, token randomness) are library-provided.
- **Mitigation:** Conditions already locked in: vetted crypto libraries only, opaque HttpOnly cookies (no browser JWTs), OWASP ASVS L1 self-audit gate, `IAuthService` seam for full replacement, external review before live traffic.
- **Detection:** ASVS audit findings; dependency scanner alerts; lockout/rate-limit telemetry anomalies.
- **Contingency:** Swap implementation behind `IAuthService` to ASP.NET Identity or a hosted IdP; revoke all sessions (opaque tokens make this a one-table operation).

## R-3: Money state diverges (ledger vs Stripe vs Xero)

- **Impact:** High — real money misaccounted; refunds double-issued or lost; accountant distrust.
- **Likelihood:** Medium — distributed money flows are exactly where eventual consistency bites.
- **Mitigation:** Ledger as single source of truth (append-only, DB-enforced balance); refunds only via saga; webhook inbox dedup; idempotency keys on money endpoints; Xero is write-only downstream, never read back as truth.
- **Detection:** Automated daily reconciliation job: ledger vs Stripe balance transactions vs Xero journal; any mismatch alerts.
- **Contingency:** Reconciliation report identifies the divergent entry; correction is a reversing ledger entry + corrected Xero posting — never an in-place edit.

## R-4: Business blockers never resolve (no entity, no supplier)

- **Impact:** High for the business goal (store can't launch or stock anything); zero for the learning goal.
- **Likelihood:** Medium — registration and supplier contracts are outside the codebase's control.
- **Mitigation:** Everything blocked is isolated behind config/interfaces: test keys, configurable currency, `ITaxStrategy`, `ISupplierImporter`, per-line `FulfillmentSource`. The build never waits on the business.
- **Detection:** MVP complete while blockers still open.
- **Contingency:** Project still succeeds as a portfolio/learning artifact; launch tasks remain a documented, bounded checklist (see Appendix).

## R-5: Two-language frontend estate drains focus (Next.js + Blazor)

- **Impact:** Medium — storefront polish ("great graphic UX" is a stated requirement) or admin usability suffers; context-switching tax.
- **Likelihood:** Medium — backend-focused developer, TypeScript ecosystem churn.
- **Mitigation:** Admin deliberately on Blazor (stays in C#); storefront uses a constrained, conventional stack (App Router + Tailwind + shadcn/ui — no exotic state management); UI scope bounded by the ≤ 3-screen checkout and design-system discipline.
- **Detection:** Storefront tasks consistently overrunning; UX goals in §11 failing review.
- **Contingency:** Buy a quality Next.js commerce template for layout/components and keep custom work to data wiring; admin features can fall back to plainer CRUD screens without business harm.
