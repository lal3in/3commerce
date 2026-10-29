# 0063 — Customer tax invoices and credit notes (EU VAT-compliant, any currency, any jurisdiction)

Status: **Accepted** — 2026-10-11. The owner's decisions are recorded in § 12. This ADR is design only; implementation starts with phase 0 (§ 9.1). The PRD update in § 14 remains **pending the owner's separate approval** of the scope change.
Date: 2026-10-11 (proposed and accepted)
Area: Invoicing (new) / Ordering / Payments / Support / Catalog / Entity / Notifications
Relates to: [0014](./0014-stripe-only-v1-double-entry-ledger.md) (ledger as money truth), [0017](./0017-xero-nightly-summary-journals.md) (Xero summary journals — unchanged), [0023](./0023-strict-multi-tenancy.md) (tenant = one legal business), [0024](./0024-tenant-isolation-postgres-rls.md) (RLS), [0028](./0028-product-supply-profiles-composable-supply.md) (offers / suppliers / dropship), [0030](./0030-deferred-services-extracted.md) (service extraction rules), [0038](./0038-per-currency-shelf-prices-and-tax-entry.md) (inclusive/exclusive tax entry), [0041](./0041-per-store-order-costs-chargebacks-and-estimated-margin.md) (chargebacks, no-FX posture), [0043](./0043-storefront-scoped-carriers-payments-and-go-live-gate.md) (go-live gate), [0045](./0045-mandatory-per-storefront-ledger-attribution.md) (per-store ledger), [0055](./0055-discounted-refund-basis-and-storefront-scoped-tax.md) (refund basis, storefront-scoped tax), [0056](./0056-a-refunded-order-keeps-its-redemption.md), [0057](./0057-subscription-renewal-price-and-introductory-promotions.md) (renewal price), [0058](./0058-branching-and-release-promotion.md), [0059](./0059-checkout-enforces-storefront-currency-and-catalog-offer-gate.md)
Research: [`docs/reference/tax-invoicing-research.md`](../reference/tax-invoicing-research.md) (jurisdiction table, with citations)

> Not legal advice. Each jurisdiction profile this ADR describes must be signed off by the tenant's tax
> adviser before that jurisdiction goes live.

---

## 1. Context

### 1.1 The owner's statement (2026-10-11)

> "Order numbering can be gap tolerant … invoice creation generally happens after order placement. Make
> sure ordering number and tax invoice numbering comply with EU VAT rules and laws as well … this
> solution is intended to trade in all currencies [and] jurisdictions."

### 1.2 What exists today (verified in code on `develop` @ d2713ba)

| Concern | Today | Gap for tax invoices |
|---|---|---|
| Tax document | **None.** No `Invoice` / `CreditNote` type anywhere. The confirmation email is the only customer-facing record. | Everything below. |
| Order number | `OrderNumberSequence` per storefront (`NextNumber` from 1000) → `Order.PublicOrderNumber`. Being made gap-tolerant on `fix/order-number-and-edit-races`. | Fine as a **customer-service reference**. It is **not** a tax document number and must never be labelled one (§ 4.4). |
| Tax computation | Computed inline in `CheckoutEndpoints`: **one storefront rate** (`StorefrontTaxCopy`, ADR-0055) on one base (taxable lines − taxable discount + shipping), inclusive or exclusive (ADR-0038). Ship-rule exemptions per ADR-0050. `ITaxStrategy` (`Ordering/Domain/Pricing.cs`) returns a single `long` and is used only by `PricingEngine`. | There is **no per-line and no per-rate breakdown** (Art. 226(8)–(10)). There are no reduced rates per product category, and no destination rates for EU distance sales (OSS). Only a single `Order.TaxMinor` is stored. |
| Seller identity | `Tenant.OwnerLegalEntityRef` → Entity `EntityRecord` (`LegalName`, role `TenantOwner`). Identifier types: `Abn`, `Acn`, `Gst`, `OtherTaxRegistration`. | No EU VAT ID / UK VRN / CA BN / CH UID types. The storefront has **no establishment or jurisdiction binding**: it has `Currency` and `TaxRegime` {None, AuGst, EuVat, UsSalesTax, Other}, but no country. |
| Buyer identity | Ship-to address only; email. | No **bill-to** address, no business name, no **buyer tax ID**. |
| Events | `OrderConfirmed(OrderId, TenantId, Email, AmountMinor, Currency, ShipTo, Lines)`. `OrderLineInfo` has unit price + `DiscountMinor`. | No storefront id, order number, tax, shipping, line id or per-line tax. |
| Refunds / chargebacks | `RefundRequested` / `RefundCompleted` / `PaymentChargedBack` carry an **order-level amount only**. | A credit note needs line and rate attribution. |
| Renewals / usage | `SubscriptionService.PostRenewalRevenueAsync` posts **tax 0** ("period price treated as net revenue"). Usage overage/auto-load charges similarly carry no tax. | In an inclusive regime this **under-reports output VAT** today, invoice or not. It is a prerequisite fix (§ 9, P4). |
| Delivery / storage | `EmailMessage(To, Subject, Body, Reference)` — no attachments. `IObjectStore` (`LocalFileObjectStore`) exists in BuildingBlocks. | Needs a document link or attachment, and immutable (WORM) retention. |
| Xero | Nightly summary journals + per-refund postings from the ledger (ADR-0017). | **Unchanged.** PRD decision #17 rejected per-order invoices *in Xero*. That was bookkeeping, not customer tax invoices. |

### 1.3 Legal requirements that drive the design (summary — full table and sources in the research doc)

1. **The seller issues.** The tax invoice is issued by the legal entity that makes the supply: the
   **tenant's legal entity** (ADR-0023), including in dropship flows where a supplier ships (ADR-0028).
   A marketplace/agency model would change that, but it is a pivot, not a default (§ 12 D1).
2. **Sequential unique number in one or more series** — EU VAT Directive **Art. 226(2)**. The Directive
   does not say "gapless". Germany explicitly does not require gaplessness (UStAE 14.5(10)). **France
   requires chronological, continuous numbering without gaps.** Spain requires correlative numbering per
   series and a **separate series for corrections**. Hence the platform invariant: **gapless +
   chronological + unique per series, with series first-class.**
3. **Issued invoices are immutable.** Corrections are made only by a document that "amends and refers
   specifically and unambiguously to the initial invoice" — **Art. 219**. That document is the credit
   note. Integrity and legibility must be ensured for the whole storage period (**Art. 233**).
4. **Mandatory content** — **Art. 226**: issue date, number, supplier VAT ID, customer VAT ID for
   reverse-charge/intra-EU, names and addresses, quantity and nature, supply date if different, **taxable
   amount per rate**, unit price exclusive of VAT, discounts, **rate**, **VAT amount**, exemption reference,
   and "Reverse charge" wording. A **simplified invoice** (Arts. 220a, 226b) is allowed at ≤ €100
   (Member States may go to €400). It is **not** allowed for intra-EU B2B, Art. 33 distance sales or
   non-established reverse charge.
5. **When an invoice is mandatory.** B2B, intra-EU supplies and payments on account always need one.
   **Distance sales need one unless the Union OSS is used** (Art. 220(1)(2)). B2C beyond that is
   Member-State-specific (Art. 221). Under OSS/IOSS, the invoicing rules of the **Member State of
   identification** apply (Art. 219a). Many Member States require B2C invoices or receipts, and consumers
   ask for them. **Design: always issue a document to every buyer** (§ 12 D4).
6. **Any currency, but the VAT amount in the national currency** of the Member State whose rules apply,
   at the Art. 91 rate (ECB or latest selling rate) — **Art. 230**. The UK likewise requires the VAT in
   sterling.
7. **Timing.** The chargeable event for goods is the supply (Art. 63), but a **payment received before
   the supply** makes VAT chargeable on receipt (Art. 65). Prepaid e-commerce is therefore taxed at
   payment. Intra-EU B2B invoices are due **by the 15th of the following month** (Art. 222); from
   **1 Jul 2030 (ViDA) within 10 days**, as an EN 16931 e-invoice. UK: 30 days. AU/NZ: within 28 days of
   a request.
8. **Retention** is set per Member State (Art. 247): **up to 10 years** (IT, BE; CH 10; DE 8; FR 6/10;
   UK 6; AU 5; NZ 7; CA 6).
9. **Structured e-invoicing is arriving everywhere.** IT SdI (live, including B2C), BE Peppol (Jan 2026),
   PL KSeF (Feb/Apr 2026), FR (receive Sep 2026; issue Sep 2026/Sep 2027), DE B2B (2025/2027/2028), ES
   VeriFactu (Jan/Jul 2027), and ViDA cross-border B2B (Jul 2030). The canonical structured model is
   **EN 16931**.
10. **Rest of world** follows the same skeleton with different thresholds. AU tax invoice: ABN; buyer
    identity at ≥ A$1,000; nothing required at ≤ A$82.50. NZ TSI: $200 / $1,000. CA ITC data: C$100 /
    C$500. UK: less-detailed at ≤ £250. US: no federal invoice rule; sales-tax receipts per state. Hence
    **data-driven jurisdiction profiles**, not code per country.

---

## 2. Decision (recommended design)

**Introduce customer tax documents (Invoice, CreditNote) as an immutable, gaplessly numbered legal
record, owned by a new `Invoicing` service. The service renders the tax facts that checkout and Payments
already computed and charged: it never computes tax itself. Its behaviour per jurisdiction is driven by
versioned, data-driven jurisdiction profiles.**

Five principles carry the whole design:

1. **Shown == charged == invoiced.** This extends ADR-0051/0055/0057. The tax on an invoice is the tax
   that was charged, line by line and rate by rate. Invoicing **never recomputes** tax. A rounding or rule
   difference between checkout and the invoice would be a compliance defect.
2. **Issued means frozen.** An issued document is never updated or deleted. Every correction is a new
   credit note that references the original (Art. 219). The same append-only posture as the ledger
   (ADR-0014).
3. **A number is allocated only in the transaction that commits the issued document.** Gapless by
   construction. Allocate-then-fail is impossible (§ 4).
4. **The order number is not an invoice number.** They are two sequences with two purposes and two
   guarantees (§ 4.4).
5. **Jurisdiction behaviour is configuration with an effective date, not branches in code.** Profiles
   are data. Code exists only for genuinely different *channels* (PDF, Peppol, SdI…) behind one seam,
   added when a jurisdiction actually needs it (no speculative interfaces).

---

## 3. Concepts and document model

### 3.1 Document types

| Type | When | Notes |
|---|---|---|
| **Invoice** (full) | Default for every taxable sale | All Art. 226 fields, or the profile's equivalent (AU "tax invoice", NZ TSI, UK VAT invoice). |
| **SimplifiedInvoice** | Profile allows it, the amount ≤ the profile threshold, **and** the sale is not B2B-reverse-charge, intra-EU or an Art. 33 distance sale | EU ≤ €100 (or the Member State's €400); UK ≤ £250; AU < A$1,000 (no buyer identity needed). Same series semantics, but a profile may give it its own series. |
| **CreditNote** | Refund, RMA, partial refund, price reduction, or a cancellation after issue | Always references **one** original invoice (number + date). Never exceeds what remains creditable on that invoice, per line and per rate. Spain: its own series ("rectificativa"). |
| *DebitNote* (later) | Upward correction after issue | Not needed at launch. The platform has no post-sale upward price changes. |
| *Receipt* (non-tax) | A jurisdiction profile says "no tax invoice" (e.g. tenant not tax-registered there, US) | Numbered in its own series; carries no tax-invoice wording. |

**B2C vs B2B** is a property of the *buyer* on the document (`BuyerKind`), not a separate type. B2B adds
the buyer's legal name, address and **tax ID**, and can switch the tax treatment (reverse charge,
intra-EU exempt supply) — but that switch happens **at checkout**, where tax is computed (§ 8), never at
invoicing.

### 3.2 The issued document (frozen snapshot)

```
TaxDocument
  Id (UUIDv7) · TenantId · Type · SeriesId · Number (string, formatted) · SequenceValue (long)
  IssueDate (date in the profile's time zone) · IssuedAt (instant) · SupplyDate? / PaymentDate
  Source { Kind: Order|Renewal|Usage|Refund|Chargeback|Manual, SourceId }   -- unique per tenant
  OriginalDocumentId? (credit notes)          · OrderId? · PublicOrderNumber? (reference only)
  StorefrontId · JurisdictionProfile { Code, Version }
  Seller   { EntityId, LegalName, Address, TaxIds[] (scheme, value, verifiedAt), Establishment }
  Buyer    { Kind: Consumer|Business, Name, BillTo address, Email, TaxId? {scheme, value, validation ref} }
  Currency · Lines[] · TaxSummary[] (per rate/category: taxable base, rate bps, tax) · Totals
  LocalCurrencyVat? { Currency, Rate, RateDate, RateSource, VatAmountLocal }   -- Art. 230 / UK sterling
  Notes[] (legal wording: "Reverse charge", exemption references, "Includes GST", OSS…)
  Language · PrevDocumentHash · DocumentHash · RenderedArtifacts[] {kind: pdf|ubl|cii|…, objectKey, sha256}
  Status: Issued | Delivered | Cleared | ClearanceRejected   -- delivery/clearance only, never content
```

`Line = { SourceLineId, Sku, Description, Quantity, UnitPriceNet, UnitPriceGross, DiscountNet, NetAmount,
TaxCategory (S/Z/E/AE/K/G/O — EN 16931 codes), RateBps, TaxAmount, GrossAmount, ExemptionReason? }`,
with shipping as its own line.

Content fields are written once at issue. Only `Status` and `RenderedArtifacts` change afterwards, and
both are delivery facts, not content. The DB enforces this: an `UPDATE` trigger refuses any change to
content columns, the same posture as the append-only ledger.

---

## 4. Numbering

### 4.1 Series

```
InvoiceSeries
  Id · TenantId · SellerEntityId · EstablishmentKey (jurisdiction/establishment, e.g. "DE", "FR-PARIS", "AU")
  StorefrontId? (null = shared by every storefront of that establishment — the default)
  DocumentType (Invoice | SimplifiedInvoice | CreditNote | Receipt)
  PeriodKey ("2026" — calendar year by default; profile may set fiscal year or "none")
  Format  e.g. "{PREFIX}{YYYY}-{SEQ:000000}"  → "INV2026-000123", "CN2026-000007"
  NextValue (long, starts 1) · LastIssuedAt (instant) · Status (Open | Closed)
  UNIQUE (TenantId, SellerEntityId, EstablishmentKey, coalesce(StorefrontId), DocumentType, PeriodKey)
```

* **Default key:** seller entity × establishment × document type × year. The storefront is *not* in the
  default key. One legal business with five storefronts in one country has one invoice series, which is
  what an auditor expects. A profile or tenant may opt into per-storefront series, e.g. a separately
  registered establishment (§ 12 D6).
* Series are created **lazily** at the first issue in a new period, inside the same transaction (an
  `INSERT … ON CONFLICT DO NOTHING`, then lock).
* A **prefix is unique per tenant across live series**. Numbers are then unique per seller even when
  printed without the series id.

### 4.2 Gapless allocation under concurrency

Issuing a document is **one database transaction** (tenant-scoped, RLS — ADR-0024):

```
BEGIN
  -- 0. idempotency: a document for this Source already exists → return it (no number consumed)
  -- 1. validate + build the full snapshot in memory (profile rules, required fields, caps)  ← all failure
  --    modes that do not depend on the number happen HERE, before any lock
  -- 2. lock + take the number (row lock serialises issuers of THIS series only)
  UPDATE invoicing.invoice_series
     SET next_value = next_value + 1, last_issued_at = clock_timestamp()
   WHERE id = @series AND status = 'Open'
  RETURNING next_value - 1 AS seq, last_issued_at AS issued_at;
  -- 3. IssueDate := issued_at in the profile's time zone (taken under the lock ⇒ chronological)
  -- 4. hash chain: PrevDocumentHash := series' last DocumentHash; DocumentHash := H(canonical snapshot)
  -- 5. INSERT tax_document (... seq, number ...)    -- UNIQUE(TenantId, SourceKind, SourceId)
  -- 6. INSERT outbox: TaxDocumentIssued             -- MassTransit EF outbox, same transaction
COMMIT
```

* **Gapless by construction.** The counter increment and the document row commit or roll back
  together. Any failure — a unique-violation from a redelivered message, a serialization failure, a
  crash — rolls back the increment too. **Never a Postgres `SEQUENCE`**: sequences are
  non-transactional and leave gaps on rollback. That is fine for order numbers and wrong here.
* **Chronological by construction.** `IssueDate` is taken *under the series lock*, so a higher number
  can never carry an earlier date.
* **Nothing fallible after the number.** Rendering the PDF/XML, storing it, emailing it and transmitting
  it to a clearance platform all happen **after commit**, from the outbox, and are retryable. They read
  the frozen snapshot, so a rendering failure never voids a number. The document *exists* the moment it
  commits.
* **Throughput.** Issuers queue per series, not globally. One short row-locked `UPDATE` per document
  sustains hundreds of documents per second per series, far beyond a tenant's checkout rate. If a series
  ever becomes hot, the remedy is a profile-permitted parallel series (e.g. per establishment), never a
  gap-tolerant counter.
* **Clearance rejection (phase 3).** Where a clearance platform rejects a document after issue (IT SdI
  *scarto*, PL KSeF), the number is **not** reused silently. The profile defines the remedy: re-submit
  the corrected document under the same number within the national window (IT allows this within 5
  days), or cancel it via a credit note. Rejections are recorded on the document's `Status`.
* **Audit evidence (Art. 233).** The per-series hash chain makes any deletion or edit detectable. It is
  also exactly the mechanism Spain's VeriFactu requires (chained record hashes), so phase 3 builds on
  it rather than adding it.

### 4.3 Formats per jurisdiction

The format string, the reset period (yearly / never), separate credit-note series (mandatory in ES,
default everywhere for clarity) and the maximum length (some clearance schemas cap the number at 20
characters) are **profile settings**. Tenants may customise prefixes within the profile's constraints.
Admin edits emit audit (§ 6.5). A series that has issued documents can be **closed** but never renumbered
or deleted.

### 4.4 Order numbers vs invoice numbers

| | Order number (`PublicOrderNumber`) | Invoice number |
|---|---|---|
| Purpose | Customer-service reference ("order #1042") | Legal identifier of a tax document |
| Allocated | At order creation (checkout), before payment | At issue, in the commit that persists the issued document |
| Gaps | **Tolerated** (abandoned checkouts, races — the parallel fix) | **Never** (strictest EU reading) |
| Scope | Per storefront | Per series (seller × establishment × type × period) |
| Mechanism | Sequence / optimistic counter, may skip | Row-locked counter inside the issuing transaction |
| Printed as | "Order reference" | "Invoice No." / "Tax invoice No." / "Credit note No." |

The invoice prints the order reference as an ordinary reference field. **The order confirmation email
must never be titled "invoice" or "tax invoice"**, because a document with a gap-tolerant number would
then masquerade as a tax document. A copy review of the confirmation template belongs to phase 1.

---

## 5. When documents are issued — money-semantics mapping

**Recommended trigger: payment capture** (§ 12 D3). Storefronts are prepaid. Under EU Art. 65, UK
tax-point rules and AU attribution rules, the payment is the first chargeable event, so the invoice is
issued for the full prepaid amount when the order is confirmed (`OrderConfirmed` is published on capture)
and dated the capture date. `SupplyDate` is left empty. The payment date is shown, and the document is
marked as covering a prepaid supply where the profile requires that wording.

| Money event (source of truth) | Tax document | Lines and amounts | Idempotency key (`Source`) |
|---|---|---|---|
| Order confirmed / captured (`OrderConfirmed`, extended — § 6.2) | **Invoice** (or Simplified per profile) | Each order line: list unit price, its allocated discount (ADR-0055 `DiscountMinor`, the promotion + storefront-wide share), net, rate, tax. Plus a shipping line. **Totals equal `Order.GrossMinor` and `Order.TaxMinor` exactly.** Applied promotion names and coupon code (ADR-0051/0052) as notes. | `Order:{orderId}` |
| Zero-value confirmed order (100% discount, free item) | Invoice with zero totals (configurable per profile, § 12 D14) | Same | `Order:{orderId}` |
| Checkout cancelled / failed / expired before capture | **Nothing.** No sale was made, consistent with ADR-0056's "whether the sale ever happened". | — | — |
| **Subscription renewal** settled (`SubscriptionRenewalSettled` — new, Payments) | **Invoice** per period | List price − only the discounts flagged `AppliesToRenewals` (ADR-0057); the store-wide discount never appears on a renewal invoice. The period dates are the supply period. | `Renewal:{subscriptionId}:{sequence}` |
| Usage overage / auto-load charge settled (new event, Payments/Usage) | **Invoice** | One line per meter: quantity × unit price; tax per the profile | `Usage:{reference}` |
| **Full refund** (`RefundCompleted` with `FullyRefunded`) | **CreditNote** for everything still creditable on the invoice | Mirror of every remaining line, with the same rates | `Refund:{refundId}` |
| **RMA / line-based partial refund** (`RefundCompleted` + appended line attribution) | **CreditNote** | The returned quantity of each line, at the line's **discounted** value (ADR-0055 `RefundableMinor`), and its tax at the line's original rate. Shipping is credited only when the refund includes it. | `Refund:{refundId}` |
| **Amount-only admin refund** (goodwill / price reduction, no lines) | **CreditNote** ("price reduction") | The amount is spread across the original invoice's rate buckets in proportion to their gross, with the same largest-remainder rule as ADR-0051/0055. Net and tax per bucket. Capped at the invoice's remaining creditable gross. | `Refund:{refundId}` |
| Refund **failed** (`RefundFailed`, ADR-0055) | **Nothing** | — | — |
| **Chargeback** (`PaymentChargedBack`) | **No automatic credit note** (§ 12 D11). The invoice is flagged `Disputed`. | In most VAT systems a chargeback is *non-payment* — bad-debt relief under Art. 90 or the national equivalent — not a reduction of the supply. Goods returned → the operator issues a CreditNote; dispute won → nothing; dispute lost with goods kept → bad-debt handling, which is out of scope here. | `Chargeback:{paymentIntentId}` when the operator credits |
| Coupon redemption stays confirmed after refund (ADR-0056) | No effect on documents | The original invoice keeps showing the discount, and the credit note credits the *discounted* value. Consistent. | — |
| Refund event consumed **before** the invoice exists (async race) | CreditNote request **parked** (`PendingCreditNote`), issued automatically the moment the invoice commits | Prevents both loss and a credit note with no original | `Refund:{refundId}` |

**Caps (never credit more than was invoiced).** Per original invoice, the service tracks what remains
creditable per line (quantity and amount) and per rate bucket. A credit-note request above the remainder
is clamped to it and the excess is reported. The excess cannot normally happen, because Payments already
refuses refunds above the captured remainder (ADR-0055), but the cap is enforced again here.

**Reconciliation identity (new, checked nightly).** Per (storefront, currency, day):
`Σ invoice gross − Σ credit-note gross = ledger sales − ledger refunds` for the same source events, and
`Σ invoice tax − Σ credit-note tax = movement on tax.store-{id}`. A mismatch is a defect alarm, on the
pattern of ADR-0017's ledger ↔ Stripe ↔ Xero reconciliation.

---

## 6. Ownership, events, storage, delivery

### 6.1 A new `Invoicing` service (recommended — § 12 D7)

A new DB-owning service, `invoicing` schema, gateway `/api/invoicing`, port **5115**, built to the
ADR-0030 checklist: named schema, EF outbox entities, unique consumer class names (ADR-0060),
`services.sh` + CI matrix + migrator + init-databases + gateway routes, and an `InitialCreate` migration.
**Port note:** 5114 is already taken by the Notifications worker (the `EDGE` list in
`scripts/lib/services.sh`). The DB-owning services use 5101–5113. **5115 is the next free port**, and it
was verified unused across `scripts/`, `deploy/`, compose and gateway config on 2026-10-11.

Why a new service rather than a module:

* **A different domain, with a different lifecycle and different reasons to change.** It is driven by
  tax law and jurisdiction timetables, not by checkout UX (Ordering) or payment rails (Payments).
* **Different dependencies and retention.** PDF/XML rendering libraries, clearance-network clients and
  10-year WORM retention do not belong in Payments: Payments holds PSP secrets, and its blast radius
  should stay small. They do not belong in Ordering either, which owns the hot checkout path.
* **Facts arrive from three owners.** Ordering (sales), Payments (renewals, usage, refunds, chargebacks)
  and Support (RMA lines via refunds). Putting the documents in any one of them makes the other two
  publish into a service that does not own the concept.
* **Cost acknowledged.** It is a 14th service: the kind-deploy capacity note in ADR-0030 applies, and
  Invoicing joins `values.yaml`, not the 7-service `values-dev.yaml` baseline.

*Alternative (§ 10): a module inside Payments.* It is cheaper to stand up and sits next to the ledger and
renewals, but it mixes legal-document concerns into the money service. Acceptable only if the owner wants
to defer the 14th service; the data model and seams in this ADR are unchanged either way, so extraction
would later be a move, not a redesign.

### 6.2 Events (all contract changes additive, per the `BuildingBlocks.Contracts` rule)

**In:**

| Event | Producer | Change |
|---|---|---|
| `OrderConfirmed` | Ordering | **Append** (all defaulted): `StorefrontId`, `PublicOrderNumber`, `NetMinor`, `DiscountMinor`, `ShippingMinor`, `TaxMinor`, `TaxInclusive`, `TaxJurisdiction`, `TaxScheme` (Domestic / OSS / IOSS / ReverseCharge / IntraEuExempt / Export / OutOfScope), `BillTo?`, `Buyer?` {kind, name, taxId, validationRef}, `ShippingTax` {rateBps, category, taxMinor}, `CapturedAt`. On `OrderLineInfo`, append `LineId`, `TaxCategory`, `TaxRateBps`, `TaxMinor`, `ExemptionReason?`. A dedicated `OrderTaxFactsRecorded` event is the fallback if the owner prefers not to grow `OrderConfirmed`, but appending is the established pattern (ADR-0055). |
| `RefundCompleted` / `RefundRequested` | Payments / Support / Admin | **Append** `Lines?` [{LineId, Quantity, AmountMinor}], `ShippingMinor?`, `Kind?` (Return / PriceReduction / Shipping / Cancellation). Absent ⇒ amount-only credit (proportional). |
| `PaymentChargedBack` | Payments | Unchanged (flags the invoice) |
| `SubscriptionRenewalSettled` (**new**) | Payments | `{TenantId, StorefrontId, SubscriptionId, Sequence, PeriodStart, PeriodEnd, Currency, Lines with tax facts, SettledAt}` |
| `UsageChargeSettled` (**new**) | Payments | `{…, Reference, Meter, Quantity, UnitPriceMinor, tax facts}` |
| `StorefrontConfigChanged` | Catalog | Append `TaxJurisdiction` (country / region) and `SellerEstablishmentKey` → projected into `StorefrontInvoicingCopy` |
| `LegalEntityChanged` (**new** or extended) | Entity | Seller legal name, address and tax identifiers (by scheme) → `SellerCopy` |

**Out:**

| Event | Consumers |
|---|---|
| `TaxDocumentIssued {DocumentId, TenantId, StorefrontId, Type, Number, OrderId?, BuyerEmail, Language}` | Notifications (email with link), Ordering (shows the invoice number on order detail), Audit |
| `StorefrontInvoicingReadinessChanged` | Catalog (extends the ADR-0043 go-live gate) |
| `AuditEntryRecorded` | Audit (admin mutations) |

**Idempotency.** `UNIQUE (TenantId, SourceKind, SourceId)` on documents; every consumer is redelivery-safe;
a duplicate rolls back its whole transaction, number included (§ 4.2).

### 6.3 Rendering and formats

* The **canonical data model is EN 16931-shaped** from day 1: the tax categories, BT-codes and party
  structure on the snapshot. Every later structured format is then a *serialisation*, not a remodel.
* **Phase 1:** a human-readable **PDF** (B2C and B2B), rendered from the frozen snapshot. It is
  deterministic: same snapshot, same layout version, same bytes. Its SHA-256 is stored on the document.
* **Phase 3:** **UBL 2.1 / CII** serialisers; **Peppol BIS Billing 3.0** (BE, DE B2B; also AU/NZ, which
  use Peppol); **Factur-X / ZUGFeRD** hybrid PDF/A-3 (FR/DE); then national clearance adapters (IT
  FatturaPA via SdI, PL KSeF FA(3), FR via a *Plateforme Agréée*, ES VeriFactu records) behind **one**
  `ITaxDocumentChannel` seam. The seam is introduced in phase 3, alongside its second real implementation,
  so it is not speculative.
* **Build the documents, buy the transmission** (§ 12 D9). A Peppol Access Point and national clearance
  connections need certification. A provider adapter is the realistic path; owning the document and
  numbering remains in-house.
* **New dependency:** a PDF library is needed. Candidates: QuestPDF (community licence is free below
  US$1M annual revenue, then commercial — a licence decision like MassTransit's), PdfSharp/MigraDoc (MIT),
  or HTML→PDF through headless Chromium (heavy). The owner decides under the dependency rule (§ 12 D16).

### 6.4 Storage, integrity, retention

* Artifacts go to `IObjectStore` under `StoredObjectKey.For(tenantId, "tax-documents", documentId, "{number}.pdf|.xml")`.
  In production the bucket uses **object lock / WORM (compliance mode)**, with the retention set from
  the profile.
* **Retention** = the max of every applicable profile's period. The default is **10 years** from the end
  of the year of issue. A purge job runs after expiry; it is never manual.
* **GDPR.** Erasure requests do **not** delete tax documents. Retention is a legal obligation (GDPR
  Art. 17(3)(b)). Other services pseudonymise as today; documents are purged at retention expiry.
* Integrity rests on the per-series **hash chain** (§ 4.2), the immutable content columns and the stored
  artifact hashes. Together they form the "business controls creating a reliable audit trail" option of
  Art. 233.
* **Authority access.** An admin export of the document register (CSV, plus the artifacts as a zip) per
  period. A SAF-T style export is a later, per-country item.

### 6.5 Admin, audit, RBAC

* New permissions in `PermissionRegistry`: `invoicing.documents.read`, `invoicing.documents.credit`
  (manual credit note), `invoicing.series.manage`, `invoicing.profiles.manage`. They are documented in
  `docs/help/roles-permissions.html` per the sync rule.
* **Every admin mutation emits audit** in the same unit of work (the mt6_1 invariant): series
  create/close/prefix change, profile overrides, manual credit notes, re-send, and marking a dispute
  credit. System issuance is recorded by the document's own hash chain plus `TaxDocumentIssued`.
* Admin screens: a document register (search by number, order, buyer, date, status; download; re-send),
  series management, a per-storefront invoicing-readiness checklist, and the reconciliation report.

### 6.6 Delivery

* Notifications consumes `TaxDocumentIssued` and sends an email in the document's language, with a
  **link**. Account holders get it on the order page (authenticated download through the gateway).
  Guests get a signed, expiring, single-document token link. `EmailMessage` gains no attachment support in
  phase 1 (§ 12 D15). The delivery-log reference is `tax-document:{id}` (`NotificationReferences`).
* Storefront: an "Invoices & credit notes" section on the order detail page, plus a "Request invoice
  details" form for adding a business name and tax ID **before issue only**. After issue, any change
  means a credit note and a re-issue, never an edit.

### 6.7 Relationship to Xero (§ 12 D-Xero)

* **Recommended:** keep ADR-0017 exactly as is — ledger-sourced nightly summary journals plus per-refund
  postings. Invoices are **not** pushed to Xero, which respects decision #17's rate-limit and bloat
  reasons. The VAT and OSS return needs per-rate and per-country totals, so the nightly journal (or a
  companion export) is built from the **invoice register's per-rate/per-country summary**, reconciled to
  the ledger by the identity in § 5.
* *Alternative:* push each invoice to Xero as an `ACCREC` invoice. Rejected for the reasons in
  ADR-0017 (60/min, 5k/day; Xero downtime coupling).
* *Alternative:* a weekly register CSV for the accountant only. A fallback if a summary-journal change is
  unwanted.

---

## 7. Jurisdiction model

Two seams with two jobs. Keep them apart.

### 7.1 Tax computation — `ITaxStrategy` (Ordering): *what is charged*

Evolve the existing seam from `long TaxFor(input)` to a **breakdown**:

```
TaxBreakdown TaxFor(TaxCalculationInput input)
  input  += Jurisdiction (storefront establishment), BuyerKind, BuyerTaxId (validated), ShipTo, per-line TaxCategoryCode, shipping
  output  = per-line { Category, RateBps, TaxableBase, Tax, ExemptionReason? }, shipping line, per-rate summary,
            Scheme (Domestic | OSS | IOSS | ReverseCharge | IntraEuExempt | Export | OutOfScope), TaxInclusive
```

* Checkout calls it **once**, persists the per-line facts on `OrderLine`/`Order` (new columns), and
  publishes them. The invariant `Σ line tax + shipping tax == Order.TaxMinor` is enforced with
  largest-remainder rounding, the same rule as ADR-0051/0055.
* Implementations: today's single-rate logic becomes `StorefrontRateTaxStrategy` (behaviour-preserving).
  An **EU OSS-aware strategy** (destination rates by country and category; the home rate below the
  €10,000 threshold; reverse charge / intra-EU exemption for validated B2B) is the prerequisite for EU
  cross-border B2C (§ 9, P1; § 12 D17). The PRD already names "Stripe Tax or OSS-aware `ITaxStrategy`"
  as a future item; this ADR makes it a precondition of EU cross-border invoicing.
* Renewals and usage need the same facts (§ 9, P4). The subscription snapshots its tax *terms*
  (jurisdiction, category, inclusive, buyer kind) at purchase, and the **rate in force at renewal** is
  resolved from a projected tax-rate table, because rates change by law. On an **exclusive** regime the
  renewal/usage tax is **added to the charge**, as checkout adds it to the first period (owner decision
  2026-10-15, option A — ADR-0057 amendment); on an inclusive regime it is contained in the charge.

### 7.2 Invoicing rules — `JurisdictionProfile` (Invoicing): *what the document must say and when*

Data-driven and versioned, with `EffectiveFrom` / `EffectiveTo`. The code-defined baseline ships in the
repo, with tenant overrides in the DB (audited):

```
JurisdictionProfile { Code ("EU-DE", "EU-FR", "AU", "NZ", "GB", "CA", "CH", "US", "GENERIC"), Version, EffectiveFrom
  DocumentTitle per type & language ("Rechnung", "Facture", "Tax invoice", "Credit note"…)
  IssuePolicy: Always | OnRequest | Never            · IssueTrigger: Capture | Supply | Request
  IssueDeadline (e.g. +15th next month for intra-EU B2B; 30d UK; 28d after request AU/NZ; 10d ViDA)
  SimplifiedThreshold (amount, currency, inclusive?) · BuyerIdentityThreshold (AU A$1,000; NZ $1,000; CA $500)
  RequiredFields (bitset over the snapshot: seller tax id, buyer tax id when B2B, supply date, unit net price…)
  NumberingPolicy: gapless (always) · ResetPeriod · SeparateCreditNoteSeries · MaxLength · Format
  LocalCurrencyVat: required? which currency? (EU Art. 230 national currency; GB GBP) · RateSource (ECB…)
  LanguageRule: storefront default | must include national language (bilingual labels)
  LegalNotes: reverse-charge text, exemption texts, OSS/IOSS notes, "Includes GST"
  RetentionYears · Channel: PdfEmail | Peppol | SdI | KSeF | FrPa | VeriFactu (phase 3)
}
```

* **Resolution.** The storefront's `TaxJurisdiction` and seller establishment, together with the sale's
  scheme, select the profile. **OSS / IOSS ⇒ the Member State of identification's profile** (Art. 219a).
  **Reverse charge ⇒ the supplier's Member State profile.**
* **Rest of world** uses `GENERIC`: full invoice, gapless, seller tax ID if one is configured, 10-year
  retention, PDF. It is safe-by-default and overridable once an adviser confirms local rules.
* `ITaxStrategy` stays the only *tax* seam. The profile is configuration, not a new speculative
  interface. The only new interface is the phase-3 `ITaxDocumentChannel`.

### 7.3 Configuration per tenant / storefront

* **Entity (seller):** the legal entity gains tax identifiers by scheme (`EuVat`, `GbVrn`, `AuAbn` (exists),
  `NzGst`, `CaBn`, `ChUid`, `UsStateSalesTaxId`, `Other`) and **establishments** (address + jurisdiction),
  with VIES verification for EU VAT IDs.
* **Catalog (storefront):** `TaxJurisdiction` (ISO country, optional region) + `SellerEstablishmentKey`.
  Projected to Ordering (tax) and Invoicing (profile).
* **Go-live gate (extends ADR-0043):** a storefront in a jurisdiction whose profile has
  `IssuePolicy ≠ Never` cannot be activated unless the seller has a tax ID for that jurisdiction (when
  registered), a profile resolves, and the invoice language is supported. Invoicing projects the
  readiness to Catalog, on the carrier/payment readiness pattern.

---

## 8. B2B, multi-currency, rounding

* **Buyer tax ID capture** (optional field at checkout: "Business purchase? Company name + VAT/ABN/GST
  number"). EU VAT IDs are validated against **VIES** at checkout. The VIES consultation number is stored
  as evidence on the order and the invoice. If VIES is unavailable, the sale falls back to B2C with VAT
  rather than to an unverified reverse charge (safe direction). **Reverse charge and the intra-EU
  exemption are computed by the tax strategy at checkout** (the charged amount differs), and the invoice
  only prints the scheme's legal note and the buyer's VAT ID. Phase 2 (§ 12 D5).
* **Document currency** = the order currency, which is the storefront currency (ADR-0038/0059).
* **Local-currency VAT (Art. 230 / UK).** When the profile's national currency differs from the document
  currency, the document carries `LocalCurrencyVat` — the VAT total converted at the Art. 91 rate (ECB
  reference rate of the issue date, or the profile's rate source), with the rate, its date and its source
  printed. This is the first real FX dependency on the platform. It is scoped to **display on tax
  documents only**: the ledger keeps its no-FX posture (ADR-0041). **Phase 1 avoids it with a gate:** a
  storefront may enable invoicing only when its currency equals its profile's national currency. A GBP
  storefront for UK VAT and an EUR storefront for a eurozone establishment pass; a USD storefront under a
  German establishment waits for phase 2 and the FX feed (§ 12 D13).
* **Rounding.** Amounts are integer minor units with ISO-4217 exponents (ADR-0046). Tax is taken
  **verbatim** from the persisted per-line facts. For an inclusive regime, `UnitPriceNet` is derived from
  gross − tax per line, with the residue of the per-unit division held on the line net, so that
  `Σ net + Σ tax = gross` exactly on every document. The per-rate summary is `Σ` of its lines. It is
  never recomputed as `base × rate`, which could drift by one minor unit from what was charged. Where a
  profile mandates "rate × base per rate" rounding (some clearance schemas validate it), the tolerance and
  the rounding-line convention are handled in that channel's serialiser.

---

## 9. Prerequisites (must land before any document is issued)

| # | Prerequisite | Owner | Why |
|---|---|---|---|
| P1 | **Per-line tax facts persisted and published** (category, rate, base, tax, exemption; shipping line; `Σ == TaxMinor`). `ITaxStrategy` returns a breakdown (§ 7.1). Behaviour-preserving for today's single-rate stores. **Status: persisted on `feature/tax-per-line-breakdown` (2026-10-27, § 9.2)**; publishing them is P5. | Ordering | Art. 226(8)–(10): per-rate breakdown; shown == invoiced |
| P1b | **EU OSS-aware tax strategy** (or a tax engine) before **EU cross-border B2C** goes live. Before P1b a storefront charged its single rate regardless of EU destination. **Status: `EuOssTaxStrategy` on `feature/eu-oss-tax-strategy` (2026-10-28, § 16)** — standard rates only; reduced-rate categories, B2B and OSS renewals are follow-ups. | Ordering | Wrong VAT cannot be made right by the invoice |
| P2 | **Seller identity**: Entity tax-ID schemes + establishments; storefront → jurisdiction + establishment binding | Entity, Catalog | Art. 226(3),(5); profile resolution |
| P3 | **Buyer identity**: bill-to address (defaults to ship-to), optional business name + tax ID at checkout. **Status: captured on `feature/checkout-bill-to-and-buyer-tax-id` (2026-10-28, § 16)**; the account-profile default business identity is deferred (`tax_p3b_profile_business_identity`). | Ordering, Storefront | Art. 226(4),(5); AU ≥ A$1,000 |
| P4 | **Renewal and usage charges compute and post tax** (today: tax 0 — an output-tax under-report in inclusive regimes, independent of invoicing) and publish the settled events. **Status: tax half done on `fix/renewal-and-usage-tax` (2026-10-15)** — inclusive regimes post checkout's split at the rate in force; exclusive regimes ADD checkout's tax to the charge and post net + tax (owner decision 2026-10-15, option A, `fix/exclusive-tax-on-renewals-and-usage`; see the ADR-0057 amendments); the settled-event half is **done in P4b** (`feature/settled-charge-events`, 2026-10-28, § 16). | Payments | Correct VAT; renewal and usage invoices |
| P5 | Additive contract changes (§ 6.2) and `LineId` on `OrderLineInfo` | Contracts | Credit-note line attribution |

### 9.1 Phase-0 task breakdown (dependency order — each is its own branch and PR)

Each task follows ADR-0058: it is cut from `develop`, tested on its own, squash-merged on green gates, and
tracked as its own row in `.ai-shared/plans/plan_status_executions.md` (`tax_p*`). Tasks on the same row
of the graph can run in parallel. **No task issues a tax document.** Every task is behaviour-preserving
for existing orders, and trial balance stays 0.

```
P2 seller identity ─┐
                    ├─► P3 buyer identity ─┐
P1 per-line tax ────┘                      ├─► P5 contract facts ─► (phase 1: Invoicing service)
P4 renewal/usage tax (in progress) ─► P4b settled-charge events ─┘
P1 + P2 ─► P1b EU OSS tax strategy   (needed for EU cross-border B2C only; not on the AU/phase-1 path)
```

| Order | Task (branch) | Scope | Depends on | Acceptance criteria |
|---|---|---|---|---|
| 1a | **P2 — `feature/tax-seller-identity`** | Entity: tax-ID schemes `EuVat`, `GbVrn`, `NzGst`, `CaBn`, `ChUid`, `UsStateSalesTaxId` (beside `Abn`/`Acn`/`Gst`/`OtherTaxRegistration`) and seller **establishments** (address + jurisdiction) on the `TenantOwner` entity; publish `LegalEntityChanged`. Catalog: `Storefront.TaxJurisdiction` (ISO-2 + optional region) and `SellerEstablishmentKey`, cloned on duplication, appended to `StorefrontConfigChanged` (defaults), and projected to Ordering. Admin forms (localized; tooltips). Audit on every mutation. | — | Identifiers validate per scheme (format; ABN checksum kept); FORCE RLS non-superuser test; `StorefrontConfigChanged` replay without the new fields still projects (defaults); duplication copies jurisdiction; audit entry + `AuditEntryRecorded` per mutation; API contracts + `docs/help` updated. |
| 1b | **P1 — `feature/tax-per-line-breakdown`** | `ITaxStrategy` returns `TaxBreakdown` (per-line category, rate, base, tax, exemption; shipping line; per-rate summary; scheme; inclusive). Today's logic moves to `StorefrontRateTaxStrategy` (**behaviour-preserving**). `CheckoutEndpoints` calls the strategy instead of computing tax inline. New columns on `OrderLine`/`CheckoutAttemptLine` (`TaxCategory`, `TaxRateBps`, `TaxableBaseMinor`, `TaxMinor`, `ExemptionReason`) and `Order` (shipping tax, `TaxScheme`). The per-line category comes from product taxability (`TaxMode`, ship rules). | — (jurisdiction input optional until P2) | `Σ line tax + shipping tax == Order.TaxMinor` on **every** checkout path (inclusive and exclusive storefronts, promotions, store-wide discount, coupons, ship-rule exemptions, collect-at-warehouse, zero-value) — property test plus the existing `StorefrontTaxScopingTests`/`MoneyFlowTests`/`RmaRefundBasisTests` unchanged and green; the charged gross is byte-identical to before for every existing test; largest-remainder rounding with ISO exponents 0/2/3; migrations with defaults and `dotnet format` on Infrastructure. |
| 2 | **P3 — `feature/checkout-bill-to-and-buyer-tax-id`** | Checkout accepts an optional **bill-to** address (defaults to ship-to) and an optional **business purchase** block (company name + tax ID + scheme); persisted on `CheckoutAttempt`/`Order`; storefront checkout UI (localized, accessible); the account profile can store a default business identity. **No reverse charge** and no change to the amount charged (phase 2). Format validation only (VIES in phase 2). | P1 (input shape), P2 (scheme vocabulary) | A guest and a member can check out with and without a business block; the amount charged is unchanged with a buyer tax ID present; invalid formats → 400 problem+json naming the field; bill-to defaults to ship-to when absent; Playwright checkout spec covers both; screenshots via `capture()`. |
| 3a | **P4 — renewal/usage tax** *(tax half done — `fix/renewal-and-usage-tax` (inclusive) + `fix/exclusive-tax-on-renewals-and-usage` (exclusive: tax added to the charge, owner decision 2026-10-15); ADR-0057 amendments)* | Renewals and usage overage/auto-load compute tax from the subscription's or storefront's terms and post it to `tax.store-{id}`. Today they post tax 0. | — | Owned by that PR. For this ADR: the renewal/usage ledger tax equals the tax a checkout would have charged on the same amount in the same storefront; trial balance 0. |
| 3b | **P4b — `feature/settled-charge-events`** *(done 2026-10-28, § 16)* | Payments publishes `SubscriptionRenewalSettled` and `UsageChargeSettled`, carrying the P1-shaped tax facts and period/meter data, through the outbox in the same unit of work as the ledger posting. | P4, P1 | Exactly one event per settled renewal or charge (redelivery-safe; keyed `renew-{id}-{seq}` / usage reference); no event for Failed/RequiresAction; contract tests; consumer-name uniqueness test (ADR-0060) still green. |
| 4 | **P5 — `feature/invoicing-contract-facts`** | **Append** to `OrderConfirmed` (`StorefrontId`, `PublicOrderNumber`, Net/Discount/Shipping/Tax, `TaxInclusive`, `TaxJurisdiction`, `TaxScheme`, `BillTo`, `Buyer`, shipping tax, `CapturedAt`) and to `OrderLineInfo` (`LineId`, `TaxCategory`, `TaxRateBps`, `TaxMinor`, `ExemptionReason`). Append `Lines`/`ShippingMinor`/`Kind` to `RefundRequested`/`RefundCompleted`; the Support RMA path and the admin refund endpoint populate them. | P1, P3 (the facts exist to publish) | Every new field has a default; a pre-change message deserializes with defaults (back-compat test, on the ADR-0055 pattern); existing consumers (Fulfillment, Notifications, Support, Entitlement) unchanged and green; an RMA refund carries its lines and an admin amount refund carries none; `e2e-verify.sh` A-group updated. |
| 5 | **P1b — `feature/eu-oss-tax-strategy`** | An EU strategy with destination rates per country and category; the home rate below the €10,000 EU-wide threshold (tracked per tenant); scheme = OSS above it. The rate table is versioned with effective dates. **Not on the phase-1 AU path; required before EU cross-border B2C goes live.** | P1, P2 | A known-rates fixture for every EU-27 standard rate; a below-threshold sale charges the home rate; an above-threshold sale charges the destination rate with scheme OSS; a rate change takes effect on its date; inclusive storefronts charge the shelf price unchanged (ADR-0038), with tax extracted at the destination rate. |

### 9.2 Implementation notes

**P1 — per-line tax breakdown (2026-10-27, `feature/tax-per-line-breakdown`).**

* **The seam.** `ITaxStrategy.TaxFor(TaxCalculationInput) → TaxBreakdown` (`Ordering/Domain/Tax.cs`). The input is
  per line — listed value, the line's share of the discount (promotion allocation + storefront-wide share, both
  largest remainder as checkout already split them), and an exemption reason — plus the shipping charged. The
  output is one `LineTax { Category, RateBps, TaxableBaseMinor, TaxMinor, ExemptionReason }` per line (index-aligned)
  and one for shipping, the `Scheme`, `TaxInclusive`, and a derived per-rate summary. `TaxBreakdown.TaxMinor` **is**
  `Σ line tax + shipping tax`, so the invariant holds by construction, and checkout charges exactly that number.
* **Behaviour-preserving.** `StorefrontRateTaxStrategy` is checkout's old inline code, moved: the goods base is
  the destination-taxable lines less their discount, clamped at their subtotal as one aggregate; shipping joins
  the base only when some goods are taxable; **one** rounding of the whole base (inclusive `× bps/(10000+bps)`,
  exclusive `× bps/10000`, half away from zero). Only then is that total split over the taxable lines and the
  shipping line by largest remainder (ties to the earliest component). Rounding per line would drift by a minor
  unit (3 × 333 at 10 % gives 99 per line vs 100 charged), which is why the split comes after the rounding. The
  arithmetic moved from `double` to `decimal`; a unit sweep over every rate step 0..10000 proves the two
  identical, and 20,000 seeded random carts reproduce the old inline result exactly (`TaxBreakdownTests`).
* **Facts persisted.** `CheckoutAttemptLine`/`OrderLine`: `TaxCategory`, `TaxRateBps`, `TaxableBaseMinor`,
  `TaxMinor`, `ExemptionReason`. `CheckoutAttempt`/`Order`: `ShippingTaxCategory`, `ShippingTaxRateBps`,
  `ShippingTaxableBaseMinor`, `ShippingTaxMinor`, `ShippingExemptionReason`, `TaxScheme`, `TaxInclusive`. `ToOrder`
  copies them verbatim. Migration `PerLineTaxBreakdown` adds them with defaults 0/false; **pre-P1 rows read
  `TaxCategory.NotRecorded` / `TaxScheme.NotRecorded`** and are not backfilled (D18). Enums cross the wire as numbers;
  append members only.
* **Base convention.** `TaxableBaseMinor` is **always net of the tax**: on an exclusive store the discounted amount
  the tax was added to; on an inclusive store the discounted shelf amount less the tax it contains, so
  `base + tax` = what the shopper paid for that component. An exempt line's base is its discounted amount.
* **Categories and reasons today.** `Standard` (the storefront's rate, possibly 0 %) or `Exempt` with
  `DestinationShipRule` (the product's ship rule for the ship-to country says `ChargeDestinationTax = false`,
  ADR-0050); an all-exempt cart's shipping is `Exempt` / `ShippingFollowsExemptGoods`. The scheme is always
  `Domestic`; P1b appends reduced categories and the other schemes. `ProductCopy.TaxMode` is **not** consulted: it is
  never projected (always `Exclusive`), and honouring it would change amounts — it remains the pricing engine's
  input only (`ProductTaxMode`).
* **Scope held.** No contract and no HTTP response changed (P5 publishes the facts). Untouched and unchanged in
  amount: promotion/coupon evaluation, `GET /cart/summary` (computes no tax), the subscription "renews at" preview
  (`taxAddedOnRenewal` reads the storefront copy only), Payments renewals/usage (`RecurringChargeTax`, the same
  rounding), refunds and the RMA basis (`OrderLineDiscounts`, `OrderConfirmed` unchanged), and a second storefront
  sharing the currency (the strategy is built from **this** storefront's copy, ADR-0055).
* **Known pre-existing quirk, preserved.** The taxable discount is clamped on the taxable subtotal as a whole. A
  line discounted past its own value (a stacked promotion plus the storefront-wide share) therefore pushes the
  excess onto the other taxable lines; the per-line bases are then the aggregate base spread over the lines' own
  discounted amounts. An exempt line discounted past its value does **not** spill into the taxable base, exactly as
  before.

---

## 10. Alternatives considered

| Option | Verdict |
|---|---|
| **Use the order number as the invoice number** | **Rejected.** It is allocated before payment, it is gap-tolerant by the owner's decision, and it is per storefront rather than per seller. It would breach FR/ES and the strict reading of Art. 226(2). |
| **Postgres `SEQUENCE` per series** | **Rejected.** Non-transactional: gaps on every rollback or redelivery. |
| **Allocate the number, then render/transmit, then commit** | **Rejected.** Any downstream failure leaves a gap or a dangling number. |
| **Issue on shipment (supply date)** | Viable, and cleaner where a supply-date invoice is preferred. But the payment already triggered the chargeable event (Art. 65), so a payment-on-account invoice would then be needed anyway, and digital/subscription lines never ship. Offered as an owner option (§ 12 D3). |
| **Invoices inside Payments** | Viable fallback (§ 6.1). It keeps the service count at 13, but couples legal-document rendering and retention to the money service. |
| **Invoices inside Ordering** | **Rejected.** Renewals, usage, refunds and chargebacks originate in Payments, and rendering does not belong on the checkout hot path. |
| **Outsource the whole thing** (Stripe Invoicing, Avalara, Fonoa, Xero invoices) | **Rejected as the system of record.** It conflicts with the learning goal and with the ledger as truth, adds per-document fees, and (for Xero) runs into ADR-0017's rate limits. **Accepted for transmission** (Peppol AP / clearance providers) in phase 3. |
| **Per-order invoices pushed to Xero** | **Rejected** — PRD decision #17 / ADR-0017 stand. |
| **Credit note = negative invoice in the invoice series** | **Rejected.** Spain requires a separate rectifying series, and a separate type with a mandatory original reference is clearer everywhere. |

---

## 11. Phased rollout

| Phase | Scope | Legally safe for | Exit criteria |
|---|---|---|---|
| **0 — Prerequisites** | P1, P2, P3, P4, P5 (§ 9). No documents yet. | — | Per-line tax facts sum to `TaxMinor` on every checkout path; renewals and usage post tax; trial balance 0 |
| **1 — MVP** | `Invoicing` service; Invoice / SimplifiedInvoice / CreditNote; gapless series; hash chain; PDF; email link + account download; admin register, series, readiness gate, reconciliation report; profiles **`EU-BASE`** (the common EU rule set straight from the VAT Directive: Art. 226 full invoice, Art. 220a/226b simplified at €100, Art. 219 credit notes, the strictest numbering reading, domestic B2C + domestic B2B *with* VAT, printing the buyer's VAT ID when given), **`AU`** (tax invoice, ABN, A$1,000 buyer identity), **`GENERIC`**. The **home Member State is configuration, not code**: once the company is registered, an `EU-<MS>` profile is derived from `EU-BASE` with that state's overrides (simplified threshold up to €400, language, retention, deadlines, numbering wording) and bound to the establishment. No engine change is needed; renewal and usage invoices; refunds/RMA → credit notes; chargebacks flagged **AU** (B2C and B2B) from day one; **EU domestic B2C/B2B-with-VAT** in the home Member State once it is registered and its `EU-<MS>` override is configured and adviser-signed (until then EU storefronts stay behind the readiness gate). **EU cross-border B2C (OSS)** only once P1b is live: the invoice then follows the Member State of identification's profile, which phase 1 already is | All acceptance criteria (§ 13); profile sign-off by an adviser |
| **2 — B2B and more jurisdictions** | VIES validation; reverse charge / intra-EU exempt supply; the 15th-of-month deadline; ECB FX for Art. 230 / UK sterling; profiles `GB`, `NZ`, `CA`, `CH`, plus additional EU Member States | Cross-border EU B2B; UK; NZ; CA; CH | VIES evidence stored; reverse-charge notes; local-currency VAT printed |
| **3 — Structured e-invoicing** | EN 16931 UBL/CII; Peppol BIS 3.0 via an AP provider (BE, DE B2B, AU/NZ); Factur-X/ZUGFeRD; clearance adapters (IT SdI, PL KSeF, FR PA, ES VeriFactu) behind `ITaxDocumentChannel`; clearance status and rejection handling | National mandates. **Must precede selling B2B in BE (live now), PL (live), FR (Sep 2026/27), DE (2027/28), ES VeriFactu (2027) and IT (any invoice)** | Per-channel conformance tests against the provider sandbox |
| **4 — Later** | ViDA cross-border B2B digital reporting (**1 Jul 2030**); marketplace/deemed-supplier model (if pivoted); self-billing; US state sales-tax receipts; other clearance countries (MX, BR, IN, SA, TR, MY) | — | — |

> **Jurisdiction gating is the safety net.** The readiness gate (§ 7.3) refuses to activate a storefront
> whose jurisdiction has no signed-off profile or whose mandatory channel is not built — for example an
> Italian establishment before the SdI adapter. A tenant cannot drift into a non-compliant market by
> configuration.

### Explicitly out of scope (this ADR)

Tax *calculation* engines beyond what P1/P1b need; bad-debt relief filings; VAT/OSS return filing (the
register export feeds it, but filing stays in Xero or with the accountant); supplier→tenant purchase
invoices (accounts payable); the platform's own SaaS invoices to tenants; marketplace/deemed-supplier and
self-billing; retroactive invoices for orders confirmed before go-live (the platform is on test rails
with no live customers — § 12 D18); and paper invoices.

---

## 12. Owner decisions

> **Recorded 2026-10-11.** The owner **accepted every recommendation below (D1–D18 and D-Xero) as
> written**, with one amendment to **D2**: the home EU Member State is not decided yet and stays
> configuration (see D2 and D13). The *Options* column is kept as the record of what was considered.

| # | Decision | Options | Recommendation |
|---|---|---|---|
| **D1** | **Seller of record in dropship/supplier flows** | (a) Always the tenant — reseller model; the supplier invoices the tenant. (b) The supplier, when it sells in its own name — marketplace/agency, which can trigger deemed-supplier rules (EU Art. 14a, AU Div 84-C, NO VOEC, US marketplace facilitator). (c) Per offer. | **(a).** (b)/(c) is the "Marketplace capabilities" pivot the PRD already flags as a ~3× scope expansion. |
| **D2** | **Launch jurisdictions** | EU one Member State only / EU + OSS B2C / + AU / worldwide via GENERIC **ACCEPTED (owner, 2026-10-11), as amended:** the home EU Member State is **not decided** and stays **configuration**. Phase 1 ships the engine + **AU** + the common **`EU-BASE`** rule set; the home state's `EU-<MS>` profile is configured once the company is registered (PRD App. B #1). EU OSS B2C follows as soon as P1b lands; everything else waits behind the readiness gate. |
| **D3** | **Issue trigger** | (a) Payment capture / order confirmed. (b) Shipment/supply. (c) On request only (AU/NZ allow). | **(a).** It matches the prepaid chargeable event, covers digital/subscription lines, and is the owner's "after order placement". |
| **D4** | **Always issue, or only on request (B2C)** | Always / on request / per profile | **Always**, simplified where allowed. Safer under Art. 221 national B2C rules and simpler UX. |
| **D5** | **B2B at launch** | (a) Capture and print the buyer's tax ID, VAT charged (no reverse charge). (b) Full reverse charge + VIES. (c) No B2B. | **(a) in phase 1, (b) in phase 2.** Reverse charge changes the charged amount and must be decided at checkout with VIES evidence. |
| **D6** | **Series granularity and format** | Per seller×establishment×type×year (default) / + per storefront / no yearly reset | **Seller × establishment × type × calendar year**; `INV{YYYY}-{000000}` / `CN{YYYY}-{000000}`; per-storefront series opt-in. |
| **D7** | **Where invoicing lives** | New `Invoicing` service / module in Payments | **New service** (§ 6.1). Module-in-Payments only if a 14th service is unwanted now. |
| **D8** | **Formats** | PDF only / PDF + UBL / hybrid Factur-X | **PDF in phase 1** with an EN 16931-shaped model; UBL/CII/Peppol/Factur-X in phase 3. |
| **D9** | **E-invoicing transmission** | Build Peppol AP + clearance clients in-house / provider adapter | **Provider adapter** (certification burden); the document and numbering stay in-house. |
| **D10** | **Invoice language(s)** | Storefront default language / buyer's language / always national language / bilingual | **Storefront default language**, with legal labels **bilingual** where the profile requires the national language (e.g. FR). Legal labels in the six supported languages need native/adviser review. |
| **D11** | **Chargeback treatment** | Auto credit note / flag + operator decides | **Flag + operator decides.** A chargeback is non-payment, not a supply reduction, in most VAT systems. |
| **D12** | **Retention and storage** | 10 y uniform / per profile; WORM or not | **Max of applicable profiles, default 10 y; WORM object lock in prod**; GDPR erasure never deletes tax documents. |
| **D13** | **FX source for Art. 230 / UK sterling** | ECB daily reference rates / provider / none (gate) **Gate in phase 1**: storefront currency must equal the national currency of the profile in force. For `EU-BASE` that is the national currency of the Member State whose rules apply, which is resolved from configuration once the home state is set. Until then, an EU storefront is gated anyway. **ECB reference rates in phase 2**, used for invoice display only (the ledger stays no-FX). |
| **D14** | **Zero-value orders** | Issue a zero invoice / no document | **Issue** (every confirmed sale has a document), switchable per profile. |
| **D15** | **Delivery** | Link / attachment / both | **Link + account download** in phase 1; attachments later if wanted. |
| **D16** | **PDF library (new dependency)** | QuestPDF (licence threshold) / PdfSharp-MigraDoc (MIT) / headless Chromium | **PdfSharp/MigraDoc** for licence simplicity, or QuestPDF with a recorded licence decision. |
| **D17** | **EU cross-border tax correctness (P1b)** | Build an OSS-aware `ITaxStrategy` (rate table per country/category) / adopt a tax engine (Stripe Tax, etc.) | **Build the OSS-aware strategy** behind the existing seam (learning value, no per-transaction fee); revisit if the category/rate catalogue becomes a burden. |
| **D18** | **Backfill for pre-feature orders** | None / issue retroactively | **None** — test rails, no live customers. Documents start at go-live. |
| **D-Xero** | **Xero relationship** | Unchanged ledger journals + register-derived per-rate summary / push invoices / CSV only | **Unchanged journals + a per-rate/per-country summary from the register** (§ 6.7). |

---

## 13. Working Brief

**Goal.** Every confirmed sale, renewal and usage charge gets a legally compliant, immutable customer
tax document, and every refund a referencing credit note. Numbered gaplessly per series. Correct for the
EU VAT Directive in the launch Member State and AU, and extensible by configuration to any jurisdiction
and currency.

**Non-goals.** Calculating tax beyond P1/P1b; filing returns; AP invoices; SaaS fees; the marketplace
model; structured e-invoicing / clearance before phase 3; editing issued documents; backfilling old
orders.

**Requirements** (proposed IDs — see the PRD diff):
- **FR-14** A confirmed order, settled renewal and settled usage charge each produce exactly one tax
  document (Invoice or SimplifiedInvoice per profile), issued at capture/settlement.
- **FR-15** A completed refund produces exactly one CreditNote referencing the original invoice. It is
  line-attributed when the refund carries lines, proportional across rate buckets otherwise, and never
  exceeds the remaining creditable amount. A chargeback flags the invoice and never auto-credits.
- **FR-16** Shoppers can view and download their tax documents (account and guest link); operators can
  search, download, re-send and export the register; series and profile changes are audited.
- **FR-17** A storefront cannot go live in a jurisdiction requiring invoices unless the seller tax ID,
  profile and language are configured (readiness gate).
- **NFR-9 (Gapless numbering)** Numbers are unique, gapless and chronological per series under
  concurrency, redelivery and crashes.
- **NFR-10 (Immutability and retention)** Issued content cannot change (DB-enforced), artifacts are
  hash-verified and retained per profile (default 10 y), and the per-series hash chain verifies.
- **NFR-11 (Reconciliation)** Invoice/credit totals reconcile to ledger sales/refunds and to the tax
  account per storefront, currency and day.

**Acceptance criteria.**
1. Invoice gross, net and tax equal `Order.GrossMinor`, `Order.NetMinor` / `TaxMinor`, and per-rate
   totals equal the per-line facts, for inclusive (AU/EU) and exclusive (US-style) storefronts, with
   promotions, store-wide discount, coupon, shipping and ship-rule exemptions.
2. 200 concurrent issues on one series, with injected failures (exceptions after the number is taken,
   duplicate redeliveries, killed connections), yield numbers `1..N` with no gaps or duplicates and
   non-decreasing issue dates; documents for failed attempts do not exist.
3. Redelivering `OrderConfirmed` / `RefundCompleted` never creates a second document or consumes a number.
4. A `RefundCompleted` consumed before the invoice exists yields a credit note right after the invoice
   commits.
5. An RMA partial return credits the line's discounted value (ADR-0055) at its original rate; an
   amount-only refund splits across rate buckets with largest remainder; a credit never exceeds the
   remainder.
6. A renewal invoice shows list price minus only the `AppliesToRenewals` discount (ADR-0057).
7. An `UPDATE` of issued content fails at the DB; the hash chain verifies; the rendered PDF hash matches
   the stored one.
8. Non-superuser RLS: tenant A cannot read or issue tenant B's documents or series.
9. The readiness gate blocks activation of a storefront without the seller tax ID or profile.
10. Nightly reconciliation reports zero mismatches on the seeded data set; trial balance stays 0 (no new
    ledger postings).

**Test / verification plan.**
- *Unit (Invoicing.Domain):* profile resolution (OSS → MS of identification; reverse charge → supplier
  MS); simplified-vs-full thresholds; buyer-identity thresholds (AU A$1,000); rounding (`Σnet+Σtax=gross`,
  ISO exponents 0/2/3); credit caps and proportional split; format rendering of numbers.
- *Unit (Ordering):* `TaxBreakdown` invariants, `Σ line tax == TaxMinor` across every checkout path
  (behaviour-preserving for the current single-rate stores).
- *Integration (Testcontainers):* the concurrency/gapless suite (criterion 2); idempotent redelivery;
  refund-before-invoice parking; renewal/usage invoices; non-superuser RLS (`IdentityUsersRlsTests`
  pattern); immutability trigger; reconciliation identity; outbox → Notifications email with link.
- *Browser E2E:* storefront order page shows and downloads the invoice and, after an RMA, the credit
  note; admin register search/download; storefront go-live gate message.
- *Live (`scripts/e2e-verify.sh --live`):* a new L-flow — order → invoice issued → RMA → credit note →
  reconciliation 0. The COVERAGE CHECKLIST and the A-group are updated per the regression-list rule.
- *Validation:* `dotnet build`, `dotnet format --verify-no-changes`, `dotnet test`, storefront
  lint/tsc/build; the CI gates per ADR-0058.

---

## 14. Proposed PRD diff (NOT applied — PRD update pending the owner's separate approval)

> Accepting this ADR does **not** apply the scope change. Per the PRD rule, the PRD is edited only once
> the owner approves this diff separately; until then the PRD stays as is.

```diff
--- docs/prd/3commerce/04-mvp-scope.md
+### Tax documents (post-MVP, ADR-0063)
+- ✅ Customer tax invoices (full / simplified per jurisdiction) issued at payment capture, for orders, renewals and usage charges
+- ✅ Credit notes for refunds / RMA referencing the original invoice; issued documents immutable
+- ✅ Gapless, chronological numbering per series (seller × establishment × document type × year) — distinct from the gap-tolerant order number
+- ✅ Jurisdiction profiles: EU-BASE (VAT Directive common rule set) + Australia + generic; home Member State profile configured after company registration; readiness gate per storefront
+- ✅ PDF delivery via account page / signed link; 10-year WORM retention
+- ❌ Structured e-invoicing / clearance (Peppol, SdI, KSeF, FR PA, VeriFactu) — phase 3
+- ❌ Reverse charge / VIES B2B — phase 2;  ❌ marketplace / deemed-supplier model
-- ❌ Stripe Tax / OSS / multi-jurisdiction tax logic
+- ❌ Stripe Tax / multi-jurisdiction tax engine (an OSS-aware `ITaxStrategy` is a prerequisite of EU cross-border invoicing — ADR-0063 P1b)

--- docs/prd/3commerce/06-architecture.md
+| **Invoicing** | customer tax documents (invoices, credit notes), numbering series, jurisdiction profiles, rendered artifacts | issue on capture/settlement/refund, render + deliver, register export, reconciliation to ledger |

--- docs/prd/3commerce/11-success-criteria.md
+- ⬜ **FR-14** Every confirmed order / settled renewal / usage charge produces exactly one tax document at capture.
+- ⬜ **FR-15** Every completed refund produces one credit note referencing the original invoice, never exceeding the remainder; chargebacks flag, never auto-credit.
+- ⬜ **FR-16** Shoppers download their tax documents; operators search/re-send/export; series/profile changes audited.
+- ⬜ **FR-17** A storefront cannot go live in an invoice-requiring jurisdiction without seller tax ID + profile.
+- ⬜ **NFR-9 (Gapless numbering)** unique, gapless, chronological per series under concurrency/redelivery/crash.
+- ⬜ **NFR-10 (Immutability & retention)** issued content immutable (DB-enforced), hash-chained, retained per profile (default 10 y).
+- ⬜ **NFR-11 (Reconciliation)** invoice/credit totals reconcile to ledger and tax accounts per storefront/currency/day.

--- docs/prd/3commerce/13-future-considerations.md
+| **Structured e-invoicing & clearance** (Peppol BIS 3.0, Factur-X/ZUGFeRD, SdI, KSeF, FR PA, VeriFactu) | Selling B2B in BE/PL/FR/DE, any sale in IT, ES VeriFactu (2027) — ADR-0063 phase 3 |
+| **ViDA digital reporting (intra-EU B2B)** | 1 Jul 2030 |
+| **Marketplace / deemed-supplier invoicing** | Only with the marketplace pivot |

--- docs/prd/3commerce/15-appendix.md
+| 21 | **Customer tax invoices** in a dedicated Invoicing service; gapless per-series numbering allocated in the issuing transaction; credit notes only; data-driven jurisdiction profiles (ADR-0063) | Order number as invoice number; DB sequences; invoices in Xero; full outsourcing | EU Art. 226/219/233; FR/ES strict numbering; any-jurisdiction by configuration |
+Glossary: **Credit note** — amending document referencing an issued invoice (EU Art. 219). **OSS/IOSS** — EU One-Stop-Shop schemes. **VIES** — EU VAT-ID validation service. **EN 16931** — EU semantic e-invoice standard.
```

---

## 15. Consequences

* **Positive:** a compliant, auditable customer document, aligned with the ledger by a nightly
  reconciliation identity. Any jurisdiction is reachable by configuration. The data model and hash chain
  are ready for e-invoicing and VeriFactu without a remodel.
* **Costs:** a 14th service (§ 6.1); new contract fields; a new PDF dependency; an eventual FX feed
  (display only); phase-0 changes in Ordering and Payments that are worth doing on their own. P4 fixes an
  existing output-tax under-report on renewals.
* **Risks:** wrong tax facts cannot be fixed by the invoice (P1/P1b first). Jurisdiction timetables move
  (re-verify the research doc before each phase). Legal labels in six languages need professional review.
  Clearance mandates (IT, PL, BE, FR, DE, ES) gate B2B in those countries until phase 3, and the readiness
  gate enforces that.

---

## 16. Implementation notes

### P2 — seller tax identity (2026-10-27, `feature/tax-seller-identity`, tracker `tax_p2_seller_identity`)

* **Entity.** `EntityIdentifierType` appends `EuVat 5 · GbVrn 6 · NzGst 7 · CaBn 8 · ChUid 9 · UsStateSalesTaxId 10`
  (mirrored by the contract enum `BuildingBlocks.Contracts.Tax.TaxIdScheme`, value parity pinned by a unit test — the
  scheme vocabulary P3 reuses). `TaxIdentifierFormats` validates **format only** per scheme: EU VAT by Member-State
  prefix (all 27 + `XI`; Greece is `EL`, `GR` refused) and national shape; UK VRN `GB` + 9/12 digits or GD/HA; NZ
  GST 8–9 digits; CA BN 9 digits (+ program account); CH UID `CHE` + 9 digits (+ MWST/TVA/IVA); a US state ID needs
  its state as the jurisdiction (`US-CA`). Identifiers carry a `Jurisdiction` (ISO country + optional ISO 3166-2
  region); identifiers stored before P2 derive it from the scheme. Errors are 400 problem+json keyed `Value` or
  `Jurisdiction` (`DomainRuleException.Field`).
* **Deviation — ABN checksum.** § 9.1 says "ABN checksum kept", but `develop` never had an ABN checksum (ABN/ACN
  were digits-only length checks, and dev seeds/tests use non-checksummed ABNs). P2 keeps that rule unchanged; an
  ABN mod-89 checksum is a separate, data-migrating decision.
* **Seller of record + establishments.** The seller is the entity with the active `TenantOwner` profile —
  `POST /entities/{id}/seller` designates it, exactly one per tenant (a per-tenant advisory lock inside the request
  transaction serializes concurrent designations; 409 for a second). `SellerEstablishment` (new table
  `entity."SellerEstablishments"`, FORCE RLS with the parent-join policy of the other entity children) = immutable
  slug `Key` (storefronts bind to it), label, address, `Jurisdiction` (defaults to the address country, may only
  narrow to a region of it). `GET /entities/seller`, `POST|PUT /entities/{id}/establishments`. No delete in P2.
* **`LegalEntityChanged`** (`Contracts/Entity`): a full snapshot (names, tax IDs with scheme/jurisdiction/verified,
  establishments, registered office, `OccurredAt`), published only for the seller entity on designate, name update,
  identifier add/verify, registered-office add, establishment add/update — through the bus outbox in the same unit
  of work as the mutation's audit entry. No consumer yet (Invoicing, phase 1).
* **Catalog.** `Storefront.TaxJurisdiction` (`""` | `CC` | `CC-RRR`) + `SellerEstablishmentKey`; create/update accept
  them (null keeps, `""` clears; 400 keyed by the field), `DuplicateFrom` clones them (the duplicate endpoint and
  thus the ADR-0060 `StorefrontDuplicated` backfill are unchanged — the copy's own `StorefrontConfigChanged` carries
  them). `StorefrontConfigChanged` appends `TaxJurisdiction = ""`, `SellerEstablishmentKey = ""`; Ordering projects
  both onto `StorefrontTaxCopy` (no checkout behaviour change). Catalog does not validate that the key exists in
  Entity (no cross-service read; the admin UI offers only the seller's keys) — the phase-1 readiness gate owns that.
* **Admin.** Entities → Details: the new schemes + jurisdiction on identifiers and a "Seller of record" section
  (designate, establishments add/edit). Commerce ops → Manage: tax jurisdiction + seller-establishment picker. All
  fields carry localized tooltips (six languages).
* **Tests.** Unit: `TaxIdentifierFormatTests`, `SellerEstablishmentTests`, `LegalEntityChangedTests`,
  `StorefrontTaxIdentityTests`, `StorefrontConfigBackCompatTests` (pre-P2 JSON → defaults). Integration:
  `SellerIdentityEndpointTests`, `SellerEstablishmentRlsTests` (real migrations as a non-superuser owner),
  `StorefrontTaxIdentityAdminTests`. Browser: `e2e-admin/seller-identity.spec.ts`. `e2e-verify.sh` A3q + A6i.

### P1b — EU OSS-aware tax strategy (2026-10-28, `feature/eu-oss-tax-strategy`, tracker `tax_p1b_eu_oss_tax_strategy`)

* **The strategy.** `EuOssTaxStrategy` (`Ordering/Domain/EuVat.cs`) behind the P1 seam. `TaxStrategySelector.For`
  picks it when the storefront's `TaxJurisdiction` (P2) names an EU Member State (`DE`, `DE-BY`; `EL` is read as
  `GR`). That country is the seller's **home Member State**: it is configuration (the storefront binding), never a
  constant, as § 12 D2 requires. Every other storefront — non-EU or unconfigured — keeps
  `StorefrontRateTaxStrategy`, byte-identical (10,000 seeded random carts compare the breakdowns field by field).
* **What it charges.**
  * A domestic sale, a sale to a non-EU destination, and a cross-border EU sale while the seller is at or under the
    threshold: the **home rate**. This is the storefront's own configured rate, so these sales are byte-identical to
    P1 and to the rate Payments uses for renewals. Scheme `Domestic`.
  * A cross-border EU sale when the seller is **strictly over EUR 10,000** in the preceding calendar year, or when the
    current year plus this sale goes over it (the crossing sale is itself OSS, Art. 59c): the **destination** Member
    State's rate in force on the supply date. Scheme `Oss`.
  * Inclusive storefronts **extract** that rate from the unchanged shelf price; exclusive ones add it (ADR-0038).
    Ship-rule exemptions (ADR-0050) stay exempt. Export zero-rating to non-EU destinations is left to those ship
    rules, as today.
* **Decision — where the threshold turnover comes from.** The source of truth is Ordering's **confirmed orders**.
  The running total is an append-only table, `ordering."EuDistanceSales"`, with one row per confirmed intra-EU
  distance sale: an order whose recorded `TaxJurisdiction` is an EU state and whose `TaxDestinationCountry` is
  another one. The row is keyed by `OrderId` and written by `OrderStatusConsumer` **in the same transaction** as the
  `Order` insert, so a sale that rolls back never counts and a redelivery never counts twice.
  * **Total.** The total per tenant per UTC calendar year is `Σ NetMinor` over the rows, served by the index
    `(TenantId, Year, Currency) INCLUDE (NetMinor)`. The tenant is the seller (D1).
  * **Why not a counter row.** It would be a hot row inside the consumer's REPEATABLE READ transaction, which is the
    `OrderNumberAllocator` 40001 lesson. Committing a counter separately would double-count on a retry.
  * **What a sale counts.** Its value **net of VAT** (Σ line bases + the shipping base). Only `EUR` rows count until
    an FX source exists (D13).
  * **When the total is read.** Checkout reads it only for an EU storefront selling into another Member State.
  * **Conservative choices, all in the never-under-collect direction:**
    * Refunds are not deducted.
    * B2B sales with VAT charged (P3, before reverse charge in phase 2) still count.
    * A non-EUR storefront cannot measure the EUR threshold, so its cross-border EU sales take the destination rate
      (opting into OSS below the threshold is lawful, Art. 59c(3)).
    * The threshold assumes a seller established in one Member State (Art. 59c(1)(a)). A seller with
      establishments in two states must not rely on it. The phase-1 readiness gate owns that check.
  * **In-flight tolerance.** Two checkouts priced at the same moment just below the threshold can both be home-taxed.
    The decision is made at pricing time from *confirmed* turnover. The window is one checkout's lifetime.
* **Decision — where the rate table lives.** In **code**: `EuVatRateTable.Current`.
  * **Why code.** A rate change is a change of law. It ships as a reviewed, tested change carrying its effective
    date, so it can land before the date and take effect exactly on it, with no deploy timing involved.
  * **Rows.** Each row is `(country, category, EffectiveFrom, bps)`. Standard rates for EU-27 start at the OSS start
    (2021-07-01), with every change since: EE 2024-01-01 22 % / 2025-07-01 24 %, LU 16 % for 2023, FI 2024-09-01
    25.5 %, SK 2025-01-01 23 %, RO 2025-08-01 21 %.
  * **Local midnight.** The effective date is the **local** date in the Member State. A sale at 21:30 UTC on 30 June
    2025 is a 1 July supply in Tallinn. Ordering's projection and the § 7.2 tenant overrides (DB, audited) can come
    later, when renewals need the table (§ 7.1).
  * **Not seeded: reduced rates.** No product carries a VAT category yet, and an unverified reduced rate in a money
    path is worse than none. A category without a row is taxed at the standard rate, which never under-collects.
  * **Already in place for categories.** `TaxLineInput.TaxCategoryCode` and per-group rounding: each rate group is
    rounded once, and shipping follows the highest-rated goods. That shipping rule is a conservative interim, until
    a split shipping line can be persisted for pro-rata apportionment.
  * **LAUNCH GATE — TEDB verification.** EU cross-border B2C must not go live until every row of
    `EuVatRateTable.Current` has been verified against the Commission's TEDB ("VAT rates applied in the Member
    States") as of the go-live date, and any change since is added with its effective date. The rates were entered
    from the cited national acts without a TEDB cross-check. Tracker: `eu_vat_rate_table_tedb_verification`. The
    check repeats on every change of law.
* **Decision — the destination country.** `TaxCalculationInput.DestinationCountry`. When it is null the strategy
  uses the **ship-to country**, the place of supply of dispatched goods, and checkout passes the ship-to today.
  Digital services are supplied where the customer belongs. When P3 lands bill-to, checkout can pass the bill-to
  country through this input for a digital-only cart, with no other seam change. Mixed carts need a per-line
  destination, which is a follow-up.
* **Facts persisted.** `CheckoutAttempt`/`Order` add `TaxJurisdiction` (the storefront's, as applied) and
  `TaxDestinationCountry`, both defaulting to `""`. The scheme lands in the existing `TaxScheme` column (`Oss`). The
  migration is `EuOssTaxStrategy`. No contract and no HTTP shape changed; P5 publishes the facts.
* **Known gaps (follow-ups, not P1b; tracker `tax_p1b_followups`):**
  * Renewals of an OSS-taxed subscription still use the storefront's rate in Payments (§ 7.1: the terms snapshot
    must carry the scheme and destination).
  * Special VAT territories (Canaries, Åland, the French DOM…) and Northern Ireland (`XI`) need a postcode or region
    rule.
  * Reduced-rate categories need a Catalog product VAT category projected to Ordering.
  * A cart that mixes physical goods and digital services needs a per-line destination: ship-to for the goods,
    bill-to for the services.
* **Tests.**
  * Unit: `EuOssTaxStrategyTests` covers the EU-27 fixture (typed independently of the table), the
    at/over/crossing threshold, the preceding year, non-EUR, an explicit destination, local-midnight effective dates
    and a future row, the unchanged inclusive gross, exclusive add-on, exemptions, categories plus 5,000 mixed-rate
    carts, selection, the non-EU byte-identity property, `EuDistanceSale` rows, and table validation. Mutating `>` to
    `>=`, or the local date to the UTC date, fails it.
  * Integration: `EuOssCheckoutTests` runs real checkouts as a fresh tenant each: under, over and crossing the
    threshold, domestic and unconfigured stores, with trial balance 0.
  * `e2e-verify.sh`: A3r and A4d.

### P3 — buyer identity at checkout (2026-10-28, `feature/checkout-bill-to-and-buyer-tax-id`, tracker `tax_p3_buyer_identity`)

* **Request.** `POST /checkout` appends `billingAddress` (the `shippingAddress` shape; absent = bill to the ship-to
  address) and `buyer` = `{ companyName, taxId?, taxIdScheme?, taxIdJurisdiction? }`. `taxIdScheme` is the shared
  contract enum `BuildingBlocks.Contracts.Tax.TaxIdScheme` from P2 and crosses HTTP as a number.
* **Validation.** It runs first in the handler, before the cart is loaded, because it depends only on the request.
  The company name is required (at most 200 characters). A tax ID needs its scheme, and a scheme needs its tax ID.
  The tax ID's format is checked per scheme by `Ordering.Domain.BuyerTaxIdFormats`, which is a **copy** of Entity's
  `TaxIdentifierFormats`. Ordering cannot reference Entity's domain, and BuildingBlocks holds no domain logic, so
  Ordering keeps its own copy. `BuyerTaxIdFormatParityTests` is a unit-lane test in the integration-test project,
  the only project that sees both. It runs a corpus of inputs through every scheme in both copies and fails on any
  disagreement. A one-character change to the NZ rule was used to confirm it fails. A US state sales-tax ID with
  no `taxIdJurisdiction` takes its state from the region of a US bill-to address. A refusal returns 400
  problem+json with `checkoutBlock` 17 `InvalidBuyerIdentity`, plus `field` (`buyer.companyName` /
  `buyer.taxId` / `buyer.taxIdScheme` / `buyer.taxIdJurisdiction`) and an `errors` dictionary keyed by that
  field. Nothing is booked, and the cart is kept.
* **Persisted.** `CheckoutAttempt` and `Order` gain `BillName/BillLine1/BillCity/BillRegion/BillPostcode/BillCountry`
  and `BuyerCompanyName/BuyerTaxId/BuyerTaxIdScheme/BuyerTaxIdJurisdiction`. All of them are nullable, and
  `ToOrder` copies them verbatim. Migration `CheckoutBillToAndBuyerIdentity` adds them; the bounded columns are
  also bounded in the domain, so an oversize value returns a 400 and never reaches the database. Every attempt
  written since P3 has a bill-to. Older rows read null and are not backfilled (D18). The tax ID is stored
  normalized, with the jurisdiction its scheme implies (an `EL` prefix gives `GR`; ABN gives `AU`).
* **Read back.** `GET /orders/{id}` and `GET /admin/orders/{id}` append `billingAddress` and `buyer`. The
  storefront's account order history shows both.
* **Amount unchanged.** Neither field reaches the tax seam or the charge. There is no reverse charge (D5), and
  `ITaxStrategy` is untouched. An integration test checks out the same cart with and without a block and gets
  byte-identical net, discount, shipping, tax and gross. `OrderConfirmed` is unchanged; P5 publishes `BillTo`
  and `Buyer`.
* **Storefront.** The billing-address section was already in the checkout form but was not posted. Its fields now
  post as `billingAddress`, and its copy now says the address is also the invoice address. A new "Business
  purchase" section (`components/checkout/BusinessPurchase.tsx`) has a checkbox, company name, tax ID, a scheme
  picker, and an issuing-state field shown for US state IDs. Its fields are controlled, so a refused checkout
  keeps what the shopper typed. A refusal is explained in the shopper's language, keyed by `field`
  (`checkout.business.errors.*`), and the field is marked `aria-invalid`. Every label and tooltip exists in all
  six languages. A member's saved addresses (Identity) feed the billing-address picker as before.
* **Deferred.** The § 9.1 scope line "the account profile can store a default business identity" is not in this
  task. It needs Identity profile columns, an endpoint, and an account UI, and it is not in P3's acceptance
  criteria. It is tracked as `tax_p3b_profile_business_identity`.
* **Tests.** Unit: `BuyerIdentityTests` (bill-to default, per-scheme accept/refuse naming the request field,
  bounds, `ToOrder` copy, request resolution) and `BuyerTaxIdFormatParityTests`. Integration:
  `BuyerIdentityCheckoutTests` covers a guest and a member, each with and without a block, an unchanged charge,
  a member reading the block back with a numeric scheme, and five refusals returning 400 with nothing booked.
  Browser: `e2e/checkout-business-purchase.spec.ts` (`capture()`). `e2e-verify.sh` A3s + A6j.

### P4b — settled-charge events (2026-10-28, `feature/settled-charge-events`, tracker `tax_p4b_settled_charge_events`)

* **Contracts.** `SubscriptionRenewalSettled` and `UsageChargeSettled` (`Contracts/Payments`), each with `Lines` of
  `SettledChargeLine { Description, Quantity, AmountMinor (shelf), TaxCategory, TaxRateBps, TaxableBaseMinor (net),
  TaxMinor, ExemptionReason }` plus `NetMinor`/`TaxMinor`/`GrossMinor`, `TaxInclusive`, `TaxScheme`, `Currency`,
  `StorefrontId`, `TenantId`, the customer, the charge reference (`renew-{id}-{seq}` / the usage reference = the ledger
  reference) and `SettledAt`; a renewal also carries `Sequence`, `PeriodStart`/`PeriodEnd`, the order/product/variant, a
  usage charge `Kind` (Overage / AutoLoad), `Meter`, `Quantity`, `UnitPriceMinor` and the settling `Attempt`. The tax-fact
  vocabulary (`TaxCategory`, `TaxExemptionReason`, `TaxScheme`) is published from **`Contracts.Tax.Facts`** — a namespace
  of its own because Ordering imports `Contracts.Tax` (TaxIdScheme) next to its domain enums of the same names — with
  Ordering's P1 values exactly (pinned by `SettledChargeContractTests`); P5 reuses it. `UsageOverageCharge` /
  `UsageAutoLoadCharge` append `UnitPriceMinor` (0 from an older publisher; Payments derives it when it divides evenly).
* **The facts are the posted split.** `SettledChargeTax.For` maps the pinned `RecurringChargeTaxResult` to the line:
  inclusive → Standard, base = charge − contained tax; exclusive → Standard, base = price, tax added; destination-exempt →
  Exempt (rate 0, `DestinationShipRule`); 0 % / zero price → Standard at the store's rate, tax 0; **unknown rate →
  `NotRecorded` category and scheme**, no tax (exactly what was posted — never a guessed rate). The scheme is `Domestic`
  (Payments charges the storefront's own single rate). The pin now records the **rate** (`RecurringChargePin` appends
  `RateBps`), so a period retried or settled after a rate change reports the rate it was charged at; a split pinned before
  this change falls back to the store's projected rate.
* **Once, in the ledger's unit of work.** A renewal publishes from `SubscriptionService.RenewAndPostAsync` — reached once per
  `Subscription.Renew`, i.e. once per new `SubscriptionRenewal` row (unique per subscription + sequence) — through the bus
  outbox, so the event commits with that row and the `renew-{id}-{seq}` entry or not at all: off-session, on a dunning
  retry, on the #315 pay page, or later from the provider's success event (`SettleAttemptAsync`, idempotent on the
  attempt's `SettledAt`). A usage charge publishes from `UsageChargeBilling.SettleAsync` before the posting's
  `SaveChanges`; a reference already posted is never posted or announced again. Failed / RequiresAction / Cancelled never
  reach either point.
* **With the refund path (#318).** Both events carry the settling `PaymentIntentId` — the intent of the `Payment` row
  (`ForSettledCharge`) that the same unit of work records for the charge, so an invoice and a later credit note find the
  same charge. No consumer issues documents yet (Invoicing, phase 1); Usage consumes `UsageChargeSettled` (below).
* **Usage charge failure feedback, delivered on the same branch** (`usage_charge_failure_feedback`, owner decision
  2026-10-27 — see the ADR-0057 follow-up): Payments now records each usage charge (`UsageCharge` + `UsageChargeAttempt`,
  migration `UsageChargeDunning`), duns it on the shared `DunningSchedule`, and publishes `UsageChargeFailed`.
* **Tests.** `SettledChargeEventTests`, `UsageChargeDunningTests` (Payments), `UsageChargeFeedbackTests` (Usage),
  `SettledChargeContractTests` (unit lane) — e2e-verify **A3u**; `ConsumerEndpointNameTests` (A3d) asserts the new queues.
