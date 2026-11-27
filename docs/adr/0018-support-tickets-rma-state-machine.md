# ADR-0018: Support = order-linked tickets + RMA state machine; no chat/KB

- **Status:** Accepted
- **Date:** 2026-06-12
- **Source:** PRD design interview (decision log #18, `docs/prd/3commerce/15-appendix.md`)

## Context

"Support and refunds" was in the original product ask. A full helpdesk (chat, knowledge base, agent assignment, SLAs) is months of work that touches no distributed-systems concepts; a bare mailto deletes the refund-saga learning.

## Decision

The Support service implements:

- **Order-linked tickets** with typed reasons (`WhereIsIt`, `Damaged`, `RefundRequest`, `Other`), message thread, email notifications both ways.
- An **RMA state machine**: `Requested → Approved/Denied → (AwaitingReturn → ReturnReceived) → RefundIssued`. Admin approval triggers the **refund saga**: Support → Payments → ledger reversal → Stripe refund → Xero posting. Partial refunds supported per line item. Approval is idempotent.
- Explicitly excluded: live chat, knowledge base, agent assignment, SLAs.

## Alternatives considered

- **Mailto + manual admin refunds** — rejected: deletes a service and the refund-saga learning.
- **External helpdesk (Zendesk/Chatwoot)** — rejected: support data leaves the system; integration work replaces learning.
- **Full helpdesk** — deferred: trigger is ticket volume exceeding solo capacity (PRD §13).

## Consequences

- The RMA flow is the integration capstone — it exercises every service plus both external integrations.
- Refunds have exactly one path through the system, keeping the ledger authoritative (ADR-0014).

## Later changes (recorded 2026-11-27)

- **The RMA saga's states as built** (`src/Services/Support/Infrastructure/Sagas/RmaStateMachine.cs`):
  `Requested → AwaitingReturn | RefundPending | Denied`, `AwaitingReturn → RefundPending` on receipt, and
  `RefundPending → RefundIssued | RefundFailed`. "Approved" and "ReturnReceived" are transitions (events), not
  persisted states, and a failed refund is its own terminal state. An admin-initiated refund arrives pre-approved
  (`AutoApprove`) and starts directly in `RefundPending`, so every refund still takes the one saga path. The ticket
  reasons are as decided.
