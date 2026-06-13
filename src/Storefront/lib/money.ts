// Money is always integer minor units + ISO 4217 code (AGENTS.md invariant).
// Never divide by 100 inline or hardcode a symbol — go through here (components.md §5).
export function formatMoney(minorUnits: number, currency: string): string {
  return new Intl.NumberFormat(undefined, {
    style: "currency",
    currency,
  }).format(minorUnits / 100);
}
