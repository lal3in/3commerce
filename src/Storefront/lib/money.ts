// Money is always integer minor units + ISO 4217 code (AGENTS.md invariant).
// Never divide by 100 inline or hardcode a symbol — go through here (components.md §5).
export function formatMoney(minorUnits: number, currency: string): string {
  // Explicit locale: deterministic server/client output (no hydration drift) and unambiguous
  // symbols — en-US renders AUD as "A$", USD as "$", EUR as "€" (rev_6 / F6).
  return new Intl.NumberFormat("en-US", {
    style: "currency",
    currency,
  }).format(minorUnits / 100);
}
