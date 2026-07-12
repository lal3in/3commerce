// Catalog product types (Catalog ProductType enum — numeric on the wire, ADR-0028). The billing
// mechanics (recurring, metered) live on the Offer; this is the browsable label a shopper filters by.

export type ProductTypeInfo = { value: number; label: string; badge: string };

export const PRODUCT_TYPES: ProductTypeInfo[] = [
  { value: 1, label: "Physical", badge: "Physical" },
  { value: 2, label: "Digital", badge: "Digital" },
  { value: 3, label: "Service", badge: "Service" },
  { value: 4, label: "Bundle", badge: "Bundle" },
  { value: 5, label: "Subscription", badge: "Subscription" },
  { value: 6, label: "Usage-based", badge: "Pay as you go" },
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
