# 2. Mission

## Mission statement

Build a secure, operationally simple e-commerce platform that is simultaneously a real business asset and a masterclass in distributed systems — where every architectural shortcut avoided today is a lesson learned and a production incident prevented tomorrow.

## Core principles

1. **Learn by owning the hard parts; outsource the dangerous parts.**
   The domain model, auth flows, sagas, and money ledger are hand-built — that is the point. Card data handling (Stripe tokenization) and cryptographic primitives (Argon2id from vetted libraries) are never hand-built — that is also the point.

2. **Production-quality from day one — no throwaway code.**
   This codebase is intended to take real orders. Every component is written, tested, and reviewed as if launch were next month, even while payments run in test mode.

3. **Simple within each service, even though the system is distributed.**
   Microservices were chosen knowingly for their learning value. The compensating discipline: each service stays small, boring, and independently understandable. Complexity budget is spent on the seams (messaging, sagas), not inside services.

4. **Abstractions at the points of known change.**
   Supplier feeds (`ISupplierImporter`), payment rails (`IPaymentProvider`), tax computation (`ITaxStrategy`), and search (`ISearchProvider`) are all undecided or expected to change — each sits behind an interface with exactly one v1 implementation. No speculative abstraction anywhere else.

5. **The ledger is the truth; everything else is a view.**
   Every money movement is a balanced double-entry record. Stripe is a rail, Xero is a report, the order screen is a projection. Disagreements are reconciled toward the ledger.
