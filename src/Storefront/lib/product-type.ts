// Catalog product types (Catalog ProductType enum — numeric on the wire, ADR-0028). The billing
// mechanics (recurring, metered) live on the Offer; this is the browsable label a shopper filters by.
//
// i18n_1: the display strings live in the message catalogs (`productTypes.*`), not here — this module
// only maps the numeric enum to catalog KEYS, so a new language translates the type chips for free.

export type ProductTypeInfo = {
  value: number;
  /** Key into the `productTypes` namespace for the filter-chip label. */
  labelKey: string;
  /** Key into the `productTypes` namespace for the card/PDP badge (differs for usage-based). */
  badgeKey: string;
};

export const PRODUCT_TYPES: ProductTypeInfo[] = [
  { value: 1, labelKey: "physical", badgeKey: "physical" },
  { value: 2, labelKey: "digital", badgeKey: "digital" },
  { value: 3, labelKey: "service", badgeKey: "service" },
  { value: 4, labelKey: "bundle", badgeKey: "bundle" },
  { value: 5, labelKey: "subscription", badgeKey: "subscription" },
  { value: 6, labelKey: "usageBased", badgeKey: "usageBasedBadge" },
];

const BY_VALUE = new Map(PRODUCT_TYPES.map((t) => [t.value, t]));

// Legacy/typeless rows persist as 0 → treat as Physical.
export function productTypeInfo(value: number): ProductTypeInfo {
  return BY_VALUE.get(value) ?? PRODUCT_TYPES[0];
}

// Tailwind classes per type so the badge is visually distinguishable at a glance.
export function productTypeClasses(value: number): string {
  switch (value) {
    case 2:
      return "bg-sky-100 text-sky-800";
    case 3:
      return "bg-amber-100 text-amber-800";
    case 4:
      return "bg-purple-100 text-purple-800";
    case 5:
      return "bg-emerald-100 text-emerald-800";
    case 6:
      return "bg-rose-100 text-rose-800";
    default:
      return "bg-neutral-100 text-neutral-700";
  }
}
