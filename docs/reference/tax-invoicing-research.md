# Customer tax invoices & credit notes — jurisdiction research

> **Status:** research input to [ADR-0063](../adr/0063-customer-tax-invoices-and-credit-notes.md) (Proposed).
> **Compiled:** 2026-10-11. **This is engineering research, not legal or tax advice.** Before go-live in
> any jurisdiction, the tenant's tax adviser must confirm the rule set we configure for it. Dates move:
> national e-invoicing timetables in particular have been postponed repeatedly. Re-check each row before
> you rely on it.
>
> **Confidence key:** **V** = checked against a primary or official source while this was compiled (link
> given). **S** = taken from a reputable secondary source (Big-4 alert, e-invoicing vendor, chamber of
> commerce) and should be checked against the primary text. **K** = from background knowledge, with the
> authoritative link given for whoever checks it.

---

## 1. Why this exists

3commerce has **no customer tax-invoice feature today**. An order gets a per-storefront public reference
number (`OrderNumberSequence`, now being made gap-tolerant in a parallel fix) and a confirmation email.
There is no `Invoice` or `CreditNote` type, no invoice numbering series, no per-rate tax breakdown on an
order, no buyer tax-ID capture, and no stored, immutable document.

Decision #17 in the PRD decision log (`docs/prd/3commerce/15-appendix.md`) rejected *per-order invoices in
Xero*. That was an **accounting** decision about bookkeeping noise and API rate limits, and it stands. It
does not address **customer tax invoices**, which are a legal document the seller owes the buyer. This file
collects the legal requirements those documents must meet.

---

## 2. European Union — VAT Directive 2006/112/EC

Primary text: [EUR-Lex consolidated VAT Directive](https://eur-lex.europa.eu/legal-content/EN/TXT/?uri=CELEX:02006L0112-20250414).
The article text below was checked against the
[legislation.gov.uk copy of the Directive](https://www.legislation.gov.uk/eudr/2006/112/contents). That
copy is frozen at 31 Dec 2020, but the invoicing articles quoted here were not changed by the 2021
e-commerce package. The only exception is Art. 220(1)(2), shown with its post-2021 wording. EUR-Lex
blocks automated fetching, so read the live consolidated text before relying on any of this.

| Topic | Rule | Article | Conf. |
|---|---|---|---|
| **Which rules apply** | The invoicing rules are those of the Member State where the supply takes place. There are exceptions: the supplier's Member State applies where the customer is liable to the tax (reverse charge), and the Member State of identification applies for OSS/IOSS supplies. | [Art. 219a](https://www.legislation.gov.uk/eudr/2006/112/article/219a) (as amended by Dir. 2017/2455) | V/S |
| **When an invoice is mandatory** | (1) Supplies to taxable persons or non-taxable legal persons. (2) Distance sales under Art. 33, **except where the supplier uses the Union OSS scheme**. (3) Intra-EU supplies of goods (Art. 138). (4) Payments on account. Beyond these, Member States *may* require invoices for other supplies, including B2C (Art. 221). | [Art. 220](https://www.legislation.gov.uk/eudr/2006/112/article/220), [221](https://www.legislation.gov.uk/eudr/2006/112/article/221); [vatupdate explainer](https://www.vatupdate.com/2022/03/01/eu-vat-directive-2006-112-ec-explained-art-220-invoicing-for-which-transactions-should-an-invoice-be-issued/); [EC e-commerce explanatory notes](https://vat-one-stop-shop.ec.europa.eu/system/files/2021-07/vatecommerceexplanatory_notes_28102020_en.pdf) | S |
| **Amending documents** | "Any document or message that amends and refers specifically and unambiguously to the initial invoice shall be treated as an invoice." This is the legal basis for **credit notes**: they reference the original invoice rather than editing it. | [Art. 219](https://www.legislation.gov.uk/eudr/2006/112/article/219) | V |
| **Simplified invoice** | Member States must allow a simplified invoice where the amount is **≤ EUR 100**, or where the document is an amending document under Art. 219. It is **not** allowed for intra-EU supplies, for Art. 33 distance sales, or for reverse-charge supplies by a non-established supplier. Art. 238 lets Member States raise the limit to EUR 400. Required content: date, supplier identity, type of goods, VAT amount (or the data to compute it) and, for an amending document, a reference to the original. | [Art. 220a](https://www.legislation.gov.uk/eudr/2006/112/article/220a), [226b](https://www.legislation.gov.uk/eudr/2006/112/article/226b), [238](https://www.legislation.gov.uk/eudr/2006/112/article/238) | V |
| **Full invoice content** | (1) Date of issue. **(2) "A sequential number, based on one or more series, which uniquely identifies the invoice."** (3) Supplier VAT ID. (4) Customer VAT ID for reverse charge or intra-EU supplies. (5) Full names and addresses of supplier and customer. (6) Quantity and nature of the goods. (7) Date of supply or payment on account, where it differs from the issue date. (7a) "Cash accounting", where it applies. (8) Taxable amount per rate or exemption, unit price exclusive of VAT, and any discounts not already in the unit price. (9) VAT rate. (10) VAT amount payable. (10a) "Self-billing". (11) Exemption reference. (11a) "Reverse charge". (12) Details of new means of transport. (13)/(14) Margin-scheme notations. | [Art. 226](https://www.legislation.gov.uk/eudr/2006/112/article/226) | V |
| **Currency** | "The amounts which appear on the invoice may be expressed in any currency, provided that **the amount of VAT payable … is expressed in the national currency of the Member State**, using the conversion rate mechanism provided for in Article 91." Art. 91 uses the latest selling rate or the ECB rate. | [Art. 230](https://www.legislation.gov.uk/eudr/2006/112/article/230), [91](https://www.legislation.gov.uk/eudr/2006/112/article/91) | V |
| **Language** | Member States may require a translation of invoices for control purposes. Some, such as France and Spain, require invoices in the national language, or expect it in practice. | [Art. 248a](https://www.legislation.gov.uk/eudr/2006/112/article/248a) | V/K |
| **Time limits** | Intra-EU supplies of goods (Art. 138) and B2B reverse-charge services (Art. 196): **no later than the 15th day of the month following the chargeable event**. In all other cases, Member States may set their own limits. | [Art. 222](https://www.legislation.gov.uk/eudr/2006/112/article/222) | V |
| **Integrity** | The authenticity of origin, integrity of content and legibility of an invoice must be ensured from issue until the end of its storage period. This can be done through business controls that create a reliable audit trail, a qualified e-signature, or EDI. | [Art. 233](https://www.legislation.gov.uk/eudr/2006/112/article/233) | V |
| **Storage** | Each Member State sets its own storage period ([Art. 247](https://www.legislation.gov.uk/eudr/2006/112/article/247)). Examples (K/S): **IT 10 y; BE 10 y; ES 4 y (tax) / 6 y (commercial); FR 6 y (tax) / 10 y (commercial); DE 8 y** (accounting vouchers since 2025, under BEG IV); **PL 5 y; NL 7 y; AT 7 y**. Authorities must get electronic access (Art. 249). | Arts. 244–249 | S/K |
| **Chargeable event** | For goods, the chargeable event is the supply (Art. 63). A **payment received before the supply** makes VAT chargeable on the amount received (Art. 65); this is the "payment on account" case, which needs its own invoice (Art. 220(1)(4)). | [Art. 63](https://www.legislation.gov.uk/eudr/2006/112/article/63), [65](https://www.legislation.gov.uk/eudr/2006/112/article/65) | V |
| **Reverse charge wording** | The invoice must say **"Reverse charge"** and show the customer's VAT ID. | Art. 226(4), (11a) | V |
| **OSS / IOSS** | Above the **EUR 10,000** EU-wide threshold for intra-EU distance sales, VAT is due in the customer's Member State, declared through the Union OSS. IOSS covers imported consignments of **≤ EUR 150**. When the Union OSS is used, an invoice is **not** mandatory for Art. 33 distance sales; if one is issued, the invoicing rules of the **Member State of identification** apply. **Electronic interfaces** that facilitate imports of ≤ EUR 150, or intra-EU sales by non-EU sellers, are **deemed suppliers** (Art. 14a). | Arts. 14a, 33, 59c, 369a–369x; [EC OSS portal](https://vat-one-stop-shop.ec.europa.eu/) | S |

### 2.1 Gaplessness: what the EU actually requires

The Directive requires a **sequential** number that **uniquely** identifies the invoice, and it allows
**one or more series**. It does **not** say "gapless". Member States read it differently:

| Member State | Reading | Source | Conf. |
|---|---|---|---|
| **Germany** | Numbers must be unique and follow a traceable system. **"Eine lückenlose Abfolge der ausgestellten Rechnungsnummern ist nicht zwingend"** — a gapless sequence is not mandatory. Any number of separate series by time, place or organisation is allowed. | §14(4) Nr. 4 UStG; UStAE 14.5 Abs. 10 ([IHK Frankfurt summary](https://www.frankfurt-main.ihk.de/recht/uebersicht-alle-rechtsthemen/steuerrecht/umsatzsteuer-national/rechnungen-pflichtangaben-in-der-rechnung-5195692)) | S |
| **France** | Numbering must be **chronological and continuous, without gaps (sans rupture)**. Several parallel series are allowed if justified, for example per establishment. | CGI art. 242 nonies A; [BOFiP BOI-TVA-DECLA-30-20-20](https://bofip.impots.gouv.fr/bofip/1546-PGP.html) | K |
| **Spain** | **Correlative** numbering inside each series. Separate series are allowed, and **rectifying invoices (facturas rectificativas) must be in their own series**. | [RD 1619/2012](https://www.boe.es/buscar/act.php?id=BOE-A-2012-14696) arts. 6.1.a, 15 | K |
| **Italy** | Progressive numbering, unique per series. Invoices are cleared through SdI. | DPR 633/1972 art. 21 | K |
| **Poland** | Sequential and unique per series. The KSeF number is assigned on clearance. | VAT Act art. 106e | K |

**Design consequence:** treat the strictest common reading — **gapless, chronological, unique per series,
with no reuse** — as the platform invariant. That satisfies every Member State at once. Series are allowed
everywhere, and Spain *requires* a separate series for corrections, so series are first-class.

### 2.2 ViDA — VAT in the Digital Age (Council Directive (EU) 2025/516)

| Milestone | Date | Source | Conf. |
|---|---|---|---|
| Adopted | 11 Mar 2025 | [eucrim](https://eucrim.eu/news/new-legislation-vat-in-the-digital-age/) | V |
| Published in the OJ | 25 Mar 2025 | [EUR-Lex 32025L0516](https://eur-lex.europa.eu/legal-content/EN/TXT/?uri=CELEX%3A32025L0516) | V |
| Entry into force. Member States may **mandate domestic e-invoicing without an EU derogation**. | 14 Apr 2025 | [EC ViDA page](https://taxation-customs.ec.europa.eu/taxation/vat/vat-digital-age-vida_en) | V |
| OSS/IOSS clarifications | 1 Jan 2027 | EC ViDA page | V |
| Platform deemed supplier for short-term accommodation and road passenger transport (Member States may defer to 1 Jan 2030). **Not physical goods.** | 1 Jul 2028 | EC ViDA page | V |
| Single VAT registration; mandatory reverse charge for non-established suppliers | 1 Jul 2028 | EC ViDA page | V |
| **Digital reporting for cross-border (intra-EU) B2B.** EN 16931 structured e-invoice is the default. Invoice due **within 10 days** of the chargeable event. Summary invoices are limited to the same month. | 1 Jul 2030 | EC ViDA page; [vatcalc](https://www.vatcalc.com/eu/eu-2028-digital-reporting-requirements-drr-e-invoice/); [Grant Thornton](https://www.grantthornton.ie/insights/factsheets/vida-digital-reporting-e-invoicing/) | V/S |
| Existing national real-time reporting systems must converge on the EU model | 1 Jan 2035 | EC ViDA page | V |

### 2.3 National e-invoicing / fiscalisation mandates (live or announced)

| Country | Scope | Dates | Format / channel | Source | Conf. |
|---|---|---|---|---|---|
| **Italy** | B2B, B2C and B2G invoices cleared through **SdI**. B2C retail receipts go through the *documento commerciale* (telematic register). | Since 1 Jan 2019 | FatturaPA XML (EN 16931-aligned); B2C uses codice destinatario `0000000` | [EC eInvoicing in Italy](https://ec.europa.eu/digital-building-blocks/sites/spaces/DIGITAL/pages/467108890/eInvoicing+in+Italy) | S |
| **France** | Every VAT-registered business must be able to **receive** e-invoices. Issuance, plus **e-reporting** of B2C and international sales, is phased by size. | Receive: all, **1 Sep 2026**. Issue + e-report: large/ETI **1 Sep 2026**; SME/micro **1 Sep 2027**. | Through a *Plateforme Agréée* (PA); Factur-X, UBL or CII | [impots.gouv.fr](https://www.impots.gouv.fr/professionnel/je-passe-la-facturation-electronique); [Pennylane calendar](https://www.pennylane.com/fr/fiches-pratiques/facture-electronique/facturation-electronique-dates-cles-et-calendrier) | V/S |
| **Poland** | **KSeF** clearance for B2B | Turnover > PLN 200m: **1 Feb 2026**. Others: **1 Apr 2026**. Sales < PLN 10k/month: to 31 Dec 2026. 2026 is a penalty-free year. | FA(3) XML via KSeF | [EY alert](https://www.ey.com/en_gl/technical/tax-alerts/poland-signs-into-law-mandatory-national-e-invoicing-system); [Sovos](https://sovos.com/blog/vat/poland-e-invoicing-via-ksef/) | S |
| **Germany** | Domestic **B2B** only; B2C is out of scope | Receive: **1 Jan 2025**. Issue: turnover > €800k **1 Jan 2027**; all **1 Jan 2028**. | EN 16931 (XRechnung, ZUGFeRD ≥ 2.1) | Wachstumschancengesetz, §14 UStG; [ecosio](https://ecosio.com/en/blog/germany-e-invoicing-explained/) | S |
| **Belgium** | Domestic **B2B** | **1 Jan 2026** (penalties from Apr 2026). E-reporting **1 Jan 2028**. | Peppol BIS Billing 3.0 | [EDICOM](https://edicomgroup.com/blog/belgium-will-make-b2b-electronic-invoice-mandatory) | S |
| **Spain** | **VeriFactu**: certified invoicing-software requirements (hash chain, QR code, optional real-time submission), covering B2C too. **TicketBAI** in the Basque Country. A B2B e-invoicing mandate (Ley Crea y Crece) is still pending regulation. | VeriFactu postponed by **RDL 15/2025** to **1 Jan 2027** (corporate-tax payers) and **1 Jul 2027** (others) | AEAT XML records | [Garrigues](https://www.garrigues.com/es_ES/noticia/retrasa-entrada-vigor-verifactu) | S |
| **Portugal** | Certified software, ATCUD unique code + QR code on every invoice, SAF-T(PT) | Live; qualified e-signature on PDF invoices deferred to 2027+ | — | [Portal das Finanças](https://info.portaldasfinancas.gov.pt/) | K |
| **Hungary** | Real-time invoice reporting (RTIR / NAV Online Számla), including B2C | Live since 2021 | NAV XML | [NAV](https://onlineszamla.nav.gov.hu/) | K |
| **Greece** | myDATA e-books; B2B e-invoicing phased in | myDATA live; B2B mandate Feb–Oct 2026 | myDATA XML | [AADE](https://www.aade.gr/mydata) | K |
| **Romania** | RO e-Factura B2B (2024) and B2C (2025) | Live | CIUS-RO UBL | [ANAF](https://www.anaf.ro/) | K |
| **Croatia** | Fiscalisation 2.0: domestic B2B e-invoicing | 1 Jan 2026 | EN 16931 | Porezna uprava | K |

---

## 3. Outside the EU

| Jurisdiction | Must the seller issue? | Content & thresholds | Numbering | Time limit | Retention | Source | Conf. |
|---|---|---|---|---|---|---|---|
| **United Kingdom** | A **VAT invoice is required only for supplies to another VAT-registered person**; B2C is not required. | Full invoice: unique sequential number, time of supply, issue date, supplier name/address/VRN, customer name/address, description, quantity, unit price, rate, net per rate, VAT total. **Less-detailed invoice for ≤ £250** (including VAT). If the invoice is in a foreign currency, **the VAT must also be shown in sterling**. | Sequential, unique, one or more series | **30 days** from the tax point | **6 years** | [VAT Notice 700/21](https://www.gov.uk/guidance/record-keeping-for-vat-notice-70021); [VATREC5010](https://www.gov.uk/hmrc-internal-manuals/vat-trader-records/vatrec5010); [VATREC6010](https://www.gov.uk/hmrc-internal-manuals/vat-trader-records/vatrec6010); [CH15200](https://www.gov.uk/hmrc-internal-manuals/compliance-handbook/ch15200) | V/S |
| **UK — MTD for VAT** | Digital records and API filing for all VAT-registered businesses. This drives **digital links** from the invoice to the return, not the invoice format. | — | — | — | — | [MTD for VAT](https://www.gov.uk/guidance/find-software-thats-compatible-with-making-tax-digital-for-vat) | K |
| **Australia** | A **tax invoice must be issued within 28 days of a recipient's request**. There is no obligation for sales **≤ A$82.50** including GST (A$75 excluding). | **< A$1,000**: shown to be intended as a tax invoice, seller identity and **ABN**, issue date, description with quantity and price, **GST amount** (or a statement that the total includes GST when every item is taxable), and the extent each sale is taxable. **≥ A$1,000**: also the **buyer's identity or ABN**. A recipient-created tax invoice (RCTI) needs a written agreement. | Not prescribed; uniqueness is good practice | 28 days from request | **5 years** | [ATO — Tax invoices](https://www.ato.gov.au/businesses-and-organisations/gst-excise-and-indirect-taxes/gst/tax-invoices); GST Act s 29-70; [GSTR 2013/1](https://www.ato.gov.au/law/view/document?docid=GST%2FGSTR20131%2FNAT%2FATO%2F00001) | V/S |
| **Australia — offshore low-value goods** | An **electronic distribution platform** is the deemed supplier for low-value imported goods (≤ A$1,000) sold to consumers (Div 84-C). | — | — | — | — | GST Act Div 84-C | K |
| **New Zealand** | **Taxable supply information (TSI)** since 1 Apr 2023. It must be provided **within 28 days of a request** for supplies **> NZ$200**. | ≤ $200: none required. $200–$1,000: supplier name and GST number, date, description, consideration, GST amount or statement. **> $1,000**: also the **recipient's name and one identifier** (address, phone, email, NZBN, website …). | Not prescribed | 28 days from request | **7 years** | [IRD — How TSI works](https://www.ird.govt.nz/gst/tax-invoices-for-gst/how-tax-invoices-for-gst-work) | V/S |
| **Canada** | There is no general invoice mandate. A registrant **purchaser** needs specific information to claim input tax credits (ITCs). | **< C$100**: supplier name, date, total. **C$100–499.99**: also the GST/HST charged (or a statement that it is included), the **supplier's registration number**, and the status of each supply. **≥ C$500**: also the buyer's name, a description and the payment terms. Thresholds have applied since 20 Apr 2021. QST in Quebec is separate. | Not prescribed | — | **6 years** | [CRA — ITC documentation (8-4)](https://www.canada.ca/en/revenue-agency/services/forms-publications/publications/8-4/documentary-requirements-claiming-input-tax-credits.html); [CRA records](https://www.canada.ca/en/revenue-agency/services/tax/businesses/topics/gst-hst-businesses/calculate-prepare-report/gst-hst-records-keep.html) | V |
| **United States** | **No federal VAT and no federal invoice-numbering rule.** Sales tax is set by each state and locality. Receipts typically must state the tax separately. **Marketplace facilitator** laws in every sales-tax state make the marketplace collect and remit. | State-specific | None federal | — | Typically 3–4 years by state | [Streamlined Sales Tax](https://www.streamlinedsalestax.org/); state DOR sites | K |
| **Switzerland** | The invoice must carry the supplier's name, place and **UID/VAT no.**, the recipient's name and place, date or period, nature and extent of the supply, consideration, rate and tax amount. Receipts of **≤ CHF 400** may omit the recipient. Amounts in a foreign currency must be converted to CHF for reporting. | — | Not prescribed | — | **10 years** | [MWSTG Art. 26](https://www.fedlex.admin.ch/eli/cc/2009/615/de#art_26), Art. 70 | K |
| **Norway** | Sales documents under the Bookkeeping Regulation §5-1. **VOEC** scheme: foreign sellers and marketplaces collect VAT on low-value goods ≤ NOK 3,000. | — | Unique, consecutive | — | **5 years** | [Bokføringsforskriften](https://lovdata.no/dokument/SF/forskrift/2004-12-01-1558); [Skatteetaten VOEC](https://www.skatteetaten.no/en/business-and-organisation/vat-and-duties/vat/foreign/e-commerce-voec/) | K |
| **Rest of world** | The common pattern: a jurisdiction-specific threshold for a full versus simplified document; the **seller's tax ID**; a **unique** (often sequential) number; tax per rate; buyer identity above a threshold or for B2B; a retention period of **5–10 years**; and, increasingly, **clearance or real-time reporting** (e.g. Mexico CFDI, Brazil NF-e, India GST e-invoice/IRN, Saudi Arabia ZATCA FATOORA, Turkey e-Fatura, Malaysia MyInvois). | — | — | — | — | Country-specific | K |

---

## 4. Who issues the invoice (seller of record)

* **Default:** the seller is the **legal entity that makes the supply**. In 3commerce that is the
  **tenant**, which is one legal operating business (ADR-0023), recorded in the Entity service as the
  `TenantOwner` entity.
* **Dropship / supplier fulfilment (ADR-0028 offers):** the tenant buys from the supplier and resells to
  the shopper, so the tenant is still the seller. The supplier invoices the **tenant**; that is an accounts-payable
  document, out of scope here. Shipment by a third party does not change who made the supply.
* **Marketplace / agency model:** this would be a pivot. A supplier selling *in its own name* through the
  tenant's storefront, with the tenant acting as an agent or electronic interface, makes the **supplier**
  the seller of record. It can also trigger **deemed-supplier rules**: EU Art. 14a for imports ≤ €150 or for
  non-EU sellers into the EU; AU Div 84-C; NO VOEC; US marketplace-facilitator laws. The PRD lists
  "Marketplace capabilities" as a deliberate future pivot. **Flagged for the owner, not decided here.**
* **Platform operator (3commerce itself):** issues invoices only for its *own* SaaS fees to tenants. That
  is a separate, out-of-scope stream.

---

## 5. Requirements the platform must therefore satisfy (union of the above)

1. Issued documents are **immutable**. Corrections are made only by a referencing **credit note** (or
   debit note). Never edit or delete an issued invoice (EU Art. 219, 233; the UK; Spain's rectificativas).
2. **Sequential, unique numbering per series**, **gapless and chronological** (the strictest reading,
   France), with **series as a first-class concept**: per seller entity, optionally per establishment or
   storefront, per period, and per document type (Spain requires corrections in a separate series).
3. **Per-rate tax breakdown**: taxable amount, rate and tax per rate, with unit prices exclusive of tax and
   discounts shown. A single order-level `TaxMinor` is **not enough**.
4. **Seller identity**: legal name, address, tax registration number(s) (VAT ID, ABN, GST/HST no., UID).
   **Buyer identity**: name, address and **tax ID** for B2B, reverse charge, and above thresholds.
5. **Multi-currency**: the document may be in any currency, but **the VAT total must be shown in the local
   currency** where required (EU Art. 230: the Member State of the supply or, under OSS, of
   identification; UK: sterling). The exchange rate and its source must be recorded.
6. **Issue timing** configurable per jurisdiction: on supply, on payment on account, by the 15th of the
   following month (EU intra-B2B), within 30 days (UK), within 28 days of a request (AU/NZ), or within
   10 days (ViDA 2030).
7. **Retention** of the *rendered* document and its data for up to **10 years** (IT/BE/CH), with integrity
   evidence and authority access.
8. **Structured formats**: EN 16931 (UBL 2.1 / CII) as the canonical structured model, with Peppol BIS 3.0,
   XRechnung/ZUGFeRD/Factur-X, FatturaPA, KSeF FA(3) and VeriFactu as **adapters** behind a clearance
   seam. A human-readable PDF is still needed for B2C.
9. **Language**: the invoice language is configurable per storefront and jurisdiction, at least the
   national language where it is required.
10. **Thresholds** for simplified versus full documents, and for buyer identification, configurable per
    jurisdiction (EU €100/€400; UK £250; AU A$82.50 / A$1,000; NZ NZ$200 / NZ$1,000; CA C$100 / C$500;
    CH CHF 400).
