/**
 * The tax-ID scheme vocabulary (ADR-0063 P2/P3) — mirrors `BuildingBlocks.Contracts.Tax.TaxIdScheme`. Enums cross
 * HTTP as NUMBERS (platform invariant), so these values are the wire contract: append only, never renumber.
 *
 * Its own module (like `promotion-basis.ts`) because the CLIENT checkout form needs the values, and importing them
 * from `gateway.ts` would drag `next/headers` into the browser bundle.
 */
export const TaxIdScheme = {
  Abn: 1,
  Acn: 2,
  Gst: 3,
  OtherTaxRegistration: 4,
  EuVat: 5,
  GbVrn: 6,
  NzGst: 7,
  CaBn: 8,
  ChUid: 9,
  UsStateSalesTaxId: 10,
} as const;
export type TaxIdScheme = (typeof TaxIdScheme)[keyof typeof TaxIdScheme];

/** The schemes a buyer can pick at checkout, with their `checkout.business.schemes.*` label keys, in display order. */
export const TAX_ID_SCHEME_OPTIONS: { value: TaxIdScheme; labelKey: string }[] = [
  { value: TaxIdScheme.EuVat, labelKey: "euVat" },
  { value: TaxIdScheme.Abn, labelKey: "abn" },
  { value: TaxIdScheme.GbVrn, labelKey: "gbVrn" },
  { value: TaxIdScheme.NzGst, labelKey: "nzGst" },
  { value: TaxIdScheme.CaBn, labelKey: "caBn" },
  { value: TaxIdScheme.ChUid, labelKey: "chUid" },
  { value: TaxIdScheme.UsStateSalesTaxId, labelKey: "usStateSalesTaxId" },
  { value: TaxIdScheme.Acn, labelKey: "acn" },
  { value: TaxIdScheme.Gst, labelKey: "gst" },
  { value: TaxIdScheme.OtherTaxRegistration, labelKey: "other" },
];

/** The `checkout.business.schemes.*` label key of a scheme value (for read-back, e.g. the order detail). */
export function taxIdSchemeLabelKey(value: number | null | undefined): string | null {
  return TAX_ID_SCHEME_OPTIONS.find((option) => option.value === value)?.labelKey ?? null;
}

/**
 * The request field a refused business block names (Ordering's problem+json `field`, ADR-0063 P3) → the
 * `checkout.business.errors.*` key that explains it in the shopper's language. Null for any other field.
 */
export function businessFieldErrorKey(field: string | null | undefined): string | null {
  switch (field) {
    case "buyer.companyName":
      return "companyName";
    case "buyer.taxId":
      return "taxId";
    case "buyer.taxIdScheme":
      return "taxIdScheme";
    case "buyer.taxIdJurisdiction":
      return "taxIdJurisdiction";
    default:
      return null;
  }
}
