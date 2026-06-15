# PRD: 3commerce — Distributed E-Commerce Platform

- **PRD:** 3commerce — microservices e-commerce platform (storefront, custom auth, ledger-backed payments, RMA support, Xero sync)
- **Status:** MVP on dev/test rails — conformance A−→A (16/21 FR/NFR met after FR-7; see `docs/reviews/prd-vs-implementation.md`). Known gaps tracked as backlog (admin catalog CRUD, NFR-2/5/7 tests). Pending external review & launch gates.
- **Owner:** lehn
- **Last updated:** 2026-06-15

---

## Table of Contents

> **Do NOT auto-load ANY of the files in the Table of Contents** (load only if the task depends on that specific section's requirements).

| # | Section | File |
|---|---------|------|
| 0 | TL;DR | [00-tldr.md](./3commerce/00-tldr.md) |
| 1 | Executive Summary | [01-executive-summary.md](./3commerce/01-executive-summary.md) |
| 2 | Mission | [02-mission.md](./3commerce/02-mission.md) |
| 3 | Target Users | [03-target-users.md](./3commerce/03-target-users.md) |
| 4 | MVP Scope | [04-mvp-scope.md](./3commerce/04-mvp-scope.md) |
| 5 | User/Flow Stories | [05-user-stories.md](./3commerce/05-user-stories.md) |
| 6 | Core Architecture & Patterns | [06-architecture.md](./3commerce/06-architecture.md) |
| 7 | Tools/Features | [07-tools-features.md](./3commerce/07-tools-features.md) |
| 8 | Technology Stack | [08-technology-stack.md](./3commerce/08-technology-stack.md) |
| 9 | Security & Configuration | [09-security-configuration.md](./3commerce/09-security-configuration.md) |
| 10 | API Specification | [10-api-specification.md](./3commerce/10-api-specification.md) |
| 11 | Success Criteria | [11-success-criteria.md](./3commerce/11-success-criteria.md) |
| 12 | Implementation Phases | [12-implementation-phases.md](./3commerce/12-implementation-phases.md) |
| 13 | Future Considerations | [13-future-considerations.md](./3commerce/13-future-considerations.md) |
| 14 | Risks & Mitigations | [14-risks-mitigations.md](./3commerce/14-risks-mitigations.md) |
| 15 | Appendix | [15-appendix.md](./3commerce/15-appendix.md) |

---

### 0. TL;DR
Read when you need the one-page picture of what is being built and why.
Path: `docs/prd/3commerce/00-tldr.md`

### 1. Executive Summary
Read for product overview, value proposition, and the MVP goal statement.
Path: `docs/prd/3commerce/01-executive-summary.md`

### 2. Mission
Read when a design decision needs to be checked against the project's core principles.
Path: `docs/prd/3commerce/02-mission.md`

### 3. Target Users
Read when designing UX flows or prioritizing features — defines shopper, admin, and the builder personas.
Path: `docs/prd/3commerce/03-target-users.md`

### 4. MVP Scope
Read before starting ANY feature work — the authoritative in/out-of-scope checklist.
Path: `docs/prd/3commerce/04-mvp-scope.md`

### 5. User/Flow Stories
Read when implementing a customer- or admin-facing flow (checkout, RMA, account, admin actions).
Path: `docs/prd/3commerce/05-user-stories.md`

### 6. Core Architecture & Patterns
Read before any cross-service work — service boundaries, messaging, sagas, data ownership, repo layout.
Path: `docs/prd/3commerce/06-architecture.md`

### 7. Tools/Features
Read when implementing a specific service — detailed per-service feature specifications.
Path: `docs/prd/3commerce/07-tools-features.md`

### 8. Technology Stack
Read when adding dependencies, scaffolding projects, or choosing libraries.
Path: `docs/prd/3commerce/08-technology-stack.md`

### 9. Security & Configuration
Read before touching auth, secrets, tokens, payments handling, or environment config.
Path: `docs/prd/3commerce/09-security-configuration.md`

### 10. API Specification
Read when adding/changing gateway routes or public endpoints — naming and payload conventions live here.
Path: `docs/prd/3commerce/10-api-specification.md`

### 11. Success Criteria
Read when validating a milestone — numbered FR/NFR requirements with measurable checks.
Path: `docs/prd/3commerce/11-success-criteria.md`

### 12. Implementation Phases
Read when planning the next chunk of work — build order, deliverables, validation per phase.
Path: `docs/prd/3commerce/12-implementation-phases.md`

### 13. Future Considerations
Read when tempted to build something not in MVP scope — it probably belongs here.
Path: `docs/prd/3commerce/13-future-considerations.md`

### 14. Risks & Mitigations
Read at phase boundaries and when an identified risk seems to be materializing.
Path: `docs/prd/3commerce/14-risks-mitigations.md`

### 15. Appendix
Read for external references, business blockers, and the decision log from the design interview.
Path: `docs/prd/3commerce/15-appendix.md`
